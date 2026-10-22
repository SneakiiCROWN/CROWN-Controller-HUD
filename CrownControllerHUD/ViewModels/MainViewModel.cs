using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IControllerManager _controllerManager;
        private readonly INotificationService _notificationService;
        private readonly ISettingsService _settingsService;
        private readonly ITrayService _trayService;
        private readonly IBatteryWarningPolicy _batteryWarnings;
        private readonly ILogger<MainViewModel> _logger;

        private ObservableCollection<ControllerViewModel> _controllers = new();
        private ControllerViewModel? _selectedController;
        private bool _isScanning = false;
        private string _statusText = "Initializing...";
        private int _connectedCount = 0;
        private bool _showDiagnostics = false;
        private ObservableCollection<ProviderDiagnosticInfo> _providerDiagnostics = new();
        private string _systemInfo = "";

        public ObservableCollection<ControllerViewModel> Controllers
        {
            get => _controllers;
            set => SetProperty(ref _controllers, value);
        }

        public ControllerViewModel? SelectedController
        {
            get => _selectedController;
            set => SetProperty(ref _selectedController, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => SetProperty(ref _isScanning, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public int ConnectedCount
        {
            get => _connectedCount;
            set => SetProperty(ref _connectedCount, value);
        }

        public bool ShowDiagnostics
        {
            get => _showDiagnostics;
            set => SetProperty(ref _showDiagnostics, value);
        }

        public ObservableCollection<ProviderDiagnosticInfo> ProviderDiagnostics
        {
            get => _providerDiagnostics;
            set => SetProperty(ref _providerDiagnostics, value);
        }

        public string SystemInfo
        {
            get => _systemInfo;
            set => SetProperty(ref _systemInfo, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand MinimizeToTrayCommand { get; }
        public ICommand TestNotificationCommand { get; }
        public ICommand ShowDiagnosticsCommand { get; }
        public ICommand OpenLogCommand { get; }
        public ICommand CopyDiagnosticsCommand { get; }

        public MainViewModel(
            IControllerManager controllerManager,
            INotificationService notificationService,
            ISettingsService settingsService,
            ITrayService trayService,
            IBatteryWarningPolicy batteryWarnings,
            ILogger<MainViewModel> logger)
        {
            _controllerManager = controllerManager;
            _notificationService = notificationService;
            _settingsService = settingsService;
            _trayService = trayService;
            _batteryWarnings = batteryWarnings;
            _logger = logger;

            RefreshCommand = new AsyncRelayCommand(async () => await RefreshAsync());
            SettingsCommand = new RelayCommand(() => ShowSettings());
            MinimizeToTrayCommand = new RelayCommand(() =>
            {
                if (Application.Current?.MainWindow is { } window)
                {
                    window.Hide();
                }
                _trayService.ShowBalloonTip("CROWN Controller HUD", "Running in background", BalloonIcon.Info);
            });
            TestNotificationCommand = new AsyncRelayCommand(async () => await TestNotificationAsync());
            ShowDiagnosticsCommand = new RelayCommand(() => ShowDiagnostics = !ShowDiagnostics);
            OpenLogCommand = new RelayCommand(() => OpenLog());
            CopyDiagnosticsCommand = new RelayCommand(() => CopyDiagnosticsToClipboard());

            SubscribeToEvents();
            UpdateSystemInfo();

            // Diagnostic: the number of these lines per run proves how many
            // MainViewModel instances subscribed to the controller manager. It must be
            // exactly ONE - two instances meant every controller transition fired two
            // notifications.
            _logger.LogInformation(
                "[Notify] MainViewModel instance #{Instance} constructed and subscribed to controller events",
                Interlocked.Increment(ref _instanceCounter));
        }

        private static int _instanceCounter;

        private void SubscribeToEvents()
        {
            _controllerManager.ControllersChanged += OnControllersChanged;
            _controllerManager.ControllerConnected += OnControllerConnected;
            _controllerManager.ControllerDisconnected += OnControllerDisconnected;
            _controllerManager.ControllerBatteryChanged += OnControllerBatteryChanged;
            _controllerManager.ControllerChargingChanged += OnControllerChargingChanged;
        }

        private int _initialized;

        public async Task InitializeAsync()
        {
            // Idempotent: this is called both from MainWindow.Loaded and from application
            // startup. The startup call matters because Window.Loaded never fires for a
            // window that is never shown (e.g. --hidden at sign-in), which previously meant
            // no controller monitoring ran at all.
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
                return;

            try
            {
                StatusText = "Starting controller detection...";
                await _controllerManager.InitializeAsync();
                await _controllerManager.StartAsync();
                StatusText = "Ready";
                await RefreshAsync();
                UpdateProviderDiagnostics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize");
                StatusText = "Error: " + ex.Message;
            }
        }

        public async Task RefreshAsync()
        {
            IsScanning = true;
            StatusText = "Scanning (querying live connection state)...";

            try
            {
                // Force refresh re-queries the live Bluetooth connection signals before
                // re-running detection, so the reported state is genuinely current and a
                // controller that was just switched off cannot keep showing as connected.
                await _controllerManager.ForceRefreshAsync();

                var error = _controllerManager.LastRefreshError;
                if (string.IsNullOrEmpty(error))
                {
                    StatusText = ConnectedCount == 0
                        ? $"No controllers connected (checked {DateTime.Now:HH:mm:ss})"
                        : $"Found {ConnectedCount} connected controller(s) (checked {DateTime.Now:HH:mm:ss})";
                }
                else
                {
                    StatusText = "Refresh problem: " + error;
                    DiagnosticLogger.Warn($"[Refresh] {error}");
                }

                UpdateProviderDiagnostics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh failed");
                StatusText = "Refresh failed: " + ex.Message;
                DiagnosticLogger.Error("Refresh failed", ex);
                UpdateProviderDiagnostics();
            }
            finally
            {
                IsScanning = false;
            }
        }

        /// <summary>
        /// Marshals work to the UI thread without ever blocking the calling thread, so a
        /// background detection/monitor callback can never deadlock or freeze the window.
        /// </summary>
        private static void OnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }

            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnControllersChanged(object? sender, ControllersChangedEventArgs e)
        {
            OnUi(() =>
            {
                UpdateControllers(e.Controllers);
                UpdateProviderDiagnostics();
            });
        }

        private void OnControllerConnected(object? sender, ControllerEventArgs e)
        {
            // Requirement: log the state transition as it is received, before anything
            // downstream can swallow it, so the pipeline is fully traceable.
            _logger.LogInformation(
                "[Notify] State transition received: CONNECTED initial={Initial} device='{Name}' type={Type} transport={Transport} id={Id} source={Source}",
                e.IsInitial, e.Controller.Name, e.Controller.Type, e.Controller.ConnectionType,
                e.Controller.DeviceId, e.Controller.ConnectionStateSource);

            OnUi(() =>
            {
                if (e.IsInitial)
                {
                    _logger.LogInformation(
                        "[Notify] Notification suppressed: initial observation of an already-connected controller ('{Name}')",
                        e.Controller.Name);
                }
                else
                {
                    _notificationService.ShowControllerConnectedAsync(e.Controller);
                }

                _trayService.UpdateTrayIcon(GetPrimaryController());
                StatusText = $"{e.Controller.Type.GetDisplayName()} connected ({e.Controller.ConnectionType}) at {DateTime.Now:HH:mm:ss}";
                UpdateProviderDiagnostics();
            });
        }

        private void OnControllerDisconnected(object? sender, ControllerEventArgs e)
        {
            _logger.LogInformation(
                "[Notify] State transition received: DISCONNECTED initial={Initial} device='{Name}' type={Type} transport={Transport} id={Id} source={Source}",
                e.IsInitial, e.Controller.Name, e.Controller.Type, e.Controller.ConnectionType,
                e.Controller.DeviceId, e.Controller.ConnectionStateSource);

            OnUi(() =>
            {
                if (e.IsInitial)
                {
                    _logger.LogInformation(
                        "[Notify] Notification suppressed: initial observation of a disconnected controller ('{Name}')",
                        e.Controller.Name);
                }
                else
                {
                    _notificationService.ShowControllerDisconnectedAsync(e.Controller);
                }

                _trayService.UpdateTrayIcon(GetPrimaryController());
                StatusText = $"{e.Controller.Type.GetDisplayName()} disconnected at {DateTime.Now:HH:mm:ss}";
                UpdateProviderDiagnostics();
            });
        }

        private void OnControllerBatteryChanged(object? sender, ControllerEventArgs e)
        {
            OnUi(() =>
            {
                UpdateControllerInList(e.Controller);
                _trayService.UpdateTrayIcon(GetPrimaryController());

                // Battery levels are noisy by nature (capacity estimates drift). All of
                // the deciding happens in BatteryWarningPolicy, which only lets a warning
                // through when a NEW, more severe level is crossed, and re-arms only after
                // a genuine recovery or confirmed charging.
                var decision = _batteryWarnings.Evaluate(
                    e.Controller.DeviceId,
                    e.Controller.BatteryPercentage,
                    e.Controller.BatteryState,
                    e.Controller.IsCharging);

                if (decision.ShouldNotify)
                {
                    DiagnosticLogger.Info(
                        $"[Battery] warning decision device='{e.Controller.Name}' kind={decision.Kind} " +
                        $"percent={(e.Controller.BatteryPercentage?.ToString() ?? "n/a")} reason='{decision.Reason}'");

                    _ = _notificationService.ShowBatteryWarningAsync(e.Controller, decision);
                }
                else
                {
                    DiagnosticLogger.Info(
                        $"[Battery] warning suppressed device='{e.Controller.Name}' reason='{decision.Reason}'");
                }

                UpdateProviderDiagnostics();
            });
        }

        private void OnControllerChargingChanged(object? sender, ControllerEventArgs e)
        {
            OnUi(() =>
            {
                UpdateControllerInList(e.Controller);
                _trayService.UpdateTrayIcon(GetPrimaryController());

                // Charging is only ever reported from genuine device telemetry
                // (Windows.Devices.Power BatteryReport.Status). A cable being plugged in
                // is not evidence of charging, so nothing is inferred here.
                if (!e.Controller.BatterySupportsCharging)
                {
                    DiagnosticLogger.Info(
                        $"[Battery] charging state not reported for '{e.Controller.Name}': the telemetry source " +
                        $"('{e.Controller.BatterySource ?? "none"}') cannot confirm charging");
                    UpdateProviderDiagnostics();
                    return;
                }

                if (e.Controller.IsCharging)
                {
                    _notificationService.ShowChargingStartedAsync(e.Controller);
                }
                else if (e.Controller.BatteryState == BatteryState.Full)
                {
                    _notificationService.ShowChargingCompleteAsync(e.Controller);
                }

                UpdateProviderDiagnostics();
            });
        }

        private void UpdateControllers(System.Collections.Generic.IReadOnlyList<ControllerInfo> controllers)
        {
            var currentIds = Controllers.Select(c => c.Info.DeviceId).ToHashSet();
            var newIds = controllers.Select(c => c.DeviceId).ToHashSet();

            for (int i = Controllers.Count - 1; i >= 0; i--)
            {
                if (!newIds.Contains(Controllers[i].Info.DeviceId))
                {
                    Controllers.RemoveAt(i);
                }
            }

            foreach (var controller in controllers)
            {
                var existing = Controllers.FirstOrDefault(c => c.Info.DeviceId == controller.DeviceId);
                if (existing != null)
                {
                    existing.Update(controller);
                }
                else
                {
                    Controllers.Add(new ControllerViewModel(controller));
                }
            }

            ConnectedCount = Controllers.Count(c => c.Info.IsConnected);
        }

        private void UpdateControllerInList(ControllerInfo controller)
        {
            var existing = Controllers.FirstOrDefault(c => c.Info.DeviceId == controller.DeviceId);
            if (existing != null)
            {
                existing.Update(controller);
            }
        }

        private ControllerInfo? GetPrimaryController()
        {
            return Controllers
                .Where(c => c.Info.IsConnected)
                .OrderBy(c => c.Info.Type)
                .Select(c => c.Info)
                .FirstOrDefault();
        }

        private void ShowSettings()
        {
            try
            {
                var settingsService = App.ServiceProvider?.GetService(typeof(ISettingsService)) as ISettingsService;
                var startupService = App.ServiceProvider?.GetService(typeof(IStartupService)) as IStartupService;
                var notificationService = App.ServiceProvider?.GetService(typeof(INotificationService)) as INotificationService;
                var controllerManager = App.ServiceProvider?.GetService(typeof(IControllerManager)) as IControllerManager;
                var logger = App.ServiceProvider?.GetService(typeof(ILogger<SettingsWindow>)) as ILogger<SettingsWindow>;

                var settingsWindow = new SettingsWindow(settingsService!, startupService!, notificationService!, controllerManager!, logger!);
                settingsWindow.Show();
                settingsWindow.Activate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open settings window");
                DiagnosticLogger.Error("Settings window failed to open", ex);
            }
        }

        private async Task TestNotificationAsync()
        {
            // Uses the same CROWN renderer as real controller events (never a tray
            // balloon), so this button genuinely proves the production path works.
            try
            {
                await _notificationService.ShowTestNotificationAsync();
                StatusText = "Test notification sent at " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test notification failed");
                StatusText = "Test notification failed: " + ex.Message;
            }
        }

        private void OpenLog()
        {
            try
            {
                var logPath = DiagnosticLogger.GetLogPath();
                if (File.Exists(logPath))
                {
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                }
                else
                {
                    StatusText = "Log file not found";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open log");
                StatusText = "Failed to open log: " + ex.Message;
            }
        }

        private void CopyDiagnosticsToClipboard()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== CROWN Controller HUD Diagnostics ===");
                sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Process ID: {Environment.ProcessId}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($"Runtime: {Environment.Version}");
                sb.AppendLine();
                sb.AppendLine("=== Controller Status ===");
                sb.AppendLine($"Connected: {ConnectedCount}");
                foreach (var c in Controllers)
                {
                    sb.AppendLine($"  {c.Name} ({c.ConnectionType}) - Connected: {c.IsConnected}");
                    sb.AppendLine($"    battery   : {c.BatteryText} ({c.BatteryStateText})");
                    sb.AppendLine($"    charging  : {c.ChargingText}");
                    sb.AppendLine($"    source    : {c.BatterySourceText}");
                    sb.AppendLine($"    freshness : {c.BatteryFreshnessText}");
                    sb.AppendLine($"    chargeRate: {c.BatteryChargeRateText}");
                    if (!string.IsNullOrEmpty(c.BatteryErrorText))
                        sb.AppendLine($"    note      : {c.BatteryErrorText}");
                    if (c.Info.LastKnownBatteryPercentage.HasValue)
                        sb.AppendLine($"    lastKnown : {c.LastKnownBatteryText}");
                        sb.AppendLine($"    note      : {c.BatteryErrorText}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Provider Diagnostics ===");
                foreach (var p in ProviderDiagnostics)
                {
                    sb.AppendLine($"  {p.Name}: {p.StatusText} ({p.DeviceCount} devices)");
                    if (p.HasError)
                        sb.AppendLine($"    ERROR: {p.ErrorMessage}");
                }
                sb.AppendLine();
                sb.AppendLine("=== System Info ===");
                sb.AppendLine(SystemInfo);
                sb.AppendLine();
                sb.AppendLine("=== Recent Log (last 50 lines) ===");
                
                var logPath = DiagnosticLogger.GetLogPath();
                if (File.Exists(logPath))
                {
                    var lines = File.ReadAllLines(logPath);
                    var recent = lines.TakeLast(50);
                    foreach (var line in recent)
                        sb.AppendLine(line);
                }
                
                Clipboard.SetText(sb.ToString());
                StatusText = "Diagnostics copied to clipboard";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy diagnostics");
                StatusText = "Copy failed: " + ex.Message;
            }
        }

        private void UpdateSystemInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($".NET Runtime: {Environment.Version}");
            sb.AppendLine($"Process: {Environment.ProcessId} (64-bit: {Environment.Is64BitProcess})");
            sb.AppendLine($"Working Dir: {Environment.CurrentDirectory}");
            sb.AppendLine($"App Base: {AppDomain.CurrentDomain.BaseDirectory}");
            sb.AppendLine($"User Interactive: {Environment.UserInteractive}");
            sb.AppendLine($"Log Path: {DiagnosticLogger.GetLogPath()}");
            sb.AppendLine($"Settings Path: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrownControllerHUD", "settings.json")}");
            SystemInfo = sb.ToString();
        }

        private void UpdateProviderDiagnostics()
        {
            try
            {
                ProviderDiagnostics.Clear();
                
                // This would ideally come from the controller manager
                // For now, we create basic diagnostics
                var providers = new[]
                {
                    new ProviderDiagnosticInfo 
                    { 
                        Name = "WinRT (Windows.Gaming.Input)", 
                        StatusText = "Initialized", 
                        StatusColor = Brushes.Green,
                        DeviceCount = Controllers.Count(c => c.Info.Type.ToString().StartsWith("Xbox") || c.Info.Type.ToString().StartsWith("XboxOne") || c.Info.Type.ToString().StartsWith("XboxSeries") || c.Info.Type.ToString().StartsWith("Xbox360") || c.Info.Type.ToString().StartsWith("XboxElite") || c.Info.Type.ToString().StartsWith("XboxAdaptive")),
                        HasError = false
                    },
                    new ProviderDiagnosticInfo 
                    { 
                        Name = "HID (HidSharp)", 
                        StatusText = "Initialized", 
                        StatusColor = Brushes.Green,
                        DeviceCount = Controllers.Count(c => c.ConnectionType == "HID" || c.Info.Type == ControllerType.GenericHID),
                        HasError = false
                    },
                    new ProviderDiagnosticInfo 
                    { 
                        Name = "Bluetooth LE", 
                        StatusText = "Initialized (passive)", 
                        StatusColor = Brushes.Orange,
                        DeviceCount = Controllers.Count(c => c.ConnectionType == "Bluetooth"),
                        HasError = false
                    }
                };
                
                foreach (var p in providers)
                    ProviderDiagnostics.Add(p);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateProviderDiagnostics failed");
            }
        }

        public void Dispose()
        {
            _controllerManager.ControllersChanged -= OnControllersChanged;
            _controllerManager.ControllerConnected -= OnControllerConnected;
            _controllerManager.ControllerDisconnected -= OnControllerDisconnected;
            _controllerManager.ControllerBatteryChanged -= OnControllerBatteryChanged;
            _controllerManager.ControllerChargingChanged -= OnControllerChargingChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public class ProviderDiagnosticInfo : INotifyPropertyChanged
    {
        private string _name = "";
        private string _statusText = "";
        private Brush _statusColor = Brushes.Gray;
        private int _deviceCount = 0;
        private bool _hasError = false;
        private string _errorMessage = "";

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
        public Brush StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }
        public int DeviceCount { get => _deviceCount; set => SetProperty(ref _deviceCount, value); }
        public bool HasError { get => _hasError; set => SetProperty(ref _hasError, value); }
        public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public class ControllerViewModel : INotifyPropertyChanged
    {
        private ControllerInfo _info;

        public ControllerInfo Info => _info;

        public string Name => _info.Type.GetDisplayName();
        public string ConnectionType => _info.ConnectionType.ToString();
        public string BatteryText => _info.BatteryPercentage.HasValue ? $"{_info.BatteryPercentage}%" : "Unknown";
        public string BatteryStateText => _info.BatteryState.ToString();
        public bool IsCharging => _info.IsCharging;
        public bool IsConnected => _info.IsConnected;
        public string FirmwareVersion => _info.FirmwareVersion ?? "Unknown";
        public string DeviceId => _info.DeviceId;

        /// <summary>
        /// Charging state as a sentence. "Not charging" is only stated when the device
        /// telemetry can actually tell us; otherwise the state is honestly unknown, because
        /// a cable being connected is not evidence of anything.
        /// </summary>
        public string ChargingText => _info.BatterySupportsCharging
            ? (_info.IsCharging ? "Charging" : "Not charging")
            : "Charging state not reported";

        /// <summary>Where the battery value came from, for the diagnostics panel.</summary>
        public string BatterySourceText => string.IsNullOrWhiteSpace(_info.BatterySource)
            ? "no battery source"
            : _info.BatterySource!;

        public string BatteryFreshnessText => _info.BatteryLastUpdated.HasValue
            ? $"{_info.BatteryLastUpdated.Value:HH:mm:ss}{(_info.BatteryIsStale ? " (stale)" : string.Empty)}"
            : "never";

        public string BatteryErrorText => _info.BatteryError ?? string.Empty;

        public string BatteryChargeRateText => _info.BatteryChargeRateMw.HasValue
            ? $"{_info.BatteryChargeRateMw.Value} mW"
            : "not reported";

        /// <summary>
        /// When the current battery telemetry is unavailable (e.g., over USB), this
        /// shows the last successfully read value with its timestamp. Never presented
        /// as current - clearly labelled as "last known".
        /// </summary>
        public string LastKnownBatteryText
        {
            get
            {
                if (!_info.LastKnownBatteryPercentage.HasValue)
                    return "none";
                var when = _info.LastKnownBatteryReadAt.HasValue
                    ? _info.LastKnownBatteryReadAt.Value.ToString("HH:mm:ss")
                    : "unknown time";
                return $"{_info.LastKnownBatteryPercentage}% at {when} (via {_info.LastKnownBatterySource ?? "unknown"})";
            }
        }

        public ControllerViewModel(ControllerInfo info)
        {
            _info = info;
        }

        public void Update(ControllerInfo info)
        {
            _info = info;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(ConnectionType));
            OnPropertyChanged(nameof(BatteryText));
            OnPropertyChanged(nameof(BatteryStateText));
            OnPropertyChanged(nameof(IsCharging));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(FirmwareVersion));
            OnPropertyChanged(nameof(ChargingText));
            OnPropertyChanged(nameof(BatterySourceText));
            OnPropertyChanged(nameof(BatteryFreshnessText));
            OnPropertyChanged(nameof(BatteryErrorText));
            OnPropertyChanged(nameof(BatteryChargeRateText));
            OnPropertyChanged(nameof(LastKnownBatteryText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _isExecuting = true;
                CommandManager.InvalidateRequerySuggested();
                try
                {
                    await _execute();
                }
                finally
                {
                    _isExecuting = false;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
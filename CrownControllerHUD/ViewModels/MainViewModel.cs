using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IControllerManager _controllerManager;
        private readonly INotificationService _notificationService;
        private readonly ISettingsService _settingsService;
        private readonly ITrayService _trayService;
        private readonly ILogger<MainViewModel> _logger;

        private ObservableCollection<ControllerViewModel> _controllers = new();
        private ControllerViewModel? _selectedController;
        private bool _isScanning = false;
        private string _statusText = "Initializing...";
        private int _connectedCount = 0;

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

        public ICommand RefreshCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand MinimizeToTrayCommand { get; }

        public MainViewModel(
            IControllerManager controllerManager,
            INotificationService notificationService,
            ISettingsService settingsService,
            ITrayService trayService,
            ILogger<MainViewModel> logger)
        {
            _controllerManager = controllerManager;
            _notificationService = notificationService;
            _settingsService = settingsService;
            _trayService = trayService;
            _logger = logger;

            RefreshCommand = new RelayCommand(async () => await RefreshAsync());
            SettingsCommand = new RelayCommand(() => ShowSettings());
            MinimizeToTrayCommand = new RelayCommand(() => _trayService.HideMainWindow());

            SubscribeToEvents();
        }

        private void SubscribeToEvents()
        {
            _controllerManager.ControllersChanged += OnControllersChanged;
            _controllerManager.ControllerConnected += OnControllerConnected;
            _controllerManager.ControllerDisconnected += OnControllerDisconnected;
            _controllerManager.ControllerBatteryChanged += OnControllerBatteryChanged;
            _controllerManager.ControllerChargingChanged += OnControllerChargingChanged;
        }

        public async Task InitializeAsync()
        {
            try
            {
                StatusText = "Starting controller detection...";
                await _controllerManager.InitializeAsync();
                await _controllerManager.StartAsync();
                StatusText = "Ready";
                await RefreshAsync();
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
            StatusText = "Scanning for controllers...";
            
            try
            {
                await _controllerManager.RefreshAsync();
                StatusText = $"Found {ConnectedCount} controller(s)";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh failed");
                StatusText = "Refresh failed: " + ex.Message;
            }
            finally
            {
                IsScanning = false;
            }
        }

        private void OnControllersChanged(object? sender, ControllersChangedEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateControllers(e.Controllers);
            });
        }

        private void OnControllerConnected(object? sender, ControllerEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _notificationService.ShowControllerConnectedAsync(e.Controller);
                _trayService.UpdateTrayIcon(GetPrimaryController());
                StatusText = $"{e.Controller.Type.GetDisplayName()} connected";
            });
        }

        private void OnControllerDisconnected(object? sender, ControllerEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _notificationService.ShowControllerDisconnectedAsync(e.Controller);
                _trayService.UpdateTrayIcon(GetPrimaryController());
                StatusText = $"{e.Controller.Type.GetDisplayName()} disconnected";
            });
        }

        private void OnControllerBatteryChanged(object? sender, ControllerEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateControllerInList(e.Controller);
                _trayService.UpdateTrayIcon(GetPrimaryController());
                
                // Check thresholds
                var settings = _settingsService.Settings.Notifications;
                if (e.Controller.BatteryPercentage.HasValue)
                {
                    if (e.Controller.BatteryPercentage <= settings.CriticalBatteryThreshold)
                    {
                        _notificationService.ShowBatteryCriticalAsync(e.Controller);
                    }
                    else if (e.Controller.BatteryPercentage <= settings.LowBatteryThreshold)
                    {
                        _notificationService.ShowBatteryLowAsync(e.Controller);
                    }
                }
            });
        }

        private void OnControllerChargingChanged(object? sender, ControllerEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateControllerInList(e.Controller);
                _trayService.UpdateTrayIcon(GetPrimaryController());
                
                if (e.Controller.IsCharging)
                    _notificationService.ShowChargingStartedAsync(e.Controller);
                else if (e.Controller.BatteryPercentage >= 95)
                    _notificationService.ShowChargingCompleteAsync(e.Controller);
            });
        }

        private void UpdateControllers(System.Collections.Generic.IReadOnlyList<ControllerInfo> controllers)
        {
            var currentIds = Controllers.Select(c => c.Info.DeviceId).ToHashSet();
            var newIds = controllers.Select(c => c.DeviceId).ToHashSet();

            // Remove disconnected
            for (int i = Controllers.Count - 1; i >= 0; i--)
            {
                if (!newIds.Contains(Controllers[i].Info.DeviceId))
                {
                    Controllers.RemoveAt(i);
                }
            }

            // Add or update
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
            // TODO: Show settings window
            _logger.LogInformation("Settings requested");
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
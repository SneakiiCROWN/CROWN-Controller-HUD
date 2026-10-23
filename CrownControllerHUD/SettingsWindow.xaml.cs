using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD
{
    public partial class SettingsWindow : Window
    {
        private bool _isLoading = true;

        private readonly ISettingsService _settingsService;
        private readonly IStartupService _startupService;
        private readonly INotificationService _notificationService;
        private readonly IControllerManager _controllerManager;
        private readonly ILogger<SettingsWindow> _logger;

        private static SettingsWindow? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// Gets or creates the singleton SettingsWindow instance.
        /// </summary>
        public static SettingsWindow GetOrCreateInstance(
            ISettingsService settingsService,
            IStartupService startupService,
            INotificationService notificationService,
            IControllerManager controllerManager,
            ILogger<SettingsWindow> logger)
        {
            lock (_instanceLock)
            {
                if (_instance == null)
                {
                    _instance = new SettingsWindow(settingsService, startupService, notificationService, controllerManager, logger);
                    _instance.Closed += (s, e) => _instance = null;
                }
                return _instance;
            }
        }

        // View models for the controllers list
        public ObservableCollection<SavedControllerViewModel> SavedControllers { get; } = new();
        public ObservableCollection<HiddenControllerViewModel> HiddenControllers { get; } = new();

        public ICommand RenameSavedControllerCommand { get; }
        public ICommand ResetSavedControllerCommand { get; }
        public ICommand RemoveSavedControllerCommand { get; }
        public ICommand ShowHiddenControllerCommand { get; }

        private SettingsWindow(
            ISettingsService settingsService,
            IStartupService startupService,
            INotificationService notificationService,
            IControllerManager controllerManager,
            ILogger<SettingsWindow> logger)
        {
            InitializeComponent();
            _settingsService = settingsService;
            _startupService = startupService;
            _notificationService = notificationService;
            _controllerManager = controllerManager;
            _logger = logger;

            DataContext = this;

            RenameSavedControllerCommand = new RelayCommand<string>(id => RenameSavedController(id));
            ResetSavedControllerCommand = new RelayCommand<string>(id => ResetSavedController(id));
            RemoveSavedControllerCommand = new RelayCommand<string>(id => RemoveSavedController(id));
            ShowHiddenControllerCommand = new RelayCommand<string>(id => ShowHiddenController(id));

            LoadSettings();
            UpdateCategorySelection("GeneralPanel");
            _isLoading = false;
        }

        private void LoadSettings()
        {
            _isLoading = true;
            var s = _settingsService.Settings;

            // General
            StartWithWindows.IsChecked = s.General.StartWithWindows;
            StartHidden.IsChecked = s.General.StartMinimized;
            MinimizeToTray.IsChecked = s.General.MinimizeToTray;
            CloseToTray.IsChecked = s.General.CloseToTray;
            ShowDisconnected.IsChecked = s.Controllers.ShowDisconnectedControllers;

            // Theme
            SetComboBoxSelection(ThemeSelector, s.General.Theme);
            SetComboBoxSelection(AppearanceThemeSelector, s.General.Theme);

            // Notifications
            EnableNotifications.IsChecked = s.Notifications.EnableNotifications;
            NotifyOnConnect.IsChecked = s.Notifications.NotifyOnConnect;
            NotifyOnDisconnect.IsChecked = s.Notifications.NotifyOnDisconnect;
            NotifyOnLowBattery.IsChecked = s.Notifications.NotifyOnLowBattery;
            NotifyOnCriticalBattery.IsChecked = s.Notifications.NotifyOnCriticalBattery;
            NotifyOnCharging.IsChecked = s.Notifications.NotifyOnCharging;
            NotifyOnFullCharge.IsChecked = s.Notifications.NotifyOnFullCharge;
            NotifyOnBatteryWarnings.IsChecked = s.Notifications.NotifyOnBatteryWarnings;
            NotifyOnBatteryRecovered.IsChecked = s.Notifications.NotifyOnBatteryRecovered;
            NotificationDuration.Value = s.Notifications.NotificationDurationMs;
            DurationText.Text = $"{s.Notifications.NotificationDurationMs} ms";
            PlaySound.IsChecked = s.Notifications.PlaySound;

            // Notification Position
            SetComboBoxSelection(NotificationPosition, s.UI.NotificationPosition);

            // Battery
            EnableBatteryTelemetry.IsChecked = s.Battery.EnableBatteryTelemetry;
            EnableGameControllerApiBattery.IsChecked = s.Battery.EnableGameControllerApiBattery;
            EnableGattBattery.IsChecked = s.Battery.EnableGattBattery;
            EnableXInputBattery.IsChecked = s.Battery.EnableXInputBattery;
            EnableHidBatteryDiagnostics.IsChecked = s.Battery.EnableHidBatteryDiagnostics;
            UseCachedGattReadsOnly.IsChecked = s.Battery.UseCachedGattReadsOnly;
            BatteryRefreshInterval.Value = s.Battery.RefreshIntervalSeconds;
            BatteryRefreshIntervalText.Text = $"{s.Battery.RefreshIntervalSeconds} s";
            BatteryStaleAfter.Value = s.Battery.StaleAfterSeconds;
            BatteryStaleAfterText.Text = $"{s.Battery.StaleAfterSeconds} s";

            Warn50.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 50 && l.Enabled);
            Warn20.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 20 && l.Enabled);
            Warn15.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 15 && l.Enabled);
            Warn10.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 10 && l.Enabled);
            Warn5.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 5 && l.Enabled);

            // Sync startup checkbox with actual registry state
            StartWithWindows.IsChecked = _startupService.IsStartupEnabled;

            // Appearance
            SetComboBoxSelection(AccentColorSelector, s.UI.AccentColor);
            UpdateAccentColorPreview(s.UI.AccentColor);
            EnableAnimations.IsChecked = s.UI.EnableAnimations;
            CompactMode.IsChecked = s.UI.CompactMode;

            // Load saved controllers
            LoadSavedControllers();

            _isLoading = false;
        }

        private void SetComboBoxSelection(ComboBox comboBox, string value)
        {
            if (comboBox == null) return;
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag?.ToString() == value)
                {
                    comboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void UpdateAccentColorPreview(string color)
        {
            if (AccentColorPreview != null)
            {
                try
                {
                    var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
                    AccentColorPreview.Background = brush;
                }
                catch { }
            }
        }

        private void LoadSavedControllers()
        {
            SavedControllers.Clear();
            HiddenControllers.Clear();

            var settings = _settingsService.Settings;
            var liveControllers = _controllerManager.Controllers.ToDictionary(c => c.DeviceId);

            // Load saved profiles from overrides
            foreach (var kvp in settings.Controllers.Overrides)
            {
                var deviceId = kvp.Key;
                var overrideSettings = kvp.Value;

                var vm = new SavedControllerViewModel
                {
                    DeviceId = deviceId,
                    CustomName = overrideSettings.CustomName,
                    EnableNotifications = overrideSettings.EnableNotifications,
                    CustomLowBatteryThreshold = overrideSettings.CustomLowBatteryThreshold,
                    CustomCriticalBatteryThreshold = overrideSettings.CustomCriticalBatteryThreshold
                };

                // Try to get type/connection from live controller
                if (liveControllers.TryGetValue(deviceId, out var live))
                {
                    vm.TypeName = live.Type.GetDisplayName();
                    vm.ConnectionType = live.ConnectionType.ToString();
                }
                else
                {
                    vm.TypeName = "Unknown";
                    vm.ConnectionType = "Unknown";
                }

                SavedControllers.Add(vm);
            }

            // Load hidden controllers
            foreach (var hiddenId in settings.Controllers.HiddenControllerIds)
            {
                var vm = new HiddenControllerViewModel
                {
                    DeviceId = hiddenId,
                    Name = hiddenId
                };

                if (liveControllers.TryGetValue(hiddenId, out var live))
                {
                    vm.Name = live.DisplayName;
                    vm.TypeName = live.Type.GetDisplayName();
                }

                HiddenControllers.Add(vm);
            }

            // Show/hide empty state messages
            if (NoSavedControllersText != null)
                NoSavedControllersText.Visibility = SavedControllers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveSettings()
        {
            if (_isLoading) return;
            var s = _settingsService.Settings;

            // General
            s.General.StartWithWindows = StartWithWindows.IsChecked ?? false;
            s.General.StartMinimized = StartHidden.IsChecked ?? false;
            s.General.MinimizeToTray = MinimizeToTray.IsChecked ?? false;
            s.General.CloseToTray = CloseToTray.IsChecked ?? false;
            s.Controllers.ShowDisconnectedControllers = ShowDisconnected.IsChecked ?? false;
            s.General.Theme = GetComboBoxSelection(ThemeSelector) ?? GetComboBoxSelection(AppearanceThemeSelector) ?? "Dark";

            // Notifications
            s.Notifications.EnableNotifications = EnableNotifications.IsChecked ?? false;
            s.Notifications.NotifyOnConnect = NotifyOnConnect.IsChecked ?? false;
            s.Notifications.NotifyOnDisconnect = NotifyOnDisconnect.IsChecked ?? false;
            s.Notifications.NotifyOnLowBattery = NotifyOnLowBattery.IsChecked ?? false;
            s.Notifications.NotifyOnCriticalBattery = NotifyOnCriticalBattery.IsChecked ?? false;
            s.Notifications.NotifyOnCharging = NotifyOnCharging.IsChecked ?? false;
            s.Notifications.NotifyOnFullCharge = NotifyOnFullCharge.IsChecked ?? false;
            s.Notifications.NotifyOnBatteryWarnings = NotifyOnBatteryWarnings.IsChecked ?? false;
            s.Notifications.NotifyOnBatteryRecovered = NotifyOnBatteryRecovered.IsChecked ?? false;
            s.Notifications.NotificationDurationMs = (int)NotificationDuration.Value;
            s.Notifications.PlaySound = PlaySound.IsChecked ?? false;
            s.UI.NotificationPosition = GetComboBoxSelection(NotificationPosition) ?? "BottomRight";

            // Battery
            s.Battery.EnableBatteryTelemetry = EnableBatteryTelemetry.IsChecked ?? false;
            s.Battery.EnableGameControllerApiBattery = EnableGameControllerApiBattery.IsChecked ?? false;
            s.Battery.EnableGattBattery = EnableGattBattery.IsChecked ?? false;
            s.Battery.EnableXInputBattery = EnableXInputBattery.IsChecked ?? false;
            s.Battery.EnableHidBatteryDiagnostics = EnableHidBatteryDiagnostics.IsChecked ?? false;
            s.Battery.UseCachedGattReadsOnly = UseCachedGattReadsOnly.IsChecked ?? false;
            s.Battery.RefreshIntervalSeconds = (int)BatteryRefreshInterval.Value;
            s.Battery.StaleAfterSeconds = (int)BatteryStaleAfter.Value;

            foreach (var level in s.Notifications.BatteryWarnings)
            {
                level.Enabled = level.Percent switch
                {
                    50 => Warn50.IsChecked ?? false,
                    20 => Warn20.IsChecked ?? false,
                    15 => Warn15.IsChecked ?? false,
                    10 => Warn10.IsChecked ?? false,
                    5 => Warn5.IsChecked ?? false,
                    _ => level.Enabled
                };
            }

            // Appearance
            s.UI.AccentColor = GetComboBoxSelection(AccentColorSelector) ?? "#0078D4";
            s.UI.EnableAnimations = EnableAnimations.IsChecked ?? false;
            s.UI.CompactMode = CompactMode.IsChecked ?? false;

            // Controllers - save notification enable state from saved controllers list
            foreach (var vm in SavedControllers)
            {
                if (s.Controllers.Overrides.TryGetValue(vm.DeviceId, out var overrideSettings))
                {
                    overrideSettings.EnableNotifications = vm.EnableNotifications;
                    overrideSettings.CustomLowBatteryThreshold = vm.CustomLowBatteryThreshold;
                    overrideSettings.CustomCriticalBatteryThreshold = vm.CustomCriticalBatteryThreshold;
                }
            }

            s.Normalize();
            _ = _settingsService.SaveAsync();
        }

        private string? GetComboBoxSelection(ComboBox comboBox)
        {
            if (comboBox?.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString();
            return null;
        }

        private void UpdateCategorySelection(string activePanelName)
        {
            var panels = new[] { "GeneralPanel", "NotificationsPanel", "BatteryPanel", "ControllersPanel", "AppearancePanel" };
            var buttons = new[] { BtnGeneral, BtnNotifications, BtnBattery, BtnControllers, BtnAppearance };

            for (int i = 0; i < panels.Length; i++)
            {
                var panel = (StackPanel)FindName(panels[i]);
                var button = buttons[i];

                if (panel != null)
                    panel.Visibility = panels[i] == activePanelName ? Visibility.Visible : Visibility.Collapsed;

                if (button != null)
                {
                    // Update selection indicator via visual state
                    if (panels[i] == activePanelName)
                    {
                        button.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
                        button.BorderThickness = new Thickness(3, 0, 0, 0);
                    }
                    else
                    {
                        button.Background = System.Windows.Media.Brushes.Transparent;
                        button.BorderBrush = System.Windows.Media.Brushes.Transparent;
                        button.BorderThickness = new Thickness(0);
                    }
                }
            }
        }

        private void General_Click(object sender, RoutedEventArgs e)
        {
            UpdateCategorySelection("GeneralPanel");
        }

        private void Notifications_Click(object sender, RoutedEventArgs e)
        {
            UpdateCategorySelection("NotificationsPanel");
        }

        private void Battery_Click(object sender, RoutedEventArgs e)
        {
            UpdateCategorySelection("BatteryPanel");
        }

        private void Controllers_Click(object sender, RoutedEventArgs e)
        {
            UpdateCategorySelection("ControllersPanel");
        }

        private void Appearance_Click(object sender, RoutedEventArgs e)
        {
            UpdateCategorySelection("AppearancePanel");
        }

        private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool success;
            if (StartWithWindows.IsChecked == true)
            {
                success = _startupService.EnableStartup();
            }
            else
            {
                success = _startupService.DisableStartup();
            }

            // Reflect actual registry state in the checkbox
            StartWithWindows.IsChecked = _startupService.IsStartupEnabled;
            SaveSettings();
        }

        private void EnableNotifications_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            SaveSettings();
        }

        private void NotificationDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            if (DurationText != null)
                DurationText.Text = $"{(int)e.NewValue} ms";
            SaveSettings();
        }

        private void BatteryRefreshInterval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            if (BatteryRefreshIntervalText != null)
                BatteryRefreshIntervalText.Text = $"{(int)e.NewValue} s";
            SaveSettings();
        }

        private void BatteryStaleAfter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            if (BatteryStaleAfterText != null)
                BatteryStaleAfterText.Text = $"{(int)e.NewValue} s";
            SaveSettings();
        }

        private void TestNotification_Click(object sender, RoutedEventArgs e)
        {
            _ = _notificationService.ShowTestNotificationAsync();
        }

        private void ThemeSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            // Sync both theme selectors
            if (sender == ThemeSelector)
                SetComboBoxSelection(AppearanceThemeSelector, GetComboBoxSelection(ThemeSelector) ?? "Dark");
            else
                SetComboBoxSelection(ThemeSelector, GetComboBoxSelection(AppearanceThemeSelector) ?? "Dark");
            SaveSettings();
        }

        private void AppearanceThemeSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            SetComboBoxSelection(ThemeSelector, GetComboBoxSelection(AppearanceThemeSelector) ?? "Dark");
            SaveSettings();
        }

        private void NotificationPosition_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            SaveSettings();
        }

        private void AccentColorSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            var color = GetComboBoxSelection(AccentColorSelector);
            if (color == "Custom")
            {
                CustomColorPanel.Visibility = Visibility.Visible;
                if (CustomColorHex != null)
                    CustomColorHex.Focus();
            }
            else
            {
                CustomColorPanel.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrEmpty(color))
                    UpdateAccentColorPreview(color);
            }
            SaveSettings();
        }

        private void ApplyCustomColor_Click(object sender, RoutedEventArgs e)
        {
            if (CustomColorHex == null) return;
            var hex = CustomColorHex.Text.Trim();
            if (!hex.StartsWith("#")) hex = "#" + hex;
            if (System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
            {
                UpdateAccentColorPreview(hex);
                // Update the custom item's tag
                foreach (ComboBoxItem item in AccentColorSelector.Items)
                {
                    if (item.Tag?.ToString() == "Custom")
                    {
                        item.Tag = hex;
                        item.Content = $"Custom ({hex})";
                        break;
                    }
                }
                AccentColorSelector.SelectedItem = AccentColorSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == hex);
                SaveSettings();
            }
            else
            {
                MessageBox.Show("Invalid color format. Use #RRGGBB format.", "Invalid Color", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RenameSavedController(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;

            var vm = SavedControllers.FirstOrDefault(c => c.DeviceId == deviceId);
            if (vm == null) return;

            var dialog = new InputDialog("Rename Controller", "Enter a nickname for this controller:", vm.CustomName ?? "");
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                var newName = dialog.ResponseText?.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    vm.CustomName = newName;

                    // Update settings
                    var settings = _settingsService.Settings;
                    if (!settings.Controllers.Overrides.ContainsKey(deviceId))
                    {
                        settings.Controllers.Overrides[deviceId] = new ControllerOverrideSettings();
                    }
                    settings.Controllers.Overrides[deviceId].CustomName = newName;
                    _ = _settingsService.SaveAsync();

                    // Update live controller if present
                    var controller = _controllerManager.GetController(deviceId);
                    if (controller != null)
                    {
                        controller.CustomName = newName;
                    }

                    _logger.LogInformation($"Renamed controller {deviceId} to '{newName}'");
                }
            }
        }

        private void ResetSavedController(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;

            var vm = SavedControllers.FirstOrDefault(c => c.DeviceId == deviceId);
            if (vm == null) return;

            vm.CustomName = null;

            var settings = _settingsService.Settings;
            if (settings.Controllers.Overrides.TryGetValue(deviceId, out var overrideSettings))
            {
                overrideSettings.CustomName = string.Empty;
                _ = _settingsService.SaveAsync();
            }

            // Update live controller if present
            var controller = _controllerManager.GetController(deviceId);
            if (controller != null)
            {
                controller.CustomName = null;
            }

            _logger.LogInformation($"Reset custom name for controller {deviceId}");
        }

        private void RemoveSavedController(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;

            var result = MessageBox.Show($"Remove saved profile for this controller?\n\nThis will delete the nickname and any custom settings, but the controller will still be detected normally.",
                "Remove Controller Profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var vm = SavedControllers.FirstOrDefault(c => c.DeviceId == deviceId);
            if (vm != null)
            {
                SavedControllers.Remove(vm);
            }

            var settings = _settingsService.Settings;
            settings.Controllers.Overrides.Remove(deviceId);
            _ = _settingsService.SaveAsync();

            // Also clear custom name from live controller
            var controller = _controllerManager.GetController(deviceId);
            if (controller != null)
            {
                controller.CustomName = null;
            }

            if (NoSavedControllersText != null)
                NoSavedControllersText.Visibility = SavedControllers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _logger.LogInformation($"Removed saved profile for controller {deviceId}");
        }

        private void ShowHiddenController(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;

            var vm = HiddenControllers.FirstOrDefault(c => c.DeviceId == deviceId);
            if (vm != null)
            {
                HiddenControllers.Remove(vm);
            }

            var settings = _settingsService.Settings;
            settings.Controllers.HiddenControllerIds.Remove(deviceId);
            _ = _settingsService.SaveAsync();

            _logger.LogInformation($"Unhid controller {deviceId}");
        }

        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Reset all settings to defaults?", "Confirm Reset",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _settingsService.ResetToDefaults();
                _startupService.DisableStartup();
                LoadSettings();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }
    }

    public class SavedControllerViewModel : INotifyPropertyChanged
    {
        public string DeviceId { get; set; } = string.Empty;
        public string TypeName { get; set; } = "Unknown";
        public string ConnectionType { get; set; } = "Unknown";

        private string? _customName;
        public string? CustomName
        {
            get => _customName;
            set { _customName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCustomName)); }
        }

        public bool HasCustomName => !string.IsNullOrWhiteSpace(_customName);

        public bool EnableNotifications { get; set; } = true;
        public int? CustomLowBatteryThreshold { get; set; }
        public int? CustomCriticalBatteryThreshold { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class HiddenControllerViewModel : INotifyPropertyChanged
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TypeName { get; set; } = "Unknown";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (parameter is T typedParam)
                return _canExecute?.Invoke(typedParam) ?? true;
            if (parameter == null && default(T) == null)
                return _canExecute?.Invoke(default) ?? true;
            return false;
        }

        public void Execute(object? parameter)
        {
            if (parameter is T typedParam)
                _execute(typedParam);
            else if (parameter == null && default(T) == null)
                _execute(default!);
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
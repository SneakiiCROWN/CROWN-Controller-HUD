using System;
using System.Windows;
using System.Windows.Controls;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        public SettingsWindow(
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

            LoadSettings();
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

            // Notifications
            EnableNotifications.IsChecked = s.Notifications.EnableNotifications;
            NotifyOnConnect.IsChecked = s.Notifications.NotifyOnConnect;
            NotifyOnDisconnect.IsChecked = s.Notifications.NotifyOnDisconnect;
            NotifyOnLowBattery.IsChecked = s.Notifications.NotifyOnLowBattery;
            NotifyOnCriticalBattery.IsChecked = s.Notifications.NotifyOnCriticalBattery;
            NotifyOnCharging.IsChecked = s.Notifications.NotifyOnCharging;
            NotifyOnFullCharge.IsChecked = s.Notifications.NotifyOnFullCharge;
            NotificationDuration.Value = s.Notifications.NotificationDurationMs;
            DurationText.Text = $"{s.Notifications.NotificationDurationMs} ms";
            PlaySound.IsChecked = s.Notifications.PlaySound;

            // Battery
            EnableBatteryTelemetry.IsChecked = s.Battery.EnableBatteryTelemetry;
            Warn50.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 50 && l.Enabled);
            Warn20.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 20 && l.Enabled);
            Warn15.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 15 && l.Enabled);
            Warn10.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 10 && l.Enabled);
            Warn5.IsChecked = s.Notifications.BatteryWarnings.Any(l => l.Percent == 5 && l.Enabled);

            // Sync startup checkbox with actual registry state
            StartWithWindows.IsChecked = _startupService.IsStartupEnabled;

            _isLoading = false;
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

            // Notifications
            s.Notifications.EnableNotifications = EnableNotifications.IsChecked ?? false;
            s.Notifications.NotifyOnConnect = NotifyOnConnect.IsChecked ?? false;
            s.Notifications.NotifyOnDisconnect = NotifyOnDisconnect.IsChecked ?? false;
            s.Notifications.NotifyOnLowBattery = NotifyOnLowBattery.IsChecked ?? false;
            s.Notifications.NotifyOnCriticalBattery = NotifyOnCriticalBattery.IsChecked ?? false;
            s.Notifications.NotifyOnCharging = NotifyOnCharging.IsChecked ?? false;
            s.Notifications.NotifyOnFullCharge = NotifyOnFullCharge.IsChecked ?? false;
            s.Notifications.NotificationDurationMs = (int)NotificationDuration.Value;
            s.Notifications.PlaySound = PlaySound.IsChecked ?? false;

            // Battery
            s.Battery.EnableBatteryTelemetry = EnableBatteryTelemetry.IsChecked ?? false;
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

            s.Normalize();
            _ = _settingsService.SaveAsync();
        }

        private void General_Click(object sender, RoutedEventArgs e)
        {
            GeneralPanel.Visibility = Visibility.Visible;
            NotificationsPanel.Visibility = Visibility.Collapsed;
            BatteryPanel.Visibility = Visibility.Collapsed;
            AppearancePanel.Visibility = Visibility.Collapsed;
        }

        private void Notifications_Click(object sender, RoutedEventArgs e)
        {
            GeneralPanel.Visibility = Visibility.Collapsed;
            NotificationsPanel.Visibility = Visibility.Visible;
            BatteryPanel.Visibility = Visibility.Collapsed;
            AppearancePanel.Visibility = Visibility.Collapsed;
        }

        private void Battery_Click(object sender, RoutedEventArgs e)
        {
            GeneralPanel.Visibility = Visibility.Collapsed;
            NotificationsPanel.Visibility = Visibility.Collapsed;
            BatteryPanel.Visibility = Visibility.Visible;
            AppearancePanel.Visibility = Visibility.Collapsed;
        }

        private void Appearance_Click(object sender, RoutedEventArgs e)
        {
            GeneralPanel.Visibility = Visibility.Collapsed;
            NotificationsPanel.Visibility = Visibility.Collapsed;
            BatteryPanel.Visibility = Visibility.Collapsed;
            AppearancePanel.Visibility = Visibility.Visible;
        }

        private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (StartWithWindows.IsChecked == true)
            {
                _startupService.EnableStartup();
            }
            else
            {
                _startupService.DisableStartup();
            }
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

        private void TestNotification_Click(object sender, RoutedEventArgs e)
        {
            _ = _notificationService.ShowTestNotificationAsync();
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
}

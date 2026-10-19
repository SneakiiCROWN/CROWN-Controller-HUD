using System;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace CrownControllerHUD.Services
{
    public interface INotificationService
    {
        Task ShowControllerConnectedAsync(ControllerInfo controller);
        Task ShowControllerDisconnectedAsync(ControllerInfo controller);
        Task ShowBatteryLowAsync(ControllerInfo controller);
        Task ShowBatteryCriticalAsync(ControllerInfo controller);
        Task ShowChargingStartedAsync(ControllerInfo controller);
        Task ShowChargingCompleteAsync(ControllerInfo controller);
        Task ShowCustomNotificationAsync(string title, string message, string? iconPath = null);
    }

    public class NotificationService : INotificationService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private readonly ToastNotifier _toastNotifier;

        public NotificationService(ILogger<NotificationService> logger, IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
            _toastNotifier = ToastNotificationManager.CreateToastNotifier("CrownControllerHUD");
        }

        public async Task ShowControllerConnectedAsync(ControllerInfo controller)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications || !_settings.CurrentValue.Notifications.NotifyOnConnect)
                return;

            await ShowToastAsync(
                "Controller Connected",
                $"{controller.Type.GetDisplayName()} connected via {controller.ConnectionType}",
                GetControllerIconPath(controller.Type));
        }

        public async Task ShowControllerDisconnectedAsync(ControllerInfo controller)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications || !_settings.CurrentValue.Notifications.NotifyOnDisconnect)
                return;

            await ShowToastAsync(
                "Controller Disconnected",
                $"{controller.Type.GetDisplayName()} disconnected",
                GetControllerIconPath(controller.Type));
        }

        public async Task ShowBatteryLowAsync(ControllerInfo controller)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications || !_settings.CurrentValue.Notifications.NotifyOnLowBattery)
                return;

            var threshold = _settings.CurrentValue.Notifications.LowBatteryThreshold;
            if (controller.BatteryPercentage.HasValue && controller.BatteryPercentage > threshold)
                return;

            await ShowToastAsync(
                "Battery Low",
                $"{controller.Type.GetDisplayName()} battery at {controller.BatteryPercentage}%",
                GetControllerIconPath(controller.Type),
                isAlert: true);
        }

        public async Task ShowBatteryCriticalAsync(ControllerInfo controller)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications || !_settings.CurrentValue.Notifications.NotifyOnCriticalBattery)
                return;

            var threshold = _settings.CurrentValue.Notifications.CriticalBatteryThreshold;
            if (controller.BatteryPercentage.HasValue && controller.BatteryPercentage > threshold)
                return;

            await ShowToastAsync(
                "Battery Critical",
                $"{controller.Type.GetDisplayName()} battery at {controller.BatteryPercentage}% - Connect charger!",
                GetControllerIconPath(controller.Type),
                isAlert: true);
        }

        public async Task ShowChargingStartedAsync(ControllerInfo controller)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications || !_settings.CurrentValue.Notifications.NotifyOnCharging)
                return;

            await ShowToastAsync(
                "Charging Started",
                $"{controller.Type.GetDisplayName()} is now charging",
                GetControllerIconPath(controller.Type));
        }

        public async Task ShowChargingCompleteAsync(ControllerInfo controller)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications || !_settings.CurrentValue.Notifications.NotifyOnFullCharge)
                return;

            await ShowToastAsync(
                "Charging Complete",
                $"{controller.Type.GetDisplayName()} is fully charged",
                GetControllerIconPath(controller.Type));
        }

        public async Task ShowCustomNotificationAsync(string title, string message, string? iconPath = null)
        {
            if (!_settings.CurrentValue.Notifications.EnableNotifications)
                return;

            await ShowToastAsync(title, message, iconPath);
        }

        private async Task ShowToastAsync(string title, string message, string? iconPath = null, bool isAlert = false)
        {
            try
            {
                var toastContent = new ToastContent
                {
                    Visual = new ToastVisual
                    {
                        BindingGeneric = new ToastBindingGeneric
                        {
                            Children =
                            {
                                new AdaptiveText { Text = title, HintStyle = AdaptiveTextStyle.Base },
                                new AdaptiveText { Text = message, HintWrap = true }
                            },
                            AppLogoOverride = new ToastGenericAppLogo
                            {
                                Source = iconPath ?? "Assets/AppIcon.png",
                                HintCrop = ToastGenericAppLogoCrop.Circle
                            }
                        }
                    },
                    Duration = isAlert ? ToastDuration.Long : ToastDuration.Short,
                    Audio = isAlert ? new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm") } : null
                };

                var toast = new ToastNotification(toastContent.GetXml())
                {
                    ExpirationTime = DateTimeOffset.Now.AddMilliseconds(_settings.CurrentValue.Notifications.NotificationDurationMs)
                };

                _toastNotifier.Show(toast);
                
                _logger.LogDebug("Toast notification shown: {Title}", title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to show toast notification");
            }

            await Task.CompletedTask;
        }

        private string GetControllerIconPath(ControllerType type)
        {
            return type switch
            {
                ControllerType.XboxOne or ControllerType.XboxOneS or ControllerType.XboxOneElite or 
                ControllerType.XboxEliteSeries2 or ControllerType.XboxSeriesX or ControllerType.XboxSeriesS 
                    => "Assets/Xbox Series/Controller.svg",
                ControllerType.PlayStation4 or ControllerType.PlayStation5 
                    => "Assets/PlayStation Series/Controller.svg",
                ControllerType.NintendoSwitchPro or ControllerType.NintendoSwitchJoyConLeft or ControllerType.NintendoSwitchJoyConRight 
                    => "Assets/Nintendo Switch/Controller.svg",
                _ => "Assets/Generic/Controller.svg"
            };
        }
    }
}
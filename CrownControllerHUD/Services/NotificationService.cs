using System;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        /// <summary>
        /// End-to-end test notification. Uses the exact same CROWN renderer as real
        /// controller events (never a tray balloon), so the button genuinely proves
        /// the production path works.
        /// </summary>
        Task ShowTestNotificationAsync();
    }

    /// <summary>
    /// Decides *whether* a notification is wanted (settings + thresholds) and renders
    /// it through the custom CROWN notification renderer.
    ///
    /// The previous implementation used the UWP toast API, which cannot display
    /// anything for this unpackaged app because it has no registered
    /// AppUserModelID - and it did so without logging, so the failure was invisible.
    /// </summary>
    public class NotificationService : INotificationService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private readonly INotificationRenderer _renderer;

        public NotificationService(
            ILogger<NotificationService> logger,
            IOptionsMonitor<AppSettings> settings,
            INotificationRenderer renderer)
        {
            _logger = logger;
            _settings = settings;
            _renderer = renderer;
        }

        private NotificationSettings Settings => _settings.CurrentValue.Notifications;

        public Task ShowControllerConnectedAsync(ControllerInfo controller)
        {
            if (!IsAllowed(Settings.NotifyOnConnect, "connected", controller))
                return Task.CompletedTask;

            _logger.LogInformation(
                "[Notify] Notification allowed: connected ('{Name}', {Type}, {Transport})",
                controller.Name, controller.Type, controller.ConnectionType);

            Show(new CrownNotificationRequest
            {
                Title = "Controller Connected",
                Message = controller.Type.GetDisplayName(),
                Detail = $"Connected over {controller.ConnectionType}",
                Kind = CrownNotificationKind.Connected,
                TimestampText = Stamp(controller),
                LogLabel = "connected"
            });

            return Task.CompletedTask;
        }

        public Task ShowControllerDisconnectedAsync(ControllerInfo controller)
        {
            if (!IsAllowed(Settings.NotifyOnDisconnect, "disconnected", controller))
                return Task.CompletedTask;

            _logger.LogInformation(
                "[Notify] Notification allowed: disconnected ('{Name}', {Type}, {Transport})",
                controller.Name, controller.Type, controller.ConnectionType);

            Show(new CrownNotificationRequest
            {
                Title = "Controller Disconnected",
                Message = controller.Type.GetDisplayName(),
                Detail = $"Was connected over {controller.ConnectionType}",
                Kind = CrownNotificationKind.Disconnected,
                TimestampText = Stamp(controller),
                LogLabel = "disconnected"
            });

            return Task.CompletedTask;
        }

        public Task ShowBatteryLowAsync(ControllerInfo controller)
        {
            if (!IsAllowed(Settings.NotifyOnLowBattery, "low-battery", controller))
                return Task.CompletedTask;

            var threshold = Settings.LowBatteryThreshold;
            if (controller.BatteryPercentage.HasValue && controller.BatteryPercentage > threshold)
            {
                _logger.LogInformation(
                    "[Notify] Notification suppressed: low-battery ({Percent}% is above the {Threshold}% threshold)",
                    controller.BatteryPercentage, threshold);
                return Task.CompletedTask;
            }

            Show(new CrownNotificationRequest
            {
                Title = "Battery Low",
                Message = $"{controller.Type.GetDisplayName()} at {Describe(controller.BatteryPercentage)}",
                Detail = "Connect a charger when convenient",
                Kind = CrownNotificationKind.BatteryLow,
                TimestampText = Stamp(controller),
                LogLabel = "battery-low"
            });

            return Task.CompletedTask;
        }

        public Task ShowBatteryCriticalAsync(ControllerInfo controller)
        {
            if (!IsAllowed(Settings.NotifyOnCriticalBattery, "critical-battery", controller))
                return Task.CompletedTask;

            var threshold = Settings.CriticalBatteryThreshold;
            if (controller.BatteryPercentage.HasValue && controller.BatteryPercentage > threshold)
            {
                _logger.LogInformation(
                    "[Notify] Notification suppressed: critical-battery ({Percent}% is above the {Threshold}% threshold)",
                    controller.BatteryPercentage, threshold);
                return Task.CompletedTask;
            }

            Show(new CrownNotificationRequest
            {
                Title = "Battery Critical",
                Message = $"{controller.Type.GetDisplayName()} at {Describe(controller.BatteryPercentage)}",
                Detail = "Connect a charger now",
                Kind = CrownNotificationKind.BatteryCritical,
                TimestampText = Stamp(controller),
                LogLabel = "battery-critical"
            });

            return Task.CompletedTask;
        }

        public Task ShowChargingStartedAsync(ControllerInfo controller)
        {
            if (!IsAllowed(Settings.NotifyOnCharging, "charging", controller))
                return Task.CompletedTask;

            Show(new CrownNotificationRequest
            {
                Title = "Charging Started",
                Message = $"{controller.Type.GetDisplayName()} is charging",
                Detail = controller.BatteryPercentage.HasValue
                    ? $"Current charge {controller.BatteryPercentage}%"
                    : null,
                Kind = CrownNotificationKind.Charging,
                TimestampText = Stamp(controller),
                LogLabel = "charging"
            });

            return Task.CompletedTask;
        }

        public Task ShowChargingCompleteAsync(ControllerInfo controller)
        {
            if (!IsAllowed(Settings.NotifyOnFullCharge, "full-charge", controller))
                return Task.CompletedTask;

            Show(new CrownNotificationRequest
            {
                Title = "Charging Complete",
                Message = $"{controller.Type.GetDisplayName()} is fully charged",
                Kind = CrownNotificationKind.Charging,
                TimestampText = Stamp(controller),
                LogLabel = "full-charge"
            });

            return Task.CompletedTask;
        }

        public Task ShowCustomNotificationAsync(string title, string message, string? iconPath = null)
        {
            if (!Settings.EnableNotifications)
            {
                _logger.LogInformation("[Notify] Notification suppressed: custom ('{Title}') because notifications are disabled", title);
                return Task.CompletedTask;
            }

            Show(new CrownNotificationRequest
            {
                Title = title,
                Message = message ?? string.Empty,
                Kind = CrownNotificationKind.Info,
                LogLabel = "custom"
            });

            return Task.CompletedTask;
        }

        public async Task ShowTestNotificationAsync()
        {
            _logger.LogInformation("[Notify] Test notification requested from the UI");

            // A test is explicit user intent: it is shown even when the per-event
            // toggles are off, but still respects the master notification switch.
            if (!Settings.EnableNotifications)
            {
                _logger.LogInformation("[Notify] Notification suppressed: test because notifications are disabled");
                return;
            }

            // Render the two real controller cards through the exact same renderer and
            // the exact same request shape as genuine events, so the test genuinely
            // proves what a real connect/disconnect will look like.
            Show(new CrownNotificationRequest
            {
                Title = "Controller Connected",
                Message = "Xbox Elite Series 2",
                Detail = "Connected over Bluetooth - TEST",
                Kind = CrownNotificationKind.Connected,
                LogLabel = "test-connected"
            });

            await Task.Delay(260).ConfigureAwait(false);

            Show(new CrownNotificationRequest
            {
                Title = "Controller Disconnected",
                Message = "Xbox Elite Series 2",
                Detail = "Was connected over Bluetooth - TEST",
                Kind = CrownNotificationKind.Disconnected,
                LogLabel = "test-disconnected"
            });

            await Task.Delay(120).ConfigureAwait(false);
        }

        private bool IsAllowed(bool eventEnabled, string label, ControllerInfo controller)
        {
            if (!Settings.EnableNotifications)
            {
                _logger.LogInformation(
                    "[Notify] Notification suppressed: {Label} because notifications are disabled in settings",
                    label);
                return false;
            }

            if (!eventEnabled)
            {
                _logger.LogInformation(
                    "[Notify] Notification suppressed: {Label} because the per-event toggle is off",
                    label);
                return false;
            }

            _ = controller;
            return true;
        }

        private void Show(CrownNotificationRequest request)
        {
            request.DurationMs = Settings.NotificationDurationMs;
            request.PlaySound = Settings.PlaySound;

            _logger.LogInformation(
                "[Notify] Notification requested: kind={Kind} label={Label} title='{Title}' duration={Duration}ms sound={Sound}",
                request.Kind, request.LogLabel, request.Title, request.DurationMs, request.PlaySound);

            var shown = _renderer.Show(request);

            if (!shown)
                _logger.LogWarning("[Notify] Notification not displayed: renderer rejected '{Label}'", request.LogLabel);
        }

        private static string Describe(int? percent) =>
            percent.HasValue ? $"{percent.Value}%" : "an unknown charge level";

        private static string Stamp(ControllerInfo controller)
        {
            var when = controller.ConnectionStateChangedAt ?? DateTime.Now;
            return when.ToString("HH:mm:ss");
        }
    }
}

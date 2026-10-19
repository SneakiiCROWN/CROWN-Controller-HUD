using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Windows.Gaming.Input;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Detects controllers through <c>Windows.Gaming.Input</c> using the *directly
    /// referenced* WinRT projections.
    ///
    /// Previously this class tried <c>Assembly.Load("Windows.Gaming.Input")</c>, which can
    /// never work with a Windows-targeted TFM: the WinRT types are projected into the
    /// application assembly, so there is no loadable "Windows.Gaming.Input" assembly to
    /// reflect over. The projected types are used directly here instead.
    ///
    /// <see cref="RawGameController"/> is used rather than <c>Gamepad</c> because it is the
    /// surface that exposes real device identity (<c>HardwareVendorId</c>,
    /// <c>HardwareProductId</c>, <c>NonRoamableId</c>). The newer <c>Gamepad</c> projection
    /// exposes no device identity at all, so it cannot be correlated to a physical device.
    ///
    /// This detector reports identity only and never asserts that a controller is switched
    /// on - ControllerManager resolves connection state from a verified live signal.
    /// </summary>
    public class WinRTControllerDetector : BaseControllerDetector
    {
        private readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);

        public override string Name => "WinRT (Windows.Gaming.Input)";

        public override bool IsAvailable { get; }

        public WinRTControllerDetector(ILogger<WinRTControllerDetector> logger) : base(logger)
        {
            bool available;
            try
            {
                _ = RawGameController.RawGameControllers.Count;
                available = true;
            }
            catch (Exception ex)
            {
                available = false;
                _logger.LogWarning(ex, "Windows.Gaming.Input is not available; WinRT detector disabled");
            }

            IsAvailable = available;
            DiagnosticLogger.Info($"WinRTControllerDetector ctor: available={IsAvailable} (direct WinRT projections)");
        }

        public override Task InitializeAsync()
        {
            if (!IsAvailable) return Task.CompletedTask;

            try
            {
                var count = RawGameController.RawGameControllers.Count;
                DiagnosticLogger.Info($"[WinRT] initialized; {count} raw game controller(s) currently connected");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enumerating Windows.Gaming.Input raw game controllers failed");
            }

            return Task.CompletedTask;
        }

        public override Task<IEnumerable<ControllerInfo>> DetectControllersAsync()
        {
            var controllers = new List<ControllerInfo>();
            if (!IsAvailable) return Task.FromResult<IEnumerable<ControllerInfo>>(controllers);

            try
            {
                foreach (var raw in RawGameController.RawGameControllers)
                {
                    var controller = BuildController(raw);
                    if (controller != null)
                        controllers.Add(controller);
                }

                lock (_lock)
                {
                    var currentIds = controllers.Select(c => c.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var id in _knownControllers.Keys.Where(k => !currentIds.Contains(k)).ToList())
                        OnControllerRemoved(id);

                    foreach (var controller in controllers)
                    {
                        if (_knownControllers.TryGetValue(controller.DeviceId, out var existing))
                        {
                            controller.ConnectedAt = existing.ConnectedAt;
                            _knownControllers[controller.DeviceId] = controller;
                            OnControllerUpdated(controller);
                        }
                        else
                        {
                            OnControllerDetected(controller);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting controllers via Windows.Gaming.Input");
            }

            return Task.FromResult<IEnumerable<ControllerInfo>>(controllers);
        }

        private ControllerInfo? BuildController(RawGameController raw)
        {
            try
            {
                ushort vendorId = raw.HardwareVendorId;
                ushort productId = raw.HardwareProductId;
                var displayName = raw.DisplayName ?? string.Empty;
                var nonRoamableId = raw.NonRoamableId ?? string.Empty;

                // Only report devices that are unambiguously game controllers, so a racing
                // wheel or flight stick raw controller cannot slip in.
                bool identified = KnownControllerIds.TryIdentifyExact(vendorId, productId, out _);
                bool plausible = KnownControllerIds.IsGamepadVendor(vendorId) && LooksLikeGamepad(displayName);
                if (!identified && !plausible)
                {
                    Log(raw, "not a recognised gamepad");
                    return null;
                }

                // Resolve the Plug-and-play identity so this record collapses into the same
                // physical device as the PnP/HID providers.
                var instanceId = PnpDeviceEnumerator.InterfacePathToInstanceId(nonRoamableId);
                Guid container = Guid.Empty;

                if (!string.IsNullOrEmpty(instanceId))
                {
                    try { container = PnpDeviceEnumerator.Describe(instanceId!).ContainerId; }
                    catch { /* handled by the container check below */ }
                }

                if (!PnpDeviceEnumerator.IsUsableContainer(container))
                {
                    // Without a container this record cannot be proven to belong to a distinct
                    // physical device; emitting it could resurrect duplicate controller
                    // entries, so it is skipped (the PnP detector still reports the pad).
                    Log(raw, "no usable device container for NonRoamableId");
                    return null;
                }

                var containerText = container.ToString("D").ToUpperInvariant();
                var controller = CreateBaseControllerInfo(
                    $"CTRL\\{containerText}\\{vendorId:X4}_{productId:X4}",
                    KnownControllerIds.IdentifyController(vendorId, productId).GetDisplayName());

                controller.Provider = "WinRT";
                controller.VendorId = vendorId;
                controller.ProductId = productId;
                controller.Type = KnownControllerIds.IdentifyController(vendorId, productId);
                controller.ContainerId = container.ToString("D");
                controller.PnpInstanceId = instanceId;
                controller.ConnectionType = DetermineTransport(nonRoamableId, instanceId);

                if (controller.ConnectionType == ConnectionType.Bluetooth)
                {
                    var mac = DeviceIdentity.FromPnpInstanceId(instanceId)
                              ?? DeviceIdentity.FromAssociationEndpointId(nonRoamableId);
                    if (mac != null)
                        controller.BluetoothAddress = mac;
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                    controller.Name = displayName;

                controller.SupportsVibration = raw.ForceFeedbackMotors.Count > 0;
                controller.SupportsWireless = raw.IsWireless || controller.ConnectionType != ConnectionType.USB;

                DiagnosticLogger.Info(
                    $"[WinRT] accept vid=0x{vendorId:X4} pid=0x{productId:X4} transport={controller.ConnectionType} " +
                    $"container={controller.ContainerId} addr={controller.BluetoothAddress ?? "<none>"} " +
                    $"wireless={raw.IsWireless} name='{controller.Name}' nonRoamable='{nonRoamableId}'");

                return controller;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error building controller from Windows.Gaming.Input raw game controller");
                return null;
            }
        }

        private static bool LooksLikeGamepad(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var lower = text.ToLowerInvariant();
            return lower.Contains("gamepad")
                || lower.Contains("game controller")
                || lower.Contains("joystick")
                || lower.Contains("xinput")
                || lower.Contains("xbox")
                || lower.Contains("wireless controller")
                || lower.Contains("dualshock")
                || lower.Contains("dualsense")
                || lower.Contains("pro controller");
        }

        private static ConnectionType DetermineTransport(string nonRoamableId, string? instanceId)
        {
            var text = (nonRoamableId + " " + (instanceId ?? string.Empty)).ToLowerInvariant();

            if (text.Contains("bthle") || text.Contains("bthenum") || text.Contains("bluetooth") || text.Contains("00001812"))
                return ConnectionType.Bluetooth;

            if (text.Contains("xboxwireless") || text.Contains("nx_adapter"))
                return ConnectionType.XboxWirelessAdapter;

            return ConnectionType.USB;
        }

        private void Log(RawGameController raw, string reason)
        {
            var key = $"{raw.NonRoamableId}|{reason}";
            lock (_logged)
            {
                if (!_logged.Add(key)) return;
            }

            DiagnosticLogger.Info(
                $"[WinRT] reject reason={reason} vid=0x{raw.HardwareVendorId:X4} pid=0x{raw.HardwareProductId:X4} " +
                $"name='{raw.DisplayName}' nonRoamable='{raw.NonRoamableId}'");
        }

        public override Task StartMonitoringAsync()
        {
            if (!IsAvailable || _isMonitoring) return Task.CompletedTask;
            _isMonitoring = true;
            _logger.LogInformation("WinRT (Windows.Gaming.Input) monitoring started");
            return Task.CompletedTask;
        }

        public override Task StopMonitoringAsync()
        {
            _isMonitoring = false;
            _logger.LogInformation("WinRT (Windows.Gaming.Input) monitoring stopped");
            return Task.CompletedTask;
        }
    }
}

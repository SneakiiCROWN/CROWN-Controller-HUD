using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Bluetooth LE controller detector.
    ///
    /// Previously this class used <c>Assembly.Load("Windows.Devices.Bluetooth")</c>, which
    /// cannot work with a Windows-targeted TFM (the WinRT types are projected into the app
    /// assembly, so there is no loadable WinRT assembly to reflect over). It now uses the
    /// directly referenced WinRT APIs.
    ///
    /// To avoid ever mistaking a Bluetooth headset/keyboard for a controller, a paired
    /// endpoint is only reported when the Plug-and-play tree proves it is a HID-over-GATT
    /// (0x1812) gamepad. It reports identity only; connection state comes from
    /// <see cref="ILiveConnectionMonitor"/> via ControllerManager.
    /// </summary>
    public class BluetoothLEControllerDetector : BaseControllerDetector
    {
        private const string HidOverGattService = "00001812";
        private const string PropDeviceAddress = "System.Devices.Aep.DeviceAddress";
        private const string PropIsConnected = "System.Devices.Aep.IsConnected";
        private const string PropIsPresent = "System.Devices.Aep.IsPresent";

        private static readonly string[] WatchedProperties = { PropDeviceAddress, PropIsConnected, PropIsPresent };

        public override string Name => "Bluetooth LE";

        public override bool IsAvailable { get; }

        public BluetoothLEControllerDetector(ILogger<BluetoothLEControllerDetector> logger) : base(logger)
        {
            bool available;
            try
            {
                _ = BluetoothLEDevice.GetDeviceSelector();
                available = true;
            }
            catch (Exception ex)
            {
                available = false;
                _logger.LogWarning(ex, "Windows.Devices.Bluetooth is not available; Bluetooth LE detector disabled");
            }

            IsAvailable = available;
            DiagnosticLogger.Info($"BluetoothLEControllerDetector ctor: available={IsAvailable}");
        }

        public override Task InitializeAsync()
        {
            DiagnosticLogger.Info("[BluetoothLE] initialized (direct WinRT)");
            return Task.CompletedTask;
        }

        public override async Task<IEnumerable<ControllerInfo>> DetectControllersAsync()
        {
            var results = new List<ControllerInfo>();
            if (!IsAvailable) return results;

            try
            {
                // Which Bluetooth addresses are proven HID-over-GATT gamepads? The 0x1812 HID
                // service node in the PnP tree is definitive and cannot match a keyboard,
                // headset or phone.
                var gamepadNodes = new Dictionary<string, (string InstanceId, Guid Container)>(StringComparer.OrdinalIgnoreCase);

                foreach (var instanceId in PnpDeviceEnumerator.EnumeratePresentInstanceIds("BTHLEDEVICE"))
                {
                    if (instanceId.IndexOf(HidOverGattService, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var mac = DeviceIdentity.FromPnpInstanceId(instanceId);
                    if (mac == null) continue;

                    PnpDeviceRecord record;
                    try { record = PnpDeviceEnumerator.Describe(instanceId); }
                    catch { continue; }

                    gamepadNodes[mac] = (instanceId, record.ContainerId);
                }

                var endpoints = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelector(), WatchedProperties, DeviceInformationKind.AssociationEndpoint);

                foreach (var endpoint in endpoints)
                {
                    string? mac = null;
                    if (endpoint.Properties.TryGetValue(PropDeviceAddress, out var addressValue) && addressValue != null)
                        mac = DeviceIdentity.NormalizeAddress(addressValue.ToString());
                    mac ??= DeviceIdentity.FromAssociationEndpointId(endpoint.Id);

                    if (mac == null || !gamepadNodes.TryGetValue(mac, out var node))
                        continue;

                    var controller = BuildController(endpoint, mac, node.InstanceId, node.Container);
                    if (controller != null)
                        results.Add(controller);
                }

                lock (_lock)
                {
                    var currentIds = results.Select(c => c.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var id in _knownControllers.Keys.Where(k => !currentIds.Contains(k)).ToList())
                        OnControllerRemoved(id);

                    foreach (var controller in results)
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
                _logger.LogError(ex, "Error detecting controllers via Bluetooth LE");
                DiagnosticLogger.Error("Bluetooth LE detection failed", ex);
            }

            return results;
        }

        private ControllerInfo? BuildController(DeviceInformation endpoint, string mac, string instanceId, Guid container)
        {
            var (hasVidPid, vendorId, productId) = PnpDeviceEnumerator.ParseVidPid(instanceId);
            var type = hasVidPid
                ? KnownControllerIds.IdentifyController(vendorId, productId)
                : ControllerType.GenericHID;

            var usableContainer = PnpDeviceEnumerator.IsUsableContainer(container);
            var deviceId = usableContainer
                ? $"CTRL\\{container.ToString("D").ToUpperInvariant()}\\{vendorId:X4}_{productId:X4}"
                : $"CTRL\\BT\\{mac}";

            var controller = CreateBaseControllerInfo(deviceId, type.GetDisplayName());

            controller.Provider = "BluetoothLE";
            controller.VendorId = vendorId;
            controller.ProductId = productId;
            controller.Type = type;
            controller.ContainerId = usableContainer ? container.ToString("D") : null;
            controller.PnpInstanceId = instanceId;
            controller.ConnectionType = ConnectionType.Bluetooth;
            controller.BluetoothAddress = mac;
            controller.SerialNumber = mac;
            controller.SupportsWireless = true;
            controller.SupportsVibration = type.IsXbox() || type.IsPlayStation() || type.IsNintendo();

            if (!string.IsNullOrWhiteSpace(endpoint.Name))
                controller.Name = endpoint.Name;

            DiagnosticLogger.Info(
                $"[BluetoothLE] accept addr={mac} vid=0x{vendorId:X4} pid=0x{productId:X4} type={type} " +
                $"container={controller.ContainerId ?? "<none>"} name='{controller.Name}'");

            return controller;
        }

        public override Task StartMonitoringAsync()
        {
            if (!IsAvailable || _isMonitoring) return Task.CompletedTask;
            _isMonitoring = true;
            _logger.LogInformation("Bluetooth LE controller monitoring started");
            return Task.CompletedTask;
        }

        public override Task StopMonitoringAsync()
        {
            _isMonitoring = false;
            _logger.LogInformation("Bluetooth LE controller monitoring stopped");
            return Task.CompletedTask;
        }
    }
}

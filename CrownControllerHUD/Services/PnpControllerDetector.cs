using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Detects controllers from the Windows Plug-and-Play device tree.
    ///
    /// This exists because a modern Xbox controller connected over Bluetooth LE is exposed
    /// as a HID-over-GATT device (<c>BTHLEDEVICE\{00001812-...}</c>) that HidSharp cannot
    /// enumerate and that XInput does not report. Windows also creates internal
    /// <c>BUTTONCONVERTER\CONVERTEDDEVICE</c> collections whose reported VID is 0x045E and
    /// PID is 0x0000; those are not controllers and are explicitly rejected here.
    ///
    /// Every emitted controller carries a Windows container id, which lets the manager
    /// collapse the many nodes/collections of one physical controller into one entry.
    /// </summary>
    public class PnpControllerDetector : BaseControllerDetector
    {
        // Buses that can carry a game controller. BTHLE is enumerated for name enrichment
        // only (it is the Bluetooth parent node, never a controller by itself).
        private static readonly string[] ControllerEnumerators = { "BTHLEDEVICE", "BTHENUM", "XNACOMPOSITE", "HID" };
        private const string BluetoothLeEnumerator = "BTHLE";

        // Bluetooth HID-over-GATT service UUID (Human Interface Device service).
        private const string HidOverGattService = "00001812-0000-1000-8000-00805F9B34FB";

        private readonly HashSet<string> _loggedNodes = new(StringComparer.OrdinalIgnoreCase);
        private string _lastControllerSignature = string.Empty;
        private string _lastXInputSignature = string.Empty;

        public override string Name => "PnP / Bluetooth LE (device tree)";

        public override bool IsAvailable => true;

        public PnpControllerDetector(ILogger<PnpControllerDetector> logger) : base(logger)
        {
            DiagnosticLogger.Info("PnpControllerDetector ctor: end");
        }

        public override async Task InitializeAsync()
        {
            DiagnosticLogger.Info("PnpControllerDetector: initialize");
            LogXInputSlots(force: true);
            await Task.CompletedTask;
        }

        public override Task<IEnumerable<ControllerInfo>> DetectControllersAsync()
        {
            var results = new List<ControllerInfo>();

            try
            {
                // Sibling display names keyed by container (so a BLE gamepad, whose own
                // description is generic, can show the real product name).
                var namesByContainer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var instanceId in PnpDeviceEnumerator.EnumeratePresentInstanceIds(BluetoothLeEnumerator))
                {
                    var record = PnpDeviceEnumerator.Describe(instanceId);
                    if (PnpDeviceEnumerator.IsUsableContainer(record.ContainerIdRaw) &&
                        !string.IsNullOrWhiteSpace(record.FriendlyName))
                    {
                        namesByContainer[record.ContainerId.ToString("D")] = record.FriendlyName!;
                    }
                }

                // Key -> best candidate for that physical device.
                var best = new Dictionary<string, (int Priority, ControllerInfo Info)>(StringComparer.OrdinalIgnoreCase);

                foreach (var enumerator in ControllerEnumerators)
                {
                    foreach (var instanceId in PnpDeviceEnumerator.EnumeratePresentInstanceIds(enumerator))
                    {
                        PnpDeviceRecord record;
                        try
                        {
                            record = PnpDeviceEnumerator.Describe(instanceId);
                        }
                        catch
                        {
                            continue;
                        }

                        bool accepted = IsGamepadNode(record, out int priority);
                        LogNodeDecision(record, accepted, priority);

                        if (!accepted)
                            continue;

                        var controller = BuildController(record, namesByContainer);
                        if (controller == null)
                            continue;

                        var key = controller.DeviceId;
                        if (best.TryGetValue(key, out var existing))
                        {
                            if (priority > existing.Priority)
                                best[key] = (priority, controller);
                        }
                        else
                        {
                            best[key] = (priority, controller);
                        }
                    }
                }

                results.AddRange(best.Values.Select(v => v.Info));

                // Keep the detector's own known-controller map in step with reality.
                lock (_lock)
                {
                    var currentIds = results.Select(c => c.DeviceId).ToHashSet();
                    foreach (var id in _knownControllers.Keys.Where(k => !currentIds.Contains(k)).ToList())
                        OnControllerRemoved(id);

                    foreach (var controller in results)
                    {
                        if (_knownControllers.ContainsKey(controller.DeviceId))
                        {
                            controller.ConnectedAt = _knownControllers[controller.DeviceId].ConnectedAt;
                            _knownControllers[controller.DeviceId] = controller;
                            OnControllerUpdated(controller);
                        }
                        else
                        {
                            OnControllerDetected(controller);
                        }
                    }
                }

                LogControllerSummary(results);
                LogXInputSlots(force: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PnP controller detection failed");
            }

            return Task.FromResult<IEnumerable<ControllerInfo>>(results);
        }

        /// <summary>Decides whether a single device node is a game controller.</summary>
        private static bool IsGamepadNode(PnpDeviceRecord record, out int priority)
        {
            priority = 0;
            var id = record.InstanceId;

            // Windows-internal / aggregate nodes must never be treated as controllers.
            if (id.IndexOf("CONVERTEDDEVICE", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (id.IndexOf("HID_DEVICE_SYSTEM_VHF", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // A device that reports PID 0 is not a real controller (aggregate nodes do this).
            if (record.HasVidPid && record.ProductId == 0) return false;

            switch (record.Enumerator)
            {
                case "BTHLEDEVICE":
                    // Only the HID-over-GATT service node is the gamepad.
                    if (id.IndexOf("00001812", StringComparison.OrdinalIgnoreCase) < 0) return false;
                    if (record.HasVidPid || LooksLikeGamepad(record.SearchText))
                    {
                        priority = 40;
                        return true;
                    }
                    return false;

                case "BTHENUM":
                    if (!record.HasVidPid) return false;
                    if (KnownControllerIds.TryIdentifyExact(record.VendorId, record.ProductId, out _) ||
                        (KnownControllerIds.IsGamepadVendor(record.VendorId) && LooksLikeGamepad(record.SearchText)))
                    {
                        priority = 35;
                        return true;
                    }
                    return false;

                case "XNACOMPOSITE":
                    if (record.HasVidPid && KnownControllerIds.TryIdentifyExact(record.VendorId, record.ProductId, out _))
                    {
                        priority = 30;
                        return true;
                    }
                    return false;

                case "HID":
                    if (!record.HasVidPid) return false;
                    // Exact VID/PID match is authoritative.
                    if (KnownControllerIds.TryIdentifyExact(record.VendorId, record.ProductId, out _))
                    {
                        priority = 20;
                        return true;
                    }
                    // Otherwise require a known gamepad vendor AND a gamepad-ish description,
                    // so Microsoft keyboards/mice/devices are not mistaken for controllers.
                    if (KnownControllerIds.IsGamepadVendor(record.VendorId) && LooksLikeGamepad(record.SearchText))
                    {
                        priority = 15;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private static ControllerInfo? BuildController(PnpDeviceRecord record, Dictionary<string, string> namesByContainer)
        {
            var info = new ControllerInfo
            {
                Provider = "PnP",
                PnpInstanceId = record.InstanceId,
                ContainerId = PnpDeviceEnumerator.IsUsableContainer(record.ContainerIdRaw)
                    ? record.ContainerId.ToString("D")
                    : null,
                VendorId = record.VendorId,
                ProductId = record.ProductId,
                // Enumerating a node proves the device is *installed/present*, NOT that it is
                // switched on: a paired Bluetooth controller keeps every node present while
                // powered off. Connection state is resolved by ControllerManager from a live
                // signal; never asserted here.
                ConnectionState = ControllerConnectionState.Unknown,
                IsConnected = false
            };

            info.Type = record.HasVidPid
                ? KnownControllerIds.IdentifyController(record.VendorId, record.ProductId)
                : ControllerType.GenericHID;

            if (info.Type == ControllerType.Unknown && LooksLikeGamepad(record.SearchText))
                info.Type = ControllerType.GenericHID;

            info.ConnectionType = DetermineTransport(record);
            info.SupportsWireless = info.ConnectionType != ConnectionType.USB;
            info.SupportsVibration = info.Type.IsXbox() || info.Type.IsPlayStation() || info.Type.IsNintendo();

            string? name = null;
            if (info.ContainerId != null && namesByContainer.TryGetValue(info.ContainerId, out var sibling))
                name = sibling;
            name ??= record.DisplayName;
            info.Name = string.IsNullOrWhiteSpace(name) ? info.Type.GetDisplayName() : name!;

            if (info.ConnectionType == ConnectionType.Bluetooth ||
                info.ConnectionType == ConnectionType.PlayStationWireless ||
                info.ConnectionType == ConnectionType.NintendoWireless)
            {
                var mac = DeviceIdentity.FromPnpInstanceId(record.InstanceId);
                if (mac != null)
                {
                    info.BluetoothAddress = mac;
                    info.SerialNumber = mac;
                }
            }

            info.DeviceId = BuildDeviceId(info, record);
            return info;
        }

        private static string BuildDeviceId(ControllerInfo info, PnpDeviceRecord record)
        {
            if (!string.IsNullOrWhiteSpace(info.ContainerId))
                return $"CTRL\\{info.ContainerId!.ToUpperInvariant()}";
            return $"PNP\\{record.InstanceId.ToUpperInvariant()}";
        }

        private static ConnectionType DetermineTransport(PnpDeviceRecord record)
        {
            var id = record.InstanceId;

            if (id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                id.IndexOf("BLUETOOTH", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ConnectionType.Bluetooth;
            }

            // The HID-over-GATT service UUID only appears on Bluetooth LE HID devices.
            if (id.IndexOf("00001812", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConnectionType.Bluetooth;

            if (id.IndexOf("XBOXWIRELESS", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConnectionType.XboxWirelessAdapter;

            return record.Enumerator switch
            {
                "USB" or "XNACOMPOSITE" or "HID" => ConnectionType.USB,
                _ => ConnectionType.Unknown
            };
        }

        private static bool LooksLikeGamepad(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            return text.Contains("gamepad")
                || text.Contains("joystick")
                || text.Contains("xinput")
                || text.Contains("game controller")
                || text.Contains("wireless controller")
                || text.Contains("dualshock")
                || text.Contains("dualsense")
                || text.Contains("pro controller")
                || text.Contains("xbox");
        }

        /// <summary>
        /// Finds the 12-hex-digit Bluetooth address embedded in an instance id. The last
        /// such run is used because the HID-over-GATT instance id starts with a service
        /// GUID (also 12 hex digits, e.g. 00001812-...-00805F9B34FB) before the device MAC.
        /// </summary>
        private static string? ExtractBluetoothAddress(string instanceId)
            => DeviceIdentity.FromPnpInstanceId(instanceId);

        private void LogNodeDecision(PnpDeviceRecord record, bool accepted, int priority)
        {
            // Only log nodes that are plausible controllers, once each, to keep the log usable.
            bool interesting = record.Enumerator == "BTHLEDEVICE"
                || (record.HasVidPid && KnownControllerIds.IsGamepadVendor(record.VendorId))
                || record.InstanceId.IndexOf("CONVERTED", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!interesting) return;

            lock (_loggedNodes)
            {
                if (!_loggedNodes.Add(record.InstanceId))
                    return;
            }

            var container = PnpDeviceEnumerator.IsUsableContainer(record.ContainerIdRaw) ? record.ContainerIdRaw : "<none>";
            var vid = record.HasVidPid ? $"0x{record.VendorId:X4}" : "----";
            var pid = record.HasVidPid ? $"0x{record.ProductId:X4}" : "----";

            DiagnosticLogger.Info(
                $"[PnP] {(accepted ? "ACCEPT" : "reject")} prio={priority} bus={record.Enumerator} " +
                $"vid={vid} pid={pid} container={container} name='{record.DisplayName}' id={record.InstanceId}");
        }

        private void LogControllerSummary(List<ControllerInfo> controllers)
        {
            var signature = string.Join("|", controllers
                .OrderBy(c => c.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{c.DeviceId}:{c.Type}:{c.ConnectionType}:{c.Name}"));

            if (signature == _lastControllerSignature) return;
            _lastControllerSignature = signature;

            DiagnosticLogger.Info($"[PnP] detected {controllers.Count} controller(s)");
            foreach (var c in controllers)
            {
                DiagnosticLogger.Info(
                    $"[PnP] controller provider={c.Provider} name='{c.Name}' type={c.Type} transport={c.ConnectionType} " +
                    $"vid=0x{c.VendorId:X4} pid=0x{c.ProductId:X4} container={c.ContainerId ?? "<none>"} " +
                    $"serial={c.SerialNumber ?? "<none>"} instance={c.PnpInstanceId}");
            }
        }

        private void LogXInputSlots(bool force)
        {
            string signature;
            XInputInterop.SlotInfo[] slots;
            try
            {
                slots = XInputInterop.EnumerateSlots();
            }
            catch
            {
                return;
            }

            var connected = slots.Where(s => s.Connected).ToList();
            signature = string.Join("|", slots.Select(s => s.ToString()));

            if (!force && signature == _lastXInputSignature) return;
            _lastXInputSignature = signature;

            DiagnosticLogger.Info($"[XInput] {connected.Count} XInput slot(s) occupied: {string.Join("; ", slots.Select(s => s.ToString()))}");
        }

        public override Task StartMonitoringAsync()
        {
            if (_isMonitoring) return Task.CompletedTask;
            _isMonitoring = true;
            _logger.LogInformation("PnP controller monitoring started");
            return Task.CompletedTask;
        }

        public override Task StopMonitoringAsync()
        {
            _isMonitoring = false;
            _logger.LogInformation("PnP controller monitoring stopped");
            return Task.CompletedTask;
        }
    }
}

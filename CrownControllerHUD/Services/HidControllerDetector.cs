using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using HidSharp;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.Services
{
    public class HidControllerDetector : BaseControllerDetector, IDisposable
    {
        private readonly List<HidDevice> _monitoredDevices = new();
        private readonly Dictionary<string, HidStream> _deviceStreams = new();
        private readonly System.Threading.Timer? _pollTimer;
        private bool _disposed = false;

        public override string Name => "HID (HidSharp)";

        public override bool IsAvailable => true;

        public HidControllerDetector(ILogger<HidControllerDetector> logger) : base(logger)
        {
            _pollTimer = new System.Threading.Timer(async _ => await PollDevicesAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public override async Task InitializeAsync()
        {
            _logger.LogInformation("Initializing HID Controller Detector");
            
            await DetectControllersAsync();
            
            _logger.LogInformation("HID Controller Detector initialized");
        }

        public override async Task<IEnumerable<ControllerInfo>> DetectControllersAsync()
        {
            var controllers = new List<ControllerInfo>();

            try
            {
                var deviceList = DeviceList.Local;
                var hidDevices = deviceList.GetHidDevices().ToList();

                foreach (var device in hidDevices)
                {
                    try
                    {
                        // Filter for gamepad-like devices
                        if (!IsGamepadDevice(device)) continue;

                        var deviceId = GetDeviceId(device);
                        
                        lock (_lock)
                        {
                            if (!_knownControllers.ContainsKey(deviceId) && !_monitoredDevices.Contains(device))
                            {
                                var controller = CreateControllerFromHidDevice(device);
                                if (controller != null)
                                {
                                    controllers.Add(controller);
                                    _monitoredDevices.Add(device);
                                    OnControllerDetected(controller);
                                }
                            }
                            else if (_knownControllers.TryGetValue(deviceId, out var existing))
                            {
                                // Update existing
                                var updated = CreateControllerFromHidDevice(device);
                                if (updated != null)
                                {
                                    updated.ConnectedAt = existing.ConnectedAt;
                                    OnControllerUpdated(updated);
                                    controllers.Add(updated);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error processing HID device {Device}", device.DevicePath);
                    }
                }

                // Check for removed devices
                var currentDeviceIds = hidDevices.Select(GetDeviceId).ToHashSet();
                var removedIds = _knownControllers.Keys.Where(id => !currentDeviceIds.Contains(id)).ToList();
                foreach (var id in removedIds)
                {
                    lock (_lock)
                    {
                        OnControllerRemoved(id);
                        _monitoredDevices.RemoveAll(d => GetDeviceId(d) == id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting controllers via HID");
            }

            return controllers;
        }

        private bool IsGamepadDevice(HidDevice device)
        {
            // Check for known gamepad VID/PID combinations
            var type = KnownControllerIds.IdentifyController((ushort)device.VendorID, (ushort)device.ProductID);
            if (type != ControllerType.Unknown) return true;

            // Check for generic HID gamepad (Usage Page 0x01, Usage 0x05 - Gamepad)
            try
            {
                // var caps = device.GetCapacities(); // This method might not exist
                // This is a simplified check - in reality we'd parse the report descriptor
                return device.VendorID == 0x045E || // Microsoft
                       device.VendorID == 0x054C || // Sony
                       device.VendorID == 0x057E || // Nintendo
                       device.VendorID == 0x28DE;   // Valve
            }
            catch
            {
                return false;
            }
        }

        private string GetDeviceId(HidDevice device)
        {
            return $"HID\\VID_{device.VendorID:X4}&PID_{device.ProductID:X4}&MI_00\\{device.DevicePath}";
        }

        private ControllerInfo? CreateControllerFromHidDevice(HidDevice device)
        {
            try
            {
                var controller = CreateBaseControllerInfo(GetDeviceId(device), device.GetFriendlyName() ?? "HID Gamepad");
                
                controller.VendorId = (ushort)device.VendorID;
                controller.ProductId = (ushort)device.ProductID;
                controller.Type = KnownControllerIds.IdentifyController((ushort)device.VendorID, (ushort)device.ProductID);
                
                // Determine connection type from device path
                controller.ConnectionType = DetermineConnectionType(device);
                
                // Try to read battery info via HID
                ReadBatteryInfo(device, controller);
                
                // Capabilities
                controller.SupportsVibration = HasVibrationSupport(device);
                controller.SupportsBatteryReporting = controller.BatteryPercentage.HasValue;
                controller.SupportsChargingStatus = controller.IsCharging || controller.BatteryState == BatteryState.Charging;
                controller.SupportsWireless = controller.ConnectionType != ConnectionType.USB;

                return controller;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error creating controller from HID device");
                return null;
            }
        }

        private ConnectionType DetermineConnectionType(HidDevice device)
        {
            var path = device.DevicePath.ToLower();
            
            if (path.Contains("bluetooth") || path.Contains("bt_"))
                return ConnectionType.Bluetooth;
            if (path.Contains("xbox") || path.Contains("wireless"))
                return ConnectionType.XboxWirelessAdapter;
            
            return ConnectionType.USB;
        }

        private void ReadBatteryInfo(HidDevice device, ControllerInfo controller)
        {
            try
            {
                // Xbox controllers use specific HID reports for battery
                if (device.VendorID == 0x045E) // Microsoft
                {
                    ReadXboxBatteryInfo(device, controller);
                }
                else if (device.VendorID == 0x054C) // Sony
                {
                    ReadPlayStationBatteryInfo(device, controller);
                }
                else if (device.VendorID == 0x057E) // Nintendo
                {
                    ReadNintendoBatteryInfo(device, controller);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read battery info for {Device}", device.DevicePath);
            }
        }

        private void ReadXboxBatteryInfo(HidDevice device, ControllerInfo controller)
        {
            // Xbox One/Series controllers report battery via HID feature report 0x09 (or 0x04 for older)
            // Elite Series 2 uses report 0x09 with specific format
            // This is a simplified implementation - real implementation needs proper report parsing
            
            try
            {
                var stream = device.Open();
                if (stream != null)
                {
                    // Try to read feature report 0x09 (Xbox battery report)
                    var buffer = new byte[64];
                    buffer[0] = 0x09; // Report ID
                    
                    if (stream.CanRead)
                    {
                        stream.GetFeature(buffer, 0, buffer.Length);
                        ParseXboxBatteryReport(buffer, controller);
                    }
                    
                    stream.Close();
                }
            }
            catch
            {
                // Ignore - not all devices support feature reports
            }
        }

        private void ParseXboxBatteryReport(byte[] report, ControllerInfo controller)
        {
            // Xbox battery report format (Report ID 0x09):
            // Byte 0: Report ID (0x09)
            // Byte 1: Battery level (0-100) or 0xFF for unknown
            // Byte 2: Charging state (0=discharging, 1=charging, 2=full, 3=wired)
            // Byte 3-5: Additional info (battery type, etc.)
            
            if (report.Length >= 3 && report[0] == 0x09)
            {
                var level = report[1];
                var chargingState = report[2];
                
                if (level != 0xFF && level <= 100)
                {
                    controller.BatteryPercentage = level;
                    controller.BatteryLastUpdated = DateTime.Now;
                    
                    controller.BatteryState = level switch
                    {
                        >= 95 => BatteryState.Full,
                        >= 50 => BatteryState.High,
                        >= 15 => BatteryState.Medium,
                        >= 5 => BatteryState.Low,
                        _ => BatteryState.Critical
                    };
                    
                    controller.IsCharging = chargingState == 1;
                    controller.SupportsChargingStatus = true;
                    
                    if (chargingState == 2) controller.BatteryState = BatteryState.Full;
                    if (chargingState == 3) controller.BatteryState = BatteryState.Full; // Wired
                }
            }
        }

        private void ReadPlayStationBatteryInfo(HidDevice device, ControllerInfo controller)
        {
            // DualShock 4 / DualSense battery reading
            // Would need specific report parsing
        }

        private void ReadNintendoBatteryInfo(HidDevice device, ControllerInfo controller)
        {
            // Nintendo Switch Pro Controller / Joy-Con battery reading
        }

        private bool HasVibrationSupport(HidDevice device)
        {
            // Check if device supports vibration (force feedback)
            // This requires parsing the HID report descriptor for PID page
            return device.VendorID == 0x045E || device.VendorID == 0x054C || device.VendorID == 0x057E;
        }

        private async Task PollDevicesAsync()
        {
            if (!_isMonitoring || _disposed) return;

            try
            {
                await DetectControllersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling HID devices");
            }
        }

        public override async Task StartMonitoringAsync()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;
            _logger.LogInformation("HID controller monitoring started");
            
            // Start polling timer (every 5 seconds)
            _pollTimer?.Change(0, 5000);
            
            await Task.CompletedTask;
        }

        public override async Task StopMonitoringAsync()
        {
            _isMonitoring = false;
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            
            // Close all streams
            foreach (var stream in _deviceStreams.Values)
            {
                try { stream.Close(); } catch { }
            }
            _deviceStreams.Clear();
            
            _logger.LogInformation("HID controller monitoring stopped");
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _pollTimer?.Dispose();
                StopMonitoringAsync().Wait();
                _disposed = true;
            }
        }
    }
}
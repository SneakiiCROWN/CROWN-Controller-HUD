using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Bluetooth LE Controller Detector
    /// Uses reflection to access Windows.Devices.Bluetooth APIs when CsWinRT projections are not available.
    /// </summary>
    public class BluetoothLEControllerDetector : BaseControllerDetector
    {
        private readonly Dictionary<string, object> _bleDevices = new();
        private object? _deviceWatcher;
        private bool _bleAvailable = false;
        private Type? _bluetoothLEDeviceType;
        private Type? _bluetoothDeviceType;
        private Type? _deviceInformationType;
        private Type? _gattServiceUuidsType;
        private Type? _gattCharacteristicUuidsType;
        private MethodInfo? _fromIdAsyncMethod;
        private MethodInfo? _getGattServiceMethod;
        private MethodInfo? _getCharacteristicsAsyncMethod;
        private PropertyInfo? _nameProperty;
        private PropertyInfo? _deviceInformationProperty;
        private PropertyInfo? _uuidsProperty;
        private MethodInfo? _writeClientCharacteristicConfigurationDescriptorAsyncMethod;
        private EventInfo? _valueChangedEvent;

        public override string Name => "Bluetooth LE";

        public override bool IsAvailable => _bleAvailable;

        public BluetoothLEControllerDetector(ILogger<BluetoothLEControllerDetector> logger) : base(logger) 
        {
            InitializeBLETypes();
        }

        private void InitializeBLETypes()
        {
            try
            {
                // Try to load Windows.Devices.Bluetooth
                var bluetoothAssembly = Assembly.Load("Windows.Devices.Bluetooth");
                if (bluetoothAssembly != null)
                {
                    _bluetoothLEDeviceType = bluetoothAssembly.GetType("Windows.Devices.Bluetooth.BluetoothLEDevice");
                    _bluetoothDeviceType = bluetoothAssembly.GetType("Windows.Devices.Bluetooth.BluetoothDevice");
                }

                // Try to load Windows.Devices.Bluetooth.GenericAttributeProfile
                var gattAssembly = Assembly.Load("Windows.Devices.Bluetooth.GenericAttributeProfile");
                if (gattAssembly != null)
                {
                    _gattServiceUuidsType = gattAssembly.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids");
                    _gattCharacteristicUuidsType = gattAssembly.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicUuids");
                }

                // Try to load Windows.Devices.Enumeration
                var devicesAssembly = Assembly.Load("Windows.Devices.Enumeration");
                if (devicesAssembly != null)
                {
                    _deviceInformationType = devicesAssembly.GetType("Windows.Devices.Enumeration.DeviceInformation");
                }

                if (_bluetoothLEDeviceType != null)
                {
                    _fromIdAsyncMethod = _bluetoothLEDeviceType.GetMethod("FromIdAsync", BindingFlags.Public | BindingFlags.Static);
                    _getGattServiceMethod = _bluetoothLEDeviceType.GetMethod("GetGattService");
                    _nameProperty = _bluetoothLEDeviceType.GetProperty("Name");
                    _deviceInformationProperty = _bluetoothLEDeviceType.GetProperty("DeviceInformation");
                }

                _bleAvailable = _bluetoothLEDeviceType != null && _fromIdAsyncMethod != null;
                
                if (_bleAvailable)
                {
                    _logger.LogInformation("Bluetooth LE types loaded successfully via reflection");
                }
                else
                {
                    _logger.LogWarning("Bluetooth LE types not available - Windows.Devices.Bluetooth not found. Install Windows SDK for full BLE support.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Bluetooth LE types via reflection. BLE detector will be disabled.");
                _bleAvailable = false;
            }
        }

        public override async Task InitializeAsync()
        {
            if (!_bleAvailable)
            {
                _logger.LogInformation("Bluetooth LE Controller Detector skipped (not available)");
                return;
            }

            _logger.LogInformation("Initializing Bluetooth LE Controller Detector");
            
            // For now, we'll skip the device watcher since it requires full WinRT projections
            // In a full implementation with Windows SDK, we'd set up a DeviceWatcher here
            
            _logger.LogInformation("Bluetooth LE Controller Detector initialized (passive mode)");
            await Task.CompletedTask;
        }

        public override async Task<IEnumerable<ControllerInfo>> DetectControllersAsync()
        {
            var controllers = new List<ControllerInfo>();

            if (!_bleAvailable) return controllers;

            try
            {
                // In passive mode, we can't enumerate devices without DeviceWatcher
                // This would require full WinRT projections
                List<KeyValuePair<string, object>> devices;
                lock (_lock)
                {
                    devices = _bleDevices.ToList();
                }
                
                foreach (var kvp in devices)
                {
                    var controller = await CreateControllerFromBLEDeviceAsync(kvp.Value, kvp.Key);
                    if (controller != null)
                    {
                        controllers.Add(controller);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting controllers via Bluetooth LE");
            }

            return controllers;
        }

        private async Task<ControllerInfo?> CreateControllerFromBLEDeviceAsync(object bleDevice, string deviceId)
        {
            try
            {
                var name = _nameProperty?.GetValue(bleDevice)?.ToString() ?? "";
                var controller = CreateBaseControllerInfo(deviceId, name ?? "Bluetooth Controller");
                
                // Identify by name
                controller.Type = IdentifyByName(name);
                controller.ConnectionType = ConnectionType.Bluetooth;
                controller.SupportsWireless = true;
                
                // Battery reading would require GATT service access
                // For now, we skip battery reading in reflection mode
                
                return controller;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error creating controller from BLE device");
                return null;
            }
        }

        private ControllerType IdentifyByName(string name)
        {
            var lower = name.ToLower();
            
            if (lower.Contains("elite series 2")) return ControllerType.XboxEliteSeries2;
            if (lower.Contains("elite")) return ControllerType.XboxOneElite;
            if (lower.Contains("xbox series")) return ControllerType.XboxSeriesX;
            if (lower.Contains("xbox")) return ControllerType.XboxOneS;
            if (lower.Contains("dualsense")) return ControllerType.PlayStation5;
            if (lower.Contains("dualshock") || lower.Contains("ps4")) return ControllerType.PlayStation4;
            if (lower.Contains("pro controller")) return ControllerType.NintendoSwitchPro;
            if (lower.Contains("joy-con") || lower.Contains("joycon"))
            {
                if (lower.Contains("left") || lower.Contains("(l)")) return ControllerType.NintendoSwitchJoyConLeft;
                if (lower.Contains("right") || lower.Contains("(r)")) return ControllerType.NintendoSwitchJoyConRight;
                return ControllerType.NintendoSwitchJoyConLeft;
            }
            if (lower.Contains("steam deck")) return ControllerType.SteamDeck;
            if (lower.Contains("steam")) return ControllerType.SteamController;
            
            return ControllerType.GenericHID;
        }

        public override async Task StartMonitoringAsync()
        {
            if (!_bleAvailable || _isMonitoring) return;
            _isMonitoring = true;
            _logger.LogInformation("Bluetooth LE controller monitoring started (passive mode)");
            await Task.CompletedTask;
        }

        public override async Task StopMonitoringAsync()
        {
            _isMonitoring = false;
            _logger.LogInformation("Bluetooth LE controller monitoring stopped");
            await Task.CompletedTask;
        }
    }
}
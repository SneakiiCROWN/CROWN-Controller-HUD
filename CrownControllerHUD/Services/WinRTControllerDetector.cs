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
    /// WinRT Controller Detector using Windows.Gaming.Input
    /// This uses reflection to work with WinRT types when CsWinRT projections are not fully available.
    /// </summary>
    public class WinRTControllerDetector : BaseControllerDetector
    {
        private readonly List<object> _gamepads = new();
        private bool _winRTAvailable = false;
        private Type? _gamepadType;
        private Type? _batteryType;
        private PropertyInfo? _deviceIdProperty;
        private PropertyInfo? _vendorIdProperty;
        private PropertyInfo? _productIdProperty;
        private PropertyInfo? _batteryProperty;
        private PropertyInfo? _headsetProperty;
        private MethodInfo? _getReportMethod;
        private PropertyInfo? _levelProperty;
        private MethodInfo? _gamepadsGetter;
        private Type? _deviceInformationType;
        private MethodInfo? _createFromIdAsyncMethod;
        private PropertyInfo? _propertiesProperty;

        public override string Name => "WinRT (Windows.Gaming.Input)";

        public override bool IsAvailable => _winRTAvailable;

        public WinRTControllerDetector(ILogger<WinRTControllerDetector> logger) : base(logger) 
        {
            InitializeWinRTTypes();
        }

        private void InitializeWinRTTypes()
        {
            try
            {
                // Try to load Windows.Gaming.Input assembly
                var gamingInputAssembly = Assembly.Load("Windows.Gaming.Input");
                if (gamingInputAssembly != null)
                {
                    _gamepadType = gamingInputAssembly.GetType("Windows.Gaming.Input.Gamepad");
                    _batteryType = gamingInputAssembly.GetType("Windows.Gaming.Input.GamepadBattery");
                    
                    if (_gamepadType != null)
                    {
                        _gamepadsGetter = _gamepadType.GetProperty("Gamepads", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                        _deviceIdProperty = _gamepadType.GetProperty("DeviceId");
                        _vendorIdProperty = _gamepadType.GetProperty("VendorId");
                        _productIdProperty = _gamepadType.GetProperty("ProductId");
                        _batteryProperty = _gamepadType.GetProperty("Battery");
                        _headsetProperty = _gamepadType.GetProperty("Headset");
                    }

                    if (_batteryType != null)
                    {
                        _getReportMethod = _batteryType.GetMethod("GetReport");
                        var reportType = gamingInputAssembly.GetType("Windows.Gaming.Input.GamepadBatteryReport");
                        if (reportType != null)
                        {
                            _levelProperty = reportType.GetProperty("Level");
                        }
                    }
                }

                // Try to load Windows.Devices.Enumeration
                var devicesAssembly = Assembly.Load("Windows.Devices.Enumeration");
                if (devicesAssembly != null)
                {
                    _deviceInformationType = devicesAssembly.GetType("Windows.Devices.Enumeration.DeviceInformation");
                    _createFromIdAsyncMethod = _deviceInformationType?.GetMethod("CreateFromIdAsync", BindingFlags.Public | BindingFlags.Static);
                    _propertiesProperty = _deviceInformationType?.GetProperty("Properties");
                }

                _winRTAvailable = _gamepadType != null && _gamepadsGetter != null;
                
                if (_winRTAvailable)
                {
                    _logger.LogInformation("WinRT types loaded successfully via reflection");
                }
                else
                {
                    _logger.LogWarning("WinRT types not available - Windows.Gaming.Input not found. Install Windows SDK for full WinRT support.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load WinRT types via reflection. WinRT detector will be disabled.");
                _winRTAvailable = false;
            }
        }

        public override async Task InitializeAsync()
        {
            if (!_winRTAvailable)
            {
                _logger.LogInformation("WinRT Controller Detector skipped (not available)");
                return;
            }

            _logger.LogInformation("Initializing WinRT Controller Detector");
            
            try
            {
                // Initialize Gamepad
                var gamepads = _gamepadsGetter?.Invoke(null, null);
                if (gamepads is System.Collections.IEnumerable enumerable)
                {
                    foreach (var gp in enumerable)
                    {
                        _gamepads.Add(gp);
                    }
                }
                
                _logger.LogInformation("WinRT Controller Detector initialized. Found {Count} gamepads", _gamepads.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing WinRT detector");
            }

            await Task.CompletedTask;
        }

        public override async Task<IEnumerable<ControllerInfo>> DetectControllersAsync()
        {
            var controllers = new List<ControllerInfo>();

            if (!_winRTAvailable) return controllers;

            try
            {
                // Refresh gamepad list
                var currentGamepadsObj = _gamepadsGetter?.Invoke(null, null);
                var currentGamepads = new List<object>();
                
                if (currentGamepadsObj is System.Collections.IEnumerable enumerable)
                {
                    foreach (var gp in enumerable)
                    {
                        currentGamepads.Add(gp);
                    }
                }

                lock (_lock)
                {
                    // Check for new gamepads
                    foreach (var gp in currentGamepads)
                    {
                        if (!_gamepads.Contains(gp))
                        {
                            _gamepads.Add(gp);
                            var controller = CreateControllerFromGamepad(gp);
                            if (controller != null)
                            {
                                controllers.Add(controller);
                                OnControllerDetected(controller);
                            }
                        }
                    }

                    // Check for removed gamepads
                    var removedGamepads = _gamepads.Where(gp => !currentGamepads.Contains(gp)).ToList();
                    foreach (var gp in removedGamepads)
                    {
                        _gamepads.Remove(gp);
                        var deviceId = _deviceIdProperty?.GetValue(gp)?.ToString() ?? "";
                        OnControllerRemoved(deviceId);
                    }

                    // Update existing gamepads
                    foreach (var gp in currentGamepads)
                    {
                        var controller = CreateControllerFromGamepad(gp);
                        if (controller != null && _knownControllers.TryGetValue(controller.DeviceId, out var existing))
                        {
                            controller.ConnectedAt = existing.ConnectedAt;
                            OnControllerUpdated(controller);
                            controllers.Add(controller);
                        }
                        else if (controller != null)
                        {
                            controllers.Add(controller);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting controllers via WinRT");
            }

            return controllers;
        }

        private ControllerInfo? CreateControllerFromGamepad(object gamepad)
        {
            try
            {
                var deviceId = _deviceIdProperty?.GetValue(gamepad)?.ToString() ?? "";
                var vendorId = (ushort)(_vendorIdProperty?.GetValue(gamepad) ?? (ushort)0);
                var productId = (ushort)(_productIdProperty?.GetValue(gamepad) ?? (ushort)0);

                var controller = CreateBaseControllerInfo(deviceId, "Xbox Controller");
                
                // Identify controller type from VID/PID
                controller.Type = KnownControllerIds.IdentifyController(vendorId, productId);
                controller.VendorId = vendorId;
                controller.ProductId = productId;
                
                // Determine connection type
                controller.ConnectionType = DetermineConnectionType(deviceId);
                
                // Get battery info
                var battery = _batteryProperty?.GetValue(gamepad);
                if (battery != null)
                {
                    controller.HasBattery = true;
                    controller.SupportsBatteryReporting = true;
                    
                    var report = _getReportMethod?.Invoke(battery, null);
                    if (report != null && _levelProperty != null)
                    {
                        var level = (double)(_levelProperty.GetValue(report) ?? 0.0);
                        controller.BatteryPercentage = (int)Math.Round(level * 100);
                        controller.BatteryLastUpdated = DateTime.Now;
                        
                        controller.BatteryState = controller.BatteryPercentage switch
                        {
                            >= 95 => BatteryState.Full,
                            >= 50 => BatteryState.High,
                            >= 15 => BatteryState.Medium,
                            >= 5 => BatteryState.Low,
                            _ => BatteryState.Critical
                        };
                    }
                    
                    controller.SupportsChargingStatus = false;
                }
                else
                {
                    controller.HasBattery = false;
                    controller.SupportsBatteryReporting = false;
                }

                // Capabilities
                var headset = _headsetProperty?.GetValue(gamepad);
                controller.SupportsVibration = true;
                controller.SupportsAudio = headset != null;
                controller.SupportsWireless = controller.ConnectionType != ConnectionType.USB;

                return controller;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating controller info from gamepad");
                return null;
            }
        }

        private ConnectionType DetermineConnectionType(string deviceId)
        {
            var lower = deviceId.ToLower();
            
            if (lower.Contains("bluetooth") || lower.Contains("bt"))
                return ConnectionType.Bluetooth;
            if (lower.Contains("xboxwireless") || lower.Contains("wireless"))
                return ConnectionType.XboxWirelessAdapter;
            if (lower.Contains("usb") || lower.Contains("vid_045e&pid"))
                return ConnectionType.USB;
                
            return ConnectionType.Bluetooth;
        }

        public override async Task StartMonitoringAsync()
        {
            if (!_winRTAvailable || _isMonitoring) return;
            _isMonitoring = true;
            _logger.LogInformation("WinRT controller monitoring started");
            
            await DetectControllersAsync();
        }

        public override async Task StopMonitoringAsync()
        {
            _isMonitoring = false;
            _logger.LogInformation("WinRT controller monitoring stopped");
            await Task.CompletedTask;
        }
    }
}
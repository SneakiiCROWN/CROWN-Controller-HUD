using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrownControllerHUD.Services
{
    public interface IControllerManager
    {
        IReadOnlyList<ControllerInfo> Controllers { get; }
        Task InitializeAsync();
        Task StartAsync();
        Task StopAsync();
        Task RefreshAsync();
        ControllerInfo? GetController(string deviceId);
        event EventHandler<ControllerEventArgs> ControllerConnected;
        event EventHandler<ControllerEventArgs> ControllerDisconnected;
        event EventHandler<ControllerEventArgs> ControllerBatteryChanged;
        event EventHandler<ControllerEventArgs> ControllerChargingChanged;
        event EventHandler<ControllersChangedEventArgs> ControllersChanged;
    }

    public class ControllersChangedEventArgs : EventArgs
    {
        public IReadOnlyList<ControllerInfo> Controllers { get; }
        public ControllersChangedEventArgs(IReadOnlyList<ControllerInfo> controllers)
        {
            Controllers = controllers;
        }
    }

    public class ControllerManager : IControllerManager
    {
        private readonly ILogger<ControllerManager> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private readonly List<IControllerDetector> _detectors = new();
        private readonly Dictionary<string, ControllerInfo> _controllers = new();
        private readonly object _lock = new();
        private bool _isRunning = false;

        public IReadOnlyList<ControllerInfo> Controllers
        {
            get
            {
                lock (_lock)
                {
                    return _controllers.Values
                        .Where(c => _settings.CurrentValue.Controllers.ShowDisconnectedControllers || c.IsConnected)
                        .OrderBy(c => c.Type)
                        .ThenBy(c => c.Name)
                        .ToList();
                }
            }
        }

        public event EventHandler<ControllerEventArgs>? ControllerConnected;
        public event EventHandler<ControllerEventArgs>? ControllerDisconnected;
        public event EventHandler<ControllerEventArgs>? ControllerBatteryChanged;
        public event EventHandler<ControllerEventArgs>? ControllerChargingChanged;
        public event EventHandler<ControllersChangedEventArgs>? ControllersChanged;

        public ControllerManager(
            ILogger<ControllerManager> logger,
            IOptionsMonitor<AppSettings> settings,
            IEnumerable<IControllerDetector> detectors)
        {
            _logger = logger;
            _settings = settings;
            _detectors.AddRange(detectors.Where(d => d.IsAvailable));
            
            foreach (var detector in _detectors)
            {
                detector.ControllerDetected += OnDetectorControllerDetected;
                detector.ControllerRemoved += OnDetectorControllerRemoved;
                detector.ControllerUpdated += OnDetectorControllerUpdated;
            }
        }

        public async Task InitializeAsync()
        {
            _logger.LogInformation("Initializing Controller Manager with {Count} detectors", _detectors.Count);
            
            foreach (var detector in _detectors)
            {
                try
                {
                    await detector.InitializeAsync();
                    _logger.LogInformation("Detector {Name} initialized", detector.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize detector {Name}", detector.Name);
                }
            }
        }

        public async Task StartAsync()
        {
            if (_isRunning) return;
            _isRunning = true;
            
            _logger.LogInformation("Starting Controller Manager");
            
            foreach (var detector in _detectors)
            {
                try
                {
                    await detector.StartMonitoringAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start detector {Name}", detector.Name);
                }
            }

            // Initial detection
            await RefreshAsync();
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;
            _isRunning = false;
            
            _logger.LogInformation("Stopping Controller Manager");
            
            foreach (var detector in _detectors)
            {
                try
                {
                    await detector.StopMonitoringAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stop detector {Name}", detector.Name);
                }
            }
        }

        public async Task RefreshAsync()
        {
            var allControllers = new List<ControllerInfo>();
            
            foreach (var detector in _detectors)
            {
                try
                {
                    var controllers = await detector.DetectControllersAsync();
                    allControllers.AddRange(controllers);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error detecting controllers with {Name}", detector.Name);
                }
            }

            // Merge controllers from different detectors
            lock (_lock)
            {
                var merged = MergeControllers(allControllers);
                
                // Update known controllers
                var currentIds = merged.Select(c => c.DeviceId).ToHashSet();
                var removedIds = _controllers.Keys.Where(id => !currentIds.Contains(id)).ToList();
                
                foreach (var id in removedIds)
                {
                    if (_controllers.TryGetValue(id, out var removed))
                    {
                        removed.IsConnected = false;
                        removed.LastSeen = DateTime.Now;
                        OnControllerDisconnected(removed);
                    }
                }

                foreach (var controller in merged)
                {
                    var isNew = !_controllers.ContainsKey(controller.DeviceId);
                    var previous = _controllers.GetValueOrDefault(controller.DeviceId);
                    
                    _controllers[controller.DeviceId] = controller;
                    
                    if (isNew)
                    {
                        OnControllerConnected(controller);
                    }
                    else if (previous != null)
                    {
                        // Check for battery changes
                        if (previous.BatteryPercentage != controller.BatteryPercentage)
                        {
                            OnControllerBatteryChanged(controller);
                        }
                        if (previous.IsCharging != controller.IsCharging)
                        {
                            OnControllerChargingChanged(controller);
                        }
                    }
                }

                OnControllersChanged();
            }
        }

        public ControllerInfo? GetController(string deviceId)
        {
            lock (_lock)
            {
                return _controllers.GetValueOrDefault(deviceId);
            }
        }

        private List<ControllerInfo> MergeControllers(List<ControllerInfo> controllers)
        {
            // Group by a common identifier (VID/PID + serial if available)
            var groups = controllers
                .GroupBy(c => GetMergeKey(c))
                .Select(g => MergeControllerGroup(g.ToList()))
                .ToList();

            return groups;
        }

        private string GetMergeKey(ControllerInfo controller)
        {
            // Use VID/PID/Serial as merge key
            if (!string.IsNullOrEmpty(controller.SerialNumber))
                return $"{controller.VendorId:X4}:{controller.ProductId:X4}:{controller.SerialNumber}";
            return $"{controller.VendorId:X4}:{controller.ProductId:X4}:{controller.DeviceId}";
        }

        private ControllerInfo MergeControllerGroup(List<ControllerInfo> group)
        {
            if (group.Count == 1) return group[0];

            // Prefer the most complete information
            var merged = group.OrderByDescending(c => GetCompletenessScore(c)).First();
            
            // Merge battery info from all sources
            var batteryInfo = group.Where(c => c.BatteryPercentage.HasValue).OrderByDescending(c => c.BatteryLastUpdated ?? DateTime.MinValue).FirstOrDefault();
            if (batteryInfo != null)
            {
                merged.BatteryPercentage = batteryInfo.BatteryPercentage;
                merged.BatteryState = batteryInfo.BatteryState;
                merged.IsCharging = batteryInfo.IsCharging;
                merged.BatteryLastUpdated = batteryInfo.BatteryLastUpdated;
                merged.SupportsBatteryReporting = true;
            }

            // Merge capabilities
            foreach (var c in group)
            {
                merged.SupportsVibration |= c.SupportsVibration;
                merged.SupportsAudio |= c.SupportsAudio;
                merged.SupportsChargingStatus |= c.SupportsChargingStatus;
                merged.SupportsWireless |= c.SupportsWireless;
                
                if (!string.IsNullOrEmpty(c.FirmwareVersion))
                    merged.FirmwareVersion = c.FirmwareVersion;
                if (!string.IsNullOrEmpty(c.Manufacturer))
                    merged.Manufacturer = c.Manufacturer;
            }

            return merged;
        }

        private int GetCompletenessScore(ControllerInfo c)
        {
            int score = 0;
            if (c.BatteryPercentage.HasValue) score += 10;
            if (!string.IsNullOrEmpty(c.FirmwareVersion)) score += 5;
            if (!string.IsNullOrEmpty(c.Manufacturer)) score += 5;
            if (c.Type != ControllerType.Unknown) score += 3;
            if (c.ConnectionType != ConnectionType.Unknown) score += 2;
            return score;
        }

        private void OnDetectorControllerDetected(object? sender, ControllerEventArgs e)
        {
            // Handled in RefreshAsync
        }

        private void OnDetectorControllerRemoved(object? sender, ControllerEventArgs e)
        {
            lock (_lock)
            {
                if (_controllers.TryGetValue(e.Controller.DeviceId, out var controller))
                {
                    controller.IsConnected = false;
                    controller.LastSeen = DateTime.Now;
                    OnControllerDisconnected(controller);
                    OnControllersChanged();
                }
            }
        }

        private void OnDetectorControllerUpdated(object? sender, ControllerEventArgs e)
        {
            lock (_lock)
            {
                if (_controllers.TryGetValue(e.Controller.DeviceId, out var existing))
                {
                    var batteryChanged = existing.BatteryPercentage != e.Controller.BatteryPercentage;
                    var chargingChanged = existing.IsCharging != e.Controller.IsCharging;
                    
                    _controllers[e.Controller.DeviceId] = e.Controller;
                    
                    if (batteryChanged) OnControllerBatteryChanged(e.Controller);
                    if (chargingChanged) OnControllerChargingChanged(e.Controller);
                    OnControllersChanged();
                }
            }
        }

        private void OnControllerConnected(ControllerInfo controller)
        {
            _logger.LogInformation("Controller connected: {Controller}", controller);
            ControllerConnected?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.Connected));
        }

        private void OnControllerDisconnected(ControllerInfo controller)
        {
            _logger.LogInformation("Controller disconnected: {Controller}", controller);
            ControllerDisconnected?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.Disconnected));
        }

        private void OnControllerBatteryChanged(ControllerInfo controller)
        {
            _logger.LogDebug("Battery changed for {Controller}: {Percentage}%", controller.Name, controller.BatteryPercentage);
            ControllerBatteryChanged?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.BatteryChanged));
        }

        private void OnControllerChargingChanged(ControllerInfo controller)
        {
            _logger.LogDebug("Charging state changed for {Controller}: {Charging}", controller.Name, controller.IsCharging);
            ControllerChargingChanged?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.ChargingStateChanged));
        }

        private void OnControllersChanged()
        {
            ControllersChanged?.Invoke(this, new ControllersChangedEventArgs(Controllers));
        }
    }
}
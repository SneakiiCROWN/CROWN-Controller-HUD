using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.Services
{
    public interface IControllerDetector
    {
        string Name { get; }
        bool IsAvailable { get; }
        Task InitializeAsync();
        Task<IEnumerable<ControllerInfo>> DetectControllersAsync();
        Task StartMonitoringAsync();
        Task StopMonitoringAsync();
        event EventHandler<ControllerEventArgs> ControllerDetected;
        event EventHandler<ControllerEventArgs> ControllerRemoved;
        event EventHandler<ControllerEventArgs> ControllerUpdated;
    }

    public abstract class BaseControllerDetector : IControllerDetector
    {
        protected readonly ILogger _logger;
        protected bool _isMonitoring = false;
        protected readonly Dictionary<string, ControllerInfo> _knownControllers = new();
        protected readonly object _lock = new();

        public abstract string Name { get; }
        public virtual bool IsAvailable => true;

        protected BaseControllerDetector(ILogger logger)
        {
            _logger = logger;
        }

        public abstract Task InitializeAsync();
        public abstract Task<IEnumerable<ControllerInfo>> DetectControllersAsync();
        public abstract Task StartMonitoringAsync();
        public abstract Task StopMonitoringAsync();

        public event EventHandler<ControllerEventArgs>? ControllerDetected;
        public event EventHandler<ControllerEventArgs>? ControllerRemoved;
        public event EventHandler<ControllerEventArgs>? ControllerUpdated;

        protected void OnControllerDetected(ControllerInfo controller)
        {
            _knownControllers[controller.DeviceId] = controller;
            ControllerDetected?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.Connected));
        }

        protected void OnControllerRemoved(string deviceId)
        {
            if (_knownControllers.TryGetValue(deviceId, out var controller))
            {
                controller.IsConnected = false;
                controller.LastSeen = DateTime.Now;
                _knownControllers.Remove(deviceId);
                ControllerRemoved?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.Disconnected));
            }
        }

        protected void OnControllerUpdated(ControllerInfo controller)
        {
            if (_knownControllers.TryGetValue(controller.DeviceId, out var existing))
            {
                var batteryChanged = existing.BatteryPercentage != controller.BatteryPercentage;
                var chargingChanged = existing.IsCharging != controller.IsCharging;
                
                _knownControllers[controller.DeviceId] = controller;
                
                if (batteryChanged)
                    ControllerUpdated?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.BatteryChanged));
                if (chargingChanged)
                    ControllerUpdated?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.ChargingStateChanged));
                if (!batteryChanged && !chargingChanged)
                    ControllerUpdated?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.PropertiesChanged));
            }
        }

        protected ControllerInfo CreateBaseControllerInfo(string deviceId, string name)
        {
            return new ControllerInfo
            {
                DeviceId = deviceId,
                Name = name,
                // Detectors describe *identity*, never connection state: being enumerated
                // does not prove a controller is switched on (paired Bluetooth devices stay
                // enumerated while powered off). ControllerManager resolves the live state.
                ConnectionState = ControllerConnectionState.Unknown,
                IsConnected = false
            };
        }
    }
}
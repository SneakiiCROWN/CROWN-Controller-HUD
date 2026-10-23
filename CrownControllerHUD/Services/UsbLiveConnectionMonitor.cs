using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Windows.Gaming.Input;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Live connection monitor for USB controllers.
    ///
    /// A USB controller is "connected" when it is actually present and active in the
    /// device tree and visible to Windows.Gaming.Input / XInput. Unlike Bluetooth,
    /// a USB controller cannot be "paired but powered off" - if it's in the device
    /// tree as present, it's physically attached.
    ///
    /// Signals used:
    ///  - PnP device presence (HID/XNACOMPOSITE enumerators)
    ///  - Windows.Gaming.Input RawGameController list
    ///  - XInput slot occupancy (corroborating)
    ///
    /// This is passive and never forces a connection.
    /// </summary>
    public interface IUsbLiveConnectionMonitor : IDisposable
    {
        bool IsAvailable { get; }
        string? LastError { get; }

        Task InitializeAsync();
        Task StartAsync();
        Task StopAsync();
        Task RefreshAsync();

        ControllerConnectionState GetState(string deviceId);
        string? GetStateSource(string deviceId);

        /// <summary>One-line summary for diagnostics.</summary>
        string Describe();

        event EventHandler<UsbConnectionChangedEventArgs>? StateChanged;
    }

    public sealed class UsbConnectionChangedEventArgs : EventArgs
    {
        public string DeviceId { get; init; } = string.Empty;
        public string? Name { get; init; }
        public ControllerConnectionState Previous { get; init; }
        public ControllerConnectionState Current { get; init; }
        public string Source { get; init; } = string.Empty;
        public bool? PnpPresent { get; init; }
        public bool? WinRTConnected { get; init; }
        public bool? XInputConnected { get; init; }
    }

    public sealed class UsbLiveConnectionMonitor : IUsbLiveConnectionMonitor
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        private readonly ILogger<UsbLiveConnectionMonitor> _logger;
        private readonly CancellationTokenSource? _cts;
        private readonly Task? _pollTask;
        private readonly object _gate = new();
        private readonly Dictionary<string, UsbDeviceState> _states = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed = false;

        public bool IsAvailable { get; private set; } = true;
        public string? LastError { get; private set; }

        public event EventHandler<UsbConnectionChangedEventArgs>? StateChanged;

        public UsbLiveConnectionMonitor(ILogger<UsbLiveConnectionMonitor> logger)
        {
            _logger = logger;
            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        private sealed class UsbDeviceState
        {
            public string DeviceId = string.Empty;
            public string? Name;
            public string? PnpInstanceId;
            public string? ContainerId;
            public ControllerConnectionState State = ControllerConnectionState.Unknown;
            public string Source = "no-live-signal";
            public bool? PnpPresent;
            public bool? WinRTConnected;
            public bool? XInputConnected;
            public DateTime LastEnumeratedUtc = DateTime.UtcNow;
            public DateTime StateChangedUtc = DateTime.UtcNow;
        }

        public async Task InitializeAsync()
        {
            try
            {
                await RefreshCoreAsync(CancellationToken.None);
                DiagnosticLogger.Info($"[USB Live] initialized: {Describe()}");
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                DiagnosticLogger.Error("[USB Live] initialization failed", ex);
                _logger.LogError(ex, "USB live connection monitor initialization failed");
            }
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }
            return Task.CompletedTask;
        }

        public Task RefreshAsync() => RefreshCoreAsync(CancellationToken.None);

        public ControllerConnectionState GetState(string deviceId)
        {
            lock (_gate)
            {
                return _states.TryGetValue(deviceId, out var state) ? state.State : ControllerConnectionState.Unknown;
            }
        }

        public string? GetStateSource(string deviceId)
        {
            lock (_gate)
            {
                return _states.TryGetValue(deviceId, out var state) ? state.Source : null;
            }
        }

        public string Describe()
        {
            lock (_gate)
            {
                if (_states.Count == 0) return "<no USB controllers tracked>";
                return string.Join(" | ", _states.Values.Select(s =>
                    $"{s.DeviceId}('{s.Name ?? "?"}') state={s.State} pnp={Format(s.PnpPresent)} winrt={Format(s.WinRTConnected)} xinput={Format(s.XInputConnected)} src={s.Source}"));
            }
        }

        private static string Format(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "?";

        private static string GetTupleValue(bool? present, string? name, string defaultName = "?")
        {
            return present.HasValue ? (present.Value ? (name ?? defaultName) : "absent") : "unknown";
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await RefreshCoreAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    _logger.LogWarning(ex, "USB live connection poll failed");
                }

                try { await Task.Delay(PollInterval, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RefreshCoreAsync(CancellationToken token)
        {
            var pnpPresent = GetPnpPresentDevices();
            var winrtConnected = GetWinRTConnectedDevices();
            var xinputConnected = GetXInputConnectedDevices();

            var allDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allDeviceIds.UnionWith(pnpPresent.Keys);
            allDeviceIds.UnionWith(winrtConnected.Keys);
            allDeviceIds.UnionWith(xinputConnected.Keys);

            var changes = new List<UsbConnectionChangedEventArgs>();

            lock (_gate)
            {
                foreach (var deviceId in allDeviceIds)
                {
                    if (!_states.TryGetValue(deviceId, out var state))
                    {
                        state = new UsbDeviceState { DeviceId = deviceId };
                        _states[deviceId] = state;
                    }

                    bool pnpPresentVal = pnpPresent.TryGetValue(deviceId, out var pnp) && pnp.Present;
                    bool winrtConnectedVal = winrtConnected.TryGetValue(deviceId, out var winrt) && winrt.Connected;
                    bool xinputConnectedVal = xinputConnected.TryGetValue(deviceId, out var xinput) && xinput.Connected;

                    state.PnpPresent = pnpPresentVal ? true : (pnpPresent.ContainsKey(deviceId) ? false : (bool?)null);
                    state.WinRTConnected = winrtConnectedVal ? true : (winrtConnected.ContainsKey(deviceId) ? false : (bool?)null);
                    state.XInputConnected = xinputConnectedVal ? true : (xinputConnected.ContainsKey(deviceId) ? false : (bool?)null);
                    
                    // Store PnP identity info
                    if (pnpPresent.TryGetValue(deviceId, out var pnp2))
                    {
                        state.Name = pnp2.Name;
                        state.ContainerId = pnp2.ContainerId;
                        state.PnpInstanceId = pnp2.PnpInstanceId;
                    }
                    else if (winrtConnected.TryGetValue(deviceId, out var winrt2))
                    {
                        state.Name = winrt2.Name;
                        state.ContainerId = winrt2.ContainerId;
                        state.PnpInstanceId = winrt2.PnpInstanceId;
                    }
                    else if (xinputConnected.TryGetValue(deviceId, out var xinput2))
                    {
                        state.Name = xinput2.Name;
                    }

                    var (nextState, source) = ComputeState(state);

                    if (nextState != state.State)
                    {
                        var previous = state.State;
                        state.State = nextState;
                        state.Source = source;
                        state.StateChangedUtc = DateTime.UtcNow;

                        changes.Add(new UsbConnectionChangedEventArgs
                        {
                            DeviceId = deviceId,
                            Name = state.Name,
                            Previous = previous,
                            Current = nextState,
                            Source = source,
                            PnpPresent = state.PnpPresent,
                            WinRTConnected = state.WinRTConnected,
                            XInputConnected = state.XInputConnected
                        });
                    }

                    state.LastEnumeratedUtc = DateTime.UtcNow;
                }

                // Clean up devices that haven't been seen for a while
                var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
                var stale = _states.Where(kv => kv.Value.LastEnumeratedUtc < cutoff && kv.Value.State != ControllerConnectionState.Unknown).ToList();
                foreach (var kv in stale)
                {
                    var state = kv.Value;
                    state.PnpPresent = false;
                    state.WinRTConnected = false;
                    state.XInputConnected = false;
                    var (nextState, source) = ComputeState(state);
                    if (nextState != state.State)
                    {
                        changes.Add(new UsbConnectionChangedEventArgs
                        {
                            DeviceId = state.DeviceId,
                            Name = state.Name,
                            Previous = state.State,
                            Current = nextState,
                            Source = source
                        });
                        state.State = nextState;
                        state.Source = source;
                        state.StateChangedUtc = DateTime.UtcNow;
                    }
                }
            }

            foreach (var change in changes)
            {
                DiagnosticLogger.Info($"[USB Live] STATE {change.Previous} -> {change.Current} device='{change.Name ?? change.DeviceId}' " +
                    $"pnp={Format(change.PnpPresent)} winrt={Format(change.WinRTConnected)} xinput={Format(change.XInputConnected)} source={change.Source}");
                try { StateChanged?.Invoke(this, change); } catch { }
            }
        }

        private Dictionary<string, (bool Present, string? Name, string? ContainerId, string? PnpInstanceId)> GetPnpPresentDevices()
        {
            var result = new Dictionary<string, (bool Present, string? Name, string? ContainerId, string? PnpInstanceId)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var instanceId in PnpDeviceEnumerator.EnumeratePresentInstanceIds("HID"))
                {
                    try
                    {
                        var record = PnpDeviceEnumerator.Describe(instanceId);
                        if (!record.HasVidPid) continue;

                        // Only controllers we recognize
                        if (!KnownControllerIds.TryIdentifyExact(record.VendorId, record.ProductId, out _) &&
                            !(KnownControllerIds.IsGamepadVendor(record.VendorId) && UsbLiveConnectionMonitor.LooksLikeGamepad(record.SearchText)))
                            continue;

                        var container = PnpDeviceEnumerator.IsUsableContainer(record.ContainerIdRaw)
                            ? record.ContainerId.ToString("D").ToUpperInvariant()
                            : null;
                        if (container == null) continue;

                        var deviceId = $"CTRL\\{container}\\{record.VendorId:X4}_{record.ProductId:X4}";
                        result[deviceId] = (true, record.DisplayName, container, instanceId);
                    }
                    catch { }
                }

                foreach (var instanceId in PnpDeviceEnumerator.EnumeratePresentInstanceIds("XNACOMPOSITE"))
                {
                    try
                    {
                        var record = PnpDeviceEnumerator.Describe(instanceId);
                        if (!record.HasVidPid) continue;

                        var container = PnpDeviceEnumerator.IsUsableContainer(record.ContainerIdRaw)
                            ? record.ContainerId.ToString("D").ToUpperInvariant()
                            : null;
                        if (container == null) continue;

                        var deviceId = $"CTRL\\{container}\\{record.VendorId:X4}_{record.ProductId:X4}";
                        result[deviceId] = (true, record.DisplayName, container, instanceId);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PnP enumeration for USB live monitor failed");
            }

            return result;
        }

        private Dictionary<string, (bool Connected, string? Name, string? ContainerId, string? PnpInstanceId)> GetWinRTConnectedDevices()
        {
            var result = new Dictionary<string, (bool Connected, string? Name, string? ContainerId, string? PnpInstanceId)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var raw in RawGameController.RawGameControllers)
                {
                    try
                    {
                        if (!KnownControllerIds.TryIdentifyExact(raw.HardwareVendorId, raw.HardwareProductId, out _) &&
                            !(KnownControllerIds.IsGamepadVendor(raw.HardwareVendorId) && UsbLiveConnectionMonitor.LooksLikeGamepad(raw.DisplayName)))
                            continue;

                        var instanceId = PnpDeviceEnumerator.InterfacePathToInstanceId(raw.NonRoamableId);
                        if (string.IsNullOrEmpty(instanceId)) continue;

                        var record = PnpDeviceEnumerator.Describe(instanceId);
                        var container = PnpDeviceEnumerator.IsUsableContainer(record.ContainerIdRaw)
                            ? record.ContainerId.ToString("D").ToUpperInvariant()
                            : null;
                        if (container == null) continue;

                        var deviceId = $"CTRL\\{container}\\{raw.HardwareVendorId:X4}_{raw.HardwareProductId:X4}";
                        result[deviceId] = (true, raw.DisplayName, container, instanceId);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WinRT enumeration for USB live monitor failed");
            }

            return result;
        }

        private static bool LooksLikeGamepad(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var lower = text.ToLowerInvariant();
            return lower.Contains("gamepad") || lower.Contains("game controller") || lower.Contains("joystick") ||
                   lower.Contains("xinput") || lower.Contains("xbox") || lower.Contains("wireless controller") ||
                   lower.Contains("dualshock") || lower.Contains("dualsense") || lower.Contains("pro controller");
        }

        private Dictionary<string, (bool Connected, string? Name)> GetXInputConnectedDevices()
        {
            var result = new Dictionary<string, (bool, string?)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var slots = XInputInterop.EnumerateSlots();
                foreach (var slot in slots)
                {
                    if (!slot.Connected) continue;

                    // We can't easily map XInput slot to DeviceId here without more context
                    // This is just a corroborating signal
                    result[$"XINPUT_SLOT_{slot.Slot}"] = (true, $"XInput Slot {slot.Slot}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "XInput enumeration for USB live monitor failed");
            }

            return result;
        }

        private static (ControllerConnectionState State, string Source) ComputeState(UsbDeviceState state)
        {
            // USB controller: if PnP says present, it's connected.
            // WinRT/XInput corroborate but are not authoritative alone.
            if (state.PnpPresent == true)
                return (ControllerConnectionState.Connected, "pnp-present");

            if (state.PnpPresent == false)
                return (ControllerConnectionState.Disconnected, "pnp-absent");

            // If PnP is unknown, use WinRT as fallback
            if (state.WinRTConnected == true)
                return (ControllerConnectionState.Connected, "windows-gaming-input");

            if (state.WinRTConnected == false)
                return (ControllerConnectionState.Disconnected, "windows-gaming-input-absent");

            return (ControllerConnectionState.Unknown, "no-live-signal");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }
    }
}
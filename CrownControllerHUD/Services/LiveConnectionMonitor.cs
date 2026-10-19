using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace CrownControllerHUD.Services
{
    public sealed class LiveConnectionChangedEventArgs : EventArgs
    {
        public string Address { get; init; } = string.Empty;
        public string? Name { get; init; }
        public ControllerConnectionState Previous { get; init; }
        public ControllerConnectionState Current { get; init; }
        public string Source { get; init; } = string.Empty;
        public bool? AepIsConnected { get; init; }
        public bool? BleIsConnected { get; init; }
    }

    /// <summary>
    /// The single authority on whether a Bluetooth controller is actually switched on.
    ///
    /// Device presence is NOT connection state: a paired Bluetooth controller keeps every
    /// one of its device nodes installed and "present" while powered off, which is why
    /// Plug-and-play enumeration alone reported a switched-off controller as connected.
    ///
    /// This monitor reads genuinely live OS signals instead:
    ///  * PRIMARY  - the association-endpoint property <c>System.Devices.Aep.IsConnected</c>,
    ///               observed through a <see cref="DeviceWatcher"/> and a 1 s poll. This is
    ///               passive: it never causes a connection attempt.
    ///  * SECONDARY - <c>BluetoothLEDevice.ConnectionStatus</c> /
    ///               <c>ConnectionStatusChanged</c>, attached only once the endpoint is
    ///               already connected (so it can never trigger an unwanted connection)
    ///               and used as an independent corroborating signal.
    ///
    /// Both signals were verified against the owner's Xbox Elite Series 2 (045E:0B22,
    /// Bluetooth LE): each flipped on the real power-on and power-off edges within ~0.1 s.
    /// </summary>
    public interface ILiveConnectionMonitor : IDisposable
    {
        bool IsAvailable { get; }
        string? LastError { get; }

        Task InitializeAsync();
        Task StartAsync();
        Task StopAsync();

        /// <summary>Forces an immediate re-query of the live connection signals.</summary>
        Task RefreshAsync();

        ControllerConnectionState GetState(string? bluetoothAddress);
        string? GetStateSource(string? bluetoothAddress);
        string? GetDeviceName(string? bluetoothAddress);

        /// <summary>One-line summary of every known device and its raw signals, for diagnostics.</summary>
        string Describe();

        event EventHandler<LiveConnectionChangedEventArgs>? StateChanged;
    }

    public sealed class WinRTLiveConnectionMonitor : ILiveConnectionMonitor
    {
        private const string PropIsConnected = "System.Devices.Aep.IsConnected";
        private const string PropIsPresent = "System.Devices.Aep.IsPresent";
        private const string PropDeviceAddress = "System.Devices.Aep.DeviceAddress";

        private static readonly string[] WatchedProperties = { PropIsConnected, PropIsPresent, PropDeviceAddress };

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DeviceObjectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan EndpointAbsentGracePeriod = TimeSpan.FromSeconds(15);

        private readonly ILogger<WinRTLiveConnectionMonitor> _logger;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _queryLock = new(1, 1);
        private readonly Dictionary<string, DeviceState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BluetoothLEDevice> _deviceObjects = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenThisRound = new(StringComparer.OrdinalIgnoreCase);

        private DeviceWatcher? _leWatcher;
        private DeviceWatcher? _classicWatcher;
        private CancellationTokenSource? _cts;
        private Task? _pollTask;
        private int _refreshQueued;
        private volatile bool _started;
        private bool _disposed;

        public bool IsAvailable { get; private set; } = true;
        public string? LastError { get; private set; }

        public event EventHandler<LiveConnectionChangedEventArgs>? StateChanged;

        public WinRTLiveConnectionMonitor(ILogger<WinRTLiveConnectionMonitor> logger)
        {
            _logger = logger;
        }

        private sealed class DeviceState
        {
            public string Address = string.Empty;
            public string? Name;
            public string? DeviceInformationId;
            public bool? AepIsConnected;
            public bool? AepIsPresent;
            public bool? BleIsConnected;
            public ControllerConnectionState State = ControllerConnectionState.Unknown;
            public string Source = "no-live-signal";
            public DateTime LastEnumeratedUtc = DateTime.UtcNow;
            public bool SeenInLastEnumeration;
            public DateTime StateChangedUtc = DateTime.UtcNow;
        }

        // ---------------------------------------------------------------- lifecycle

        public async Task InitializeAsync()
        {
            try
            {
                _leWatcher = DeviceInformation.CreateWatcher(
                    BluetoothLEDevice.GetDeviceSelector(), WatchedProperties, DeviceInformationKind.AssociationEndpoint);
                HookWatcher(_leWatcher, "BluetoothLE");

                // Bluetooth Classic (BR/EDR) association endpoints use a different protocol
                // id, so watch them too (DualShock 4, older Xbox pads, ...).
                _classicWatcher = DeviceInformation.CreateWatcher(
                    BluetoothDevice.GetDeviceSelector(), WatchedProperties, DeviceInformationKind.AssociationEndpoint);
                HookWatcher(_classicWatcher, "BluetoothClassic");

                await RefreshCoreAsync(attachDeviceObjects: false, CancellationToken.None);

                DiagnosticLogger.Info($"[Live] WinRT live-connection monitor initialized: {Describe()}");
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                DiagnosticLogger.Error("[Live] Failed to initialize WinRT live-connection monitor", ex);
                _logger.LogError(ex, "Live connection monitor initialization failed");
            }
        }

        public Task StartAsync()
        {
            if (_disposed || _started || !IsAvailable) return Task.CompletedTask;
            _started = true;

            try
            {
                StartWatcher(_leWatcher, "BluetoothLE");
                StartWatcher(_classicWatcher, "BluetoothClassic");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("[Live] Failed to start device watchers", ex);
            }

            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
            DiagnosticLogger.Info("[Live] live-connection monitoring started");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            if (!_started) return Task.CompletedTask;
            _started = false;

            try { _cts?.Cancel(); } catch { }

            StopWatcher(_leWatcher, "BluetoothLE");
            StopWatcher(_classicWatcher, "BluetoothClassic");

            DiagnosticLogger.Info("[Live] live-connection monitoring stopped");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { StopAsync(); } catch { }
            try { _cts?.Dispose(); } catch { }
            // NOTE: _queryLock is intentionally not disposed; a poll iteration may still be
            // releasing it while the app is shutting down, and a leaked semaphore at process
            // exit is harmless.

            lock (_gate)
            {
                foreach (var device in _deviceObjects.Values)
                {
                    try { device.Dispose(); } catch { }
                }
                _deviceObjects.Clear();
            }
        }

        // ---------------------------------------------------------------- public queries

        public Task RefreshAsync() => RefreshCoreAsync(attachDeviceObjects: true, CancellationToken.None);

        public ControllerConnectionState GetState(string? bluetoothAddress)
        {
            var address = CanonicalKey(bluetoothAddress);
            if (address == null) return ControllerConnectionState.Unknown;

            lock (_gate)
            {
                return _states.TryGetValue(address, out var state)
                    ? state.State
                    : ControllerConnectionState.Unknown;
            }
        }

        public string? GetStateSource(string? bluetoothAddress)
        {
            var address = CanonicalKey(bluetoothAddress);
            if (address == null) return null;

            lock (_gate)
            {
                return _states.TryGetValue(address, out var state) ? state.Source : null;
            }
        }

        public string? GetDeviceName(string? bluetoothAddress)
        {
            var address = CanonicalKey(bluetoothAddress);
            if (address == null) return null;

            lock (_gate)
            {
                return _states.TryGetValue(address, out var state) ? state.Name : null;
            }
        }

        public string Describe()
        {
            lock (_gate)
            {
                if (_states.Count == 0) return "<no paired Bluetooth endpoints found>";

                return string.Join(" | ", _states.Values.Select(s =>
                    $"{s.Address}('{s.Name ?? "?"}') state={s.State} aep={Format(s.AepIsConnected)} ble={Format(s.BleIsConnected)} src={s.Source}"));
            }
        }

        private static string Format(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "?";

        private static string? CanonicalKey(string? bluetoothAddress)
        {
            if (string.IsNullOrWhiteSpace(bluetoothAddress)) return null;
            return DeviceIdentity.NormalizeAddress(bluetoothAddress)
                   ?? bluetoothAddress!.Trim().ToUpperInvariant();
        }

        // ---------------------------------------------------------------- polling

        private async Task PollLoopAsync(CancellationToken token)
        {
            int tick = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Re-attaching BluetoothLEDevice objects is comparatively expensive, so
                    // refresh them periodically rather than on every tick.
                    await RefreshCoreAsync(attachDeviceObjects: (tick % 5) == 0, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    _logger.LogWarning(ex, "Live connection poll failed");
                }

                tick++;
                try { await Task.Delay(PollInterval, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RefreshCoreAsync(bool attachDeviceObjects, CancellationToken token)
        {
            if (!IsAvailable || _disposed) return;

            // One enumeration at a time: the poll loop, a devnode watcher event and a forced
            // refresh must not interleave their enumeration bookkeeping.
            try { await _queryLock.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                lock (_gate) _seenThisRound.Clear();

                var episode = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelector(), WatchedProperties, DeviceInformationKind.AssociationEndpoint);
                foreach (var info in episode)
                {
                    if (token.IsCancellationRequested) return;
                    ApplyDeviceInformation(info);
                }

                try
                {
                    var classic = await DeviceInformation.FindAllAsync(
                        BluetoothDevice.GetDeviceSelector(), WatchedProperties, DeviceInformationKind.AssociationEndpoint);
                    foreach (var info in classic)
                    {
                        if (token.IsCancellationRequested) return;
                        ApplyDeviceInformation(info);
                    }
                }
                catch (Exception ex)
                {
                    // Classic Bluetooth may be unavailable (no adapter); never fail the whole poll.
                    _logger.LogDebug(ex, "Bluetooth Classic association endpoint enumeration failed");
                }

                CommitEnumerationRound();

                if (attachDeviceObjects)
                {
                    List<DeviceState> snapshot;
                    lock (_gate) snapshot = _states.Values.ToList();

                    foreach (var state in snapshot)
                    {
                        if (token.IsCancellationRequested) return;
                        await TryAttachDeviceObjectAsync(state);
                    }
                }

                RecomputeAllStates();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                DiagnosticLogger.Warn($"[Live] live-connection query failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                _queryLock.Release();
            }
        }

        private void ApplyDeviceInformation(DeviceInformation info)
        {
            try
            {
                string? address = null;

                if (info.Properties.TryGetValue(PropDeviceAddress, out var addressValue) && addressValue != null)
                    address = DeviceIdentity.NormalizeAddress(addressValue.ToString());

                address ??= DeviceIdentity.FromAssociationEndpointId(info.Id);
                if (address == null) return;

                bool? isConnected = null;
                if (info.Properties.TryGetValue(PropIsConnected, out var connectedValue) && connectedValue is bool connected)
                    isConnected = connected;

                bool? isPresent = null;
                if (info.Properties.TryGetValue(PropIsPresent, out var presentValue) && presentValue is bool present)
                    isPresent = present;

                lock (_gate)
                {
                    if (!_states.TryGetValue(address, out var state))
                    {
                        state = new DeviceState { Address = address };
                        _states[address] = state;
                    }

                    state.DeviceInformationId = info.Id;
                    state.LastEnumeratedUtc = DateTime.UtcNow;
                    state.SeenInLastEnumeration = true;
                    _seenThisRound.Add(address);

                    if (!string.IsNullOrWhiteSpace(info.Name))
                        state.Name = info.Name;

                    if (isConnected.HasValue) state.AepIsConnected = isConnected;
                    if (isPresent.HasValue) state.AepIsPresent = isPresent;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to apply association endpoint information for {Id}", info.Id);
            }
        }

        private void CommitEnumerationRound()
        {
            var now = DateTime.UtcNow;

            lock (_gate)
            {
                foreach (var state in _states.Values)
                {
                    state.SeenInLastEnumeration = _seenThisRound.Contains(state.Address);

                    // An endpoint that is no longer enumerated at all is definitely not
                    // connected (it is not merely powered off - paired endpoints stay
                    // enumerated in that case).
                    if (!state.SeenInLastEnumeration &&
                        (now - state.LastEnumeratedUtc) > EndpointAbsentGracePeriod)
                    {
                        state.AepIsConnected = false;
                        state.AepIsPresent = false;
                    }
                }
            }
        }

        private async Task TryAttachDeviceObjectAsync(DeviceState state)
        {
            if (string.IsNullOrEmpty(state.DeviceInformationId)) return;

            lock (_gate)
            {
                if (_deviceObjects.ContainsKey(state.Address)) return;
            }

            // Only attach when the endpoint is already connected, so this can never be the
            // thing that makes Windows connect to a powered-off controller.
            if (state.AepIsConnected != true) return;

            BluetoothLEDevice? device;
            try
            {
                device = await AwaitOperationAsync(
                    BluetoothLEDevice.FromIdAsync(state.DeviceInformationId), DeviceObjectTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "BluetoothLEDevice.FromIdAsync failed for {Address}", state.Address);
                return;
            }

            if (device == null) return;

            bool stored = false;
            lock (_gate)
            {
                if (_disposed || _deviceObjects.ContainsKey(state.Address))
                {
                    try { device.Dispose(); } catch { }
                }
                else
                {
                    _deviceObjects[state.Address] = device;
                    stored = true;
                }
            }

            if (!stored) return;

            var address = state.Address;
            device.ConnectionStatusChanged += (sender, _) => OnDeviceObjectStatusChanged(address, sender.ConnectionStatus);
            OnDeviceObjectStatusChanged(address, device.ConnectionStatus);
        }

        private void OnDeviceObjectStatusChanged(string address, BluetoothConnectionStatus status)
        {
            bool connected = status == BluetoothConnectionStatus.Connected;

            lock (_gate)
            {
                if (!_states.TryGetValue(address, out var state))
                {
                    state = new DeviceState { Address = address };
                    _states[address] = state;
                }
                state.BleIsConnected = connected;
            }

            DiagnosticLogger.Info($"[Live] BluetoothLEDevice.ConnectionStatusChanged addr={address} -> {status}");
            RecomputeAllStates();
        }

        // ---------------------------------------------------------------- state machine

        private void RecomputeAllStates()
        {
            var changes = new List<LiveConnectionChangedEventArgs>();
            var disagreements = new List<string>();

            lock (_gate)
            {
                foreach (var state in _states.Values)
                {
                    var (next, source) = ComputeState(state);

                    if (state.AepIsConnected.HasValue && state.BleIsConnected.HasValue &&
                        state.AepIsConnected.Value != state.BleIsConnected.Value)
                    {
                        disagreements.Add(
                            $"{state.Address} AepIsConnected={state.AepIsConnected} != ConnectionStatus={state.BleIsConnected}");
                    }

                    if (next == state.State) continue;

                    var previous = state.State;
                    state.State = next;
                    state.Source = source;
                    state.StateChangedUtc = DateTime.UtcNow;

                    changes.Add(new LiveConnectionChangedEventArgs
                    {
                        Address = state.Address,
                        Name = state.Name,
                        Previous = previous,
                        Current = next,
                        Source = source,
                        AepIsConnected = state.AepIsConnected,
                        BleIsConnected = state.BleIsConnected
                    });
                }
            }

            foreach (var disagreement in disagreements)
                DiagnosticLogger.Warn($"[Live] SIGNAL DISAGREEMENT {disagreement}");

            foreach (var change in changes)
            {
                DiagnosticLogger.Info(
                    $"[Live] STATE {change.Previous} -> {change.Current} addr={change.Address} name='{change.Name ?? "?"}' " +
                    $"source={change.Source} aep={Format(change.AepIsConnected)} ble={Format(change.BleIsConnected)}");

                try
                {
                    StateChanged?.Invoke(this, change);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A live-connection StateChanged handler threw");
                }
            }
        }

        /// <summary>
        /// Combines the live signals into one state. The association-endpoint property is
        /// authoritative because it is passive and OS-managed; the device object status is
        /// used as a corroborating fallback, and always fails safe (a signal reporting
        /// "disconnected" is never overridden into "connected").
        /// </summary>
        private static (ControllerConnectionState State, string Source) ComputeState(DeviceState state)
        {
            if (state.AepIsConnected == true)
                return (ControllerConnectionState.Connected, $"{PropIsConnected}=true");

            if (state.AepIsConnected == false)
                return (ControllerConnectionState.Disconnected, $"{PropIsConnected}=false");

            if (state.BleIsConnected == true)
                return (ControllerConnectionState.Connected, "BluetoothLEDevice.ConnectionStatus=Connected");

            if (state.BleIsConnected == false)
                return (ControllerConnectionState.Disconnected, "BluetoothLEDevice.ConnectionStatus=Disconnected");

            return (ControllerConnectionState.Unknown, "no-live-signal");
        }

        // ---------------------------------------------------------------- watcher plumbing

        private void HookWatcher(DeviceWatcher watcher, string label)
        {
            watcher.Added += (_, info) =>
            {
                try
                {
                    ApplyDeviceInformation(info);
                    RecomputeAllStates();
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Watcher Added handler failed ({Label})", label); }
            };

            watcher.Updated += (_, update) =>
            {
                try
                {
                    HandleDeviceInformationUpdate(update);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Watcher Updated handler failed ({Label})", label); }
            };

            watcher.Removed += (_, update) =>
            {
                try
                {
                    HandleDeviceRemoved(update.Id);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Watcher Removed handler failed ({Label})", label); }
            };

            watcher.EnumerationCompleted += (_, _) =>
            {
                try { RecomputeAllStates(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Watcher EnumerationCompleted handler failed ({Label})", label); }
            };

            watcher.Stopped += (_, _) => _logger.LogDebug("Device watcher stopped ({Label})", label);
        }

        private void HandleDeviceInformationUpdate(DeviceInformationUpdate update)
        {
            if (update.Properties.TryGetValue(PropIsConnected, out var value) && value is bool connected)
            {
                var address = FindAddressForId(update.Id);
                if (address != null)
                {
                    lock (_gate)
                    {
                        if (_states.TryGetValue(address, out var state))
                        {
                            state.AepIsConnected = connected;
                            state.LastEnumeratedUtc = DateTime.UtcNow;
                        }
                    }
                    RecomputeAllStates();
                    return;
                }
            }

            // The update did not carry the property we need (Windows sometimes raises an
            // update with an empty value); do a full, coalesced re-query instead.
            QueueFullRefresh();
        }

        private void HandleDeviceRemoved(string id)
        {
            var address = FindAddressForId(id);
            if (address == null) return;

            lock (_gate)
            {
                if (_states.TryGetValue(address, out var state))
                {
                    state.AepIsConnected = false;
                    state.AepIsPresent = false;
                    state.SeenInLastEnumeration = false;
                }
            }

            DiagnosticLogger.Info($"[Live] endpoint removed addr={address}");
            RecomputeAllStates();
        }

        private string? FindAddressForId(string id)
        {
            lock (_gate)
            {
                foreach (var pair in _states)
                {
                    if (string.Equals(pair.Value.DeviceInformationId, id, StringComparison.OrdinalIgnoreCase))
                        return pair.Key;
                }
            }

            return DeviceIdentity.FromAssociationEndpointId(id);
        }

        private void QueueFullRefresh()
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try { await RefreshCoreAsync(attachDeviceObjects: false, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Queued live refresh failed"); }
                finally { Interlocked.Exchange(ref _refreshQueued, 0); }
            });
        }

        private void StartWatcher(DeviceWatcher? watcher, string label)
        {
            if (watcher == null) return;
            if (watcher.Status == DeviceWatcherStatus.Started || watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                return;

            watcher.Start();
            _logger.LogInformation("Device watcher started ({Label}, status {Status})", label, watcher.Status);
        }

        private void StopWatcher(DeviceWatcher? watcher, string label)
        {
            if (watcher == null) return;

            try
            {
                if (watcher.Status == DeviceWatcherStatus.Started ||
                    watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping device watcher failed ({Label})", label);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static async Task<T?> AwaitOperationAsync<T>(IAsyncOperation<T> operation, TimeSpan timeout)
            where T : class
        {
            var operationTask = AwaitOperationCore(operation);
            var completed = await Task.WhenAny(operationTask, Task.Delay(timeout)).ConfigureAwait(false);

            if (completed != operationTask)
            {
                _ = operationTask.ContinueWith(
                    task => { _ = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }

            try { return await operationTask.ConfigureAwait(false); }
            catch { return null; }
        }

        private static async Task<T> AwaitOperationCore<T>(IAsyncOperation<T> operation) => await operation;
    }
}

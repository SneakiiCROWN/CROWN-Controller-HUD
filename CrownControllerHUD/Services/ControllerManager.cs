using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        /// <summary>
        /// Refresh that also re-queries the live connection signals first and reports any
        /// failure through <see cref="LastRefreshError"/>. Used by the UI Refresh buttons.
        /// </summary>
        Task ForceRefreshAsync();

        /// <summary>Null when the most recent refresh fully succeeded.</summary>
        string? LastRefreshError { get; }

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

    public class ControllerManager : IControllerManager, IDisposable
    {
        /// <summary>
        /// Safety-net poll. Live transitions normally arrive immediately from
        /// <see cref="ILiveConnectionMonitor.StateChanged"/>; this poll guarantees eventual
        /// convergence even if an OS event is missed.
        /// </summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// A state must stay unchanged this long before it is reported, so a brief glitch in
        /// a signal can never produce a spurious notification (debouncing).
        /// </summary>
        private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(800);

        private readonly ILogger<ControllerManager> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private readonly ILiveConnectionMonitor _liveMonitor;
        private readonly List<IControllerDetector> _detectors = new();
        private readonly Dictionary<string, ControllerInfo> _controllers = new();

        /// <summary>Last state we actually reported to the UI, per physical device.</summary>
        private readonly Dictionary<string, ControllerConnectionState> _reportedState = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>State currently waiting out the debounce window, per physical device.</summary>
        private readonly Dictionary<string, (ControllerConnectionState State, DateTime Since)> _pendingState = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new();
        private readonly System.Threading.Timer? _refreshTimer;
        private bool _isRunning = false;
        private int _refreshing = 0;
        private int _refreshQueued = 0;
        private int _debounceScheduled = 0;
        private bool _disposed = false;
        private volatile string? _lastRefreshError;

        public string? LastRefreshError => _lastRefreshError;

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
            ILiveConnectionMonitor liveMonitor,
            IEnumerable<IControllerDetector> detectors)
        {
            _logger = logger;
            _settings = settings;
            _liveMonitor = liveMonitor;

            DiagnosticLogger.Info("ControllerManager ctor: begin");
            _detectors.AddRange(detectors.Where(d => d.IsAvailable));
            DiagnosticLogger.Info($"ControllerManager ctor: {_detectors.Count} available detectors");

            foreach (var detector in _detectors)
            {
                detector.ControllerDetected += OnDetectorControllerDetected;
                detector.ControllerRemoved += OnDetectorControllerRemoved;
                detector.ControllerUpdated += OnDetectorControllerUpdated;
            }

            // Verified live transitions arrive from the monitor; refresh immediately instead
            // of waiting for the safety-net poll so a real power event is reported at once.
            _liveMonitor.StateChanged += OnLiveConnectionStateChanged;

            // The manager polls all detectors and is the single source of truth for
            // connect/disconnect/battery events. Detector-raised events are intentionally
            // ignored (see handlers below) so a single physical event cannot be reported
            // twice by two providers or by a detector poll plus a manager poll.
            _refreshTimer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);

            DiagnosticLogger.Info("ControllerManager ctor: end");
        }

        public async Task InitializeAsync()
        {
            _logger.LogInformation("Initializing Controller Manager with {Count} detectors", _detectors.Count);

            try
            {
                // Bring up the live connection monitor first so the very first refresh can
                // already report a verified state instead of guessing from device presence.
                await _liveMonitor.InitializeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize live connection monitor");
                DiagnosticLogger.Error("Live connection monitor initialization failed", ex);
            }

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

            try { await _liveMonitor.StartAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to start live connection monitor"); }

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

            // Keep watching for connect/disconnect/battery changes.
            _refreshTimer?.Change(RefreshInterval, RefreshInterval);
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;
            _isRunning = false;

            _refreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
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

            try { await _liveMonitor.StopAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to stop live connection monitor"); }
        }

        /// <summary>
        /// Coalescing refresh: callers set a "queued" flag; if a refresh is already running
        /// it simply returns and the running refresh loops once more. This makes the refresh
        /// re-entrant safe from the timer, the UI and monitor events all at once.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (_disposed) return;

            Interlocked.Exchange(ref _refreshQueued, 1);
            if (Interlocked.Exchange(ref _refreshing, 1) == 1)
                return;

            try
            {
                while (Interlocked.Exchange(ref _refreshQueued, 0) == 1 && !_disposed)
                    await RefreshCoreAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        public async Task ForceRefreshAsync()
        {
            DiagnosticLogger.Info("[Refresh] Force refresh requested: re-querying live connection signals");
            _logger.LogInformation("Force refresh requested");

            try
            {
                await _liveMonitor.RefreshAsync();
                DiagnosticLogger.Info($"[Refresh] live signals after force refresh: {_liveMonitor.Describe()}");
            }
            catch (Exception ex)
            {
                _lastRefreshError = $"Bluetooth live query failed: {ex.Message}";
                DiagnosticLogger.Error("[Refresh] live connection query failed", ex);
            }

            await RefreshAsync();
        }

        private async Task RefreshCoreAsync()
        {
            var connectedEvents = new List<ControllerInfo>();
            var disconnectedEvents = new List<ControllerInfo>();
            var batteryEvents = new List<ControllerInfo>();
            var chargingEvents = new List<ControllerInfo>();

            // Devices whose *first ever* observation is "Connected". These are not
            // transitions - the app simply found a controller that was already switched
            // on - so they must never produce a connect notification.
            var initialConnectIds = new HashSet<string>();

            var now = DateTime.UtcNow;
            bool scheduleDebouncedRefresh = false;

            try
            {
                var allControllers = new List<ControllerInfo>();
                string? detectorError = null;

                foreach (var detector in _detectors)
                {
                    try
                    {
                        var controllers = await detector.DetectControllersAsync();
                        allControllers.AddRange(controllers);
                    }
                    catch (Exception ex)
                    {
                        detectorError = $"Detector '{detector.Name}' failed: {ex.Message}";
                        _logger.LogError(ex, "Error detecting controllers with {Name}", detector.Name);
                    }
                }

                // Merge controllers from different detectors into one entry per physical device.
                var merged = MergeControllers(allControllers);

                // Resolve the *verified* live connection state for every discovered device.
                // Detection only proves a device exists; this is what proves it is switched on.
                foreach (var controller in merged)
                    ResolveConnectionState(controller);

                lock (_lock)
                {
                    var currentIds = merged.Select(c => c.DeviceId).ToHashSet();

                    // Battery / charging transitions (compare against the previous snapshot).
                    foreach (var controller in merged)
                    {
                        if (_controllers.TryGetValue(controller.DeviceId, out var previous))
                        {
                            if (previous.BatteryPercentage != controller.BatteryPercentage)
                                batteryEvents.Add(controller);
                            if (previous.IsCharging != controller.IsCharging)
                                chargingEvents.Add(controller);
                        }
                    }

                    // Devices that vanished from enumeration entirely (e.g. a USB pad was
                    // unplugged) while we had reported them connected.
                    foreach (var id in _reportedState.Keys.Where(id => !currentIds.Contains(id)).ToList())
                    {
                        var previousState = _reportedState[id];
                        _reportedState.Remove(id);
                        _pendingState.Remove(id);

                        if (previousState == ControllerConnectionState.Connected &&
                            _controllers.TryGetValue(id, out var gone))
                        {
                            gone.ConnectionState = ControllerConnectionState.Disconnected;
                            gone.IsConnected = false;
                            gone.ConnectionStateSource = "device-no-longer-present";
                            gone.ConnectionStateChangedAt = now;
                            gone.LastSeen = DateTime.Now;
                            disconnectedEvents.Add(gone);
                            LogTransition(gone, previousState, ControllerConnectionState.Disconnected,
                                "device-no-longer-present");
                        }
                    }

                    // State transitions for present devices (debounced, edge-triggered).
                    foreach (var controller in merged)
                    {
                        var id = controller.DeviceId;
                        var state = controller.ConnectionState;
                        bool haveReported = _reportedState.TryGetValue(id, out var reported);

                        // Unchanged state: nothing to debounce, nothing to report. This is what
                        // keeps a steady controller from re-arming the debounce every poll.
                        if (haveReported && state == reported)
                        {
                            _pendingState.Remove(id);
                            continue;
                        }

                        // "Unknown" is not a confirmed transition: never silently promote to
                        // connected, and never silently demote a connected controller because a
                        // signal momentarily went quiet.
                        if (state == ControllerConnectionState.Unknown)
                        {
                            _pendingState.Remove(id);
                            if (haveReported)
                            {
                                DiagnosticLogger.Info(
                                    $"[State] unconfirmed (live signal lost, keeping {reported}) device='{controller.Name}' " +
                                    $"addr={controller.BluetoothAddress ?? "<none>"}");
                            }
                            continue;
                        }

                        // A changed state must hold for the debounce window before it is
                        // reported, so a momentary signal glitch cannot cause a notification.
                        if (!_pendingState.TryGetValue(id, out var pending) || pending.State != state)
                            _pendingState[id] = pending = (state, now);

                        if ((now - pending.Since) < DebounceWindow)
                        {
                            scheduleDebouncedRefresh = true;
                            continue;
                        }

                        _pendingState.Remove(id);
                        _reportedState[id] = state;
                        controller.ConnectionStateChangedAt = now;

                        if (state == ControllerConnectionState.Connected)
                        {
                            controller.ConnectedAt = now;
                            connectedEvents.Add(controller);

                            if (!haveReported)
                                initialConnectIds.Add(controller.DeviceId);
                        }
                        else if (haveReported)
                        {
                            // A genuine Connected -> Disconnected transition. When this is the
                            // *first* observation of a device (nothing was ever reported
                            // connected) there is nothing to notify about: a controller that
                            // was found switched off has not "disconnected".
                            disconnectedEvents.Add(controller);
                        }

                        LogTransition(
                            controller,
                            haveReported ? reported : ControllerConnectionState.Unknown,
                            state,
                            controller.ConnectionStateSource ?? "live-signal",
                            haveReported ? "TRANSITION" : "INITIAL");
                    }

                    // Publish the freshest identity, keeping the timestamps we own.
                    foreach (var controller in merged)
                    {
                        if (_controllers.TryGetValue(controller.DeviceId, out var previous))
                        {
                            if (controller.ConnectedAt == default) controller.ConnectedAt = previous.ConnectedAt;
                            controller.ConnectionStateChangedAt ??= previous.ConnectionStateChangedAt;
                            controller.LastSeen ??= previous.LastSeen;
                        }

                        _controllers[controller.DeviceId] = controller;
                    }
                }

                _lastRefreshError = BuildRefreshError(merged, detectorError);
            }
            catch (Exception ex)
            {
                _lastRefreshError = $"Refresh failed: {ex.Message}";
                _logger.LogError(ex, "Controller refresh failed");
                DiagnosticLogger.Error("Controller refresh failed", ex);
            }

            // Raise events outside the lock so handlers (which marshal to the UI thread)
            // can never deadlock against the Controllers property.
            foreach (var controller in disconnectedEvents)
                OnControllerDisconnected(controller, false);
            foreach (var controller in connectedEvents)
                OnControllerConnected(controller, initialConnectIds.Contains(controller.DeviceId));
            foreach (var controller in batteryEvents)
                OnControllerBatteryChanged(controller);
            foreach (var controller in chargingEvents)
                OnControllerChargingChanged(controller);

            if (disconnectedEvents.Count > 0 || connectedEvents.Count > 0 || batteryEvents.Count > 0 || chargingEvents.Count > 0)
                OnControllersChanged();

            if (scheduleDebouncedRefresh)
                ScheduleDebouncedRefresh();
        }

        /// <summary>
        /// Waits out the current debounce window once, so a transition that just started
        /// settling is reported promptly rather than at the next 2 s poll.
        /// </summary>
        private void ScheduleDebouncedRefresh()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _debounceScheduled, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceWindow + TimeSpan.FromMilliseconds(150));
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Debounced refresh failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _debounceScheduled, 0);
                }
            });
        }

        private static bool IsBluetooth(ControllerInfo controller)
            => controller.ConnectionType == ConnectionType.Bluetooth
               || !string.IsNullOrWhiteSpace(controller.BluetoothAddress);

        /// <summary>
        /// Decides the verified connection state of one controller.
        ///
        /// Bluetooth is the only bus where a powered-off device keeps all of its device
        /// nodes enumerated, so it must be resolved from a live signal. Everything else is
        /// physically attached whenever its node is enumerable as *present*.
        /// </summary>
        private void ResolveConnectionState(ControllerInfo controller)
        {
            if (IsBluetooth(controller))
            {
                var state = _liveMonitor.GetState(controller.BluetoothAddress);
                var source = _liveMonitor.GetStateSource(controller.BluetoothAddress);

                controller.ConnectionState = state;
                controller.IsConnected = state == ControllerConnectionState.Connected;
                controller.ConnectionStateSource = source
                    ?? (string.IsNullOrWhiteSpace(controller.BluetoothAddress)
                        ? "bluetooth-without-address (unverifiable)"
                        : "no-live-signal");
                return;
            }

            controller.ConnectionState = ControllerConnectionState.Connected;
            controller.IsConnected = true;
            controller.ConnectionStateSource = "pnp-present";
        }

        private string? BuildRefreshError(List<ControllerInfo> merged, string? detectorError)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(detectorError))
                parts.Add(detectorError!);

            if (!_liveMonitor.IsAvailable)
                parts.Add($"live connection monitor unavailable ({_liveMonitor.LastError ?? "unknown error"})");

            var unverified = merged
                .Where(c => IsBluetooth(c) && c.ConnectionState == ControllerConnectionState.Unknown)
                .Select(c => string.IsNullOrWhiteSpace(c.BluetoothAddress)
                    ? $"{c.Name} (no Bluetooth address)"
                    : $"{c.Name} ({c.BluetoothAddress})")
                .Distinct()
                .ToList();

            if (unverified.Count > 0)
                parts.Add("live connection state unavailable for: " + string.Join(", ", unverified));

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        public ControllerInfo? GetController(string deviceId)
        {
            lock (_lock)
            {
                return _controllers.GetValueOrDefault(deviceId);
            }
        }

        // ------------------------------------------------------------------ merging

        private List<ControllerInfo> MergeControllers(List<ControllerInfo> controllers)
        {
            if (controllers.Count <= 1)
                return controllers.Select(Canonicalize).ToList();

            // Union-find over every identity token a controller exposes. Two records are the
            // same physical device if they share ANY strong token (Bluetooth MAC, Windows
            // device container, or VID/PID+serial). This is deliberately more forgiving than
            // a single key so a provider that only knows one of them still collapses into the
            // same entry - and it can never merge two *different* controllers, because none
            // of those tokens is shared between distinct physical devices.
            int count = controllers.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            var tokenOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                foreach (var token in IdentityTokens(controllers[i]))
                {
                    if (tokenOwner.TryGetValue(token, out var owner))
                        Union(owner, i);
                    else
                        tokenOwner[token] = i;
                }
            }

            return Enumerable.Range(0, count)
                .GroupBy(Find)
                .Select(g => MergeControllerGroup(g.Select(i => controllers[i]).ToList()))
                .ToList();
        }

        private static IEnumerable<string> IdentityTokens(ControllerInfo controller)
        {
            if (!string.IsNullOrWhiteSpace(controller.BluetoothAddress))
                yield return $"BT:{controller.BluetoothAddress}";

            var container = NormalizeContainer(controller.ContainerId);
            if (container != null)
                yield return $"CNT:{container}";

            if (!string.IsNullOrWhiteSpace(controller.SerialNumber))
                yield return $"SN:{controller.VendorId:X4}:{controller.ProductId:X4}:{controller.SerialNumber}";

            yield return $"DEVID:{controller.DeviceId}";
        }

        /// <summary>
        /// Returns a canonical, brace-free container id so providers that format it
        /// differently ("{guid}" vs "guid") still correlate to the same physical device.
        /// </summary>
        private static string? NormalizeContainer(string? containerId)
        {
            if (!PnpDeviceEnumerator.IsUsableContainer(containerId))
                return null;

            return Guid.TryParse(containerId, out var guid)
                ? guid.ToString("D").ToUpperInvariant()
                : null;
        }

        private ControllerInfo MergeControllerGroup(List<ControllerInfo> group)
        {
            if (group.Count == 1)
                return Canonicalize(group[0]);

            // Prefer the most complete information
            var merged = group.OrderByDescending(c => GetCompletenessScore(c)).First();

            // Prefer a real transport over "Unknown".
            var bestTransport = group
                .Where(c => c.ConnectionType != ConnectionType.Unknown)
                .GroupBy(c => c.ConnectionType)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            if (bestTransport != ConnectionType.Unknown)
                merged.ConnectionType = bestTransport;

            // Prefer the best (non-generic, most specific) controller type.
            var bestType = group
                .Where(c => c.Type != ControllerType.Unknown && c.Type != ControllerType.GenericXInput && c.Type != ControllerType.GenericHID)
                .Select(c => c.Type)
                .FirstOrDefault();
            if (bestType != ControllerType.Unknown)
                merged.Type = bestType;

            // Prefer a human-friendly product name over provider-y descriptions.
            var bestName = group
                .Select(c => c.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("HID ", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(bestName))
                merged.Name = bestName!;

            // Carry across the physical identity / metadata whenever any provider supplied it.
            foreach (var c in group)
            {
                if (string.IsNullOrEmpty(merged.ContainerId) && !string.IsNullOrEmpty(c.ContainerId))
                    merged.ContainerId = c.ContainerId;
                if (string.IsNullOrEmpty(merged.PnpInstanceId) && !string.IsNullOrEmpty(c.PnpInstanceId))
                    merged.PnpInstanceId = c.PnpInstanceId;
                if (string.IsNullOrEmpty(merged.SerialNumber) && !string.IsNullOrEmpty(c.SerialNumber))
                    merged.SerialNumber = c.SerialNumber;
                if (string.IsNullOrEmpty(merged.BluetoothAddress) && !string.IsNullOrEmpty(c.BluetoothAddress))
                    merged.BluetoothAddress = c.BluetoothAddress;
                if (merged.XInputSlot == null && c.XInputSlot != null)
                    merged.XInputSlot = c.XInputSlot;
            }

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

            merged.Provider = string.Join("+", group.Select(c => c.Provider).Where(p => !string.IsNullOrEmpty(p)).Distinct());

            return Canonicalize(merged);
        }

        /// <summary>
        /// Gives the merged controller a stable identity based on its physical device, so
        /// the id does not change depending on which provider/collection won the merge.
        /// </summary>
        private static ControllerInfo Canonicalize(ControllerInfo controller)
        {
            var container = NormalizeContainer(controller.ContainerId);
            if (container != null)
                controller.DeviceId = $"CTRL\\{container}\\{controller.VendorId:X4}_{controller.ProductId:X4}";
            else if (!string.IsNullOrWhiteSpace(controller.BluetoothAddress))
                controller.DeviceId = $"CTRL\\BT\\{controller.BluetoothAddress}";
            return controller;
        }

        private int GetCompletenessScore(ControllerInfo c)
        {
            int score = 0;
            if (c.BatteryPercentage.HasValue) score += 10;
            if (!string.IsNullOrEmpty(c.FirmwareVersion)) score += 5;
            if (!string.IsNullOrEmpty(c.Manufacturer)) score += 5;
            if (c.Type != ControllerType.Unknown) score += 3;
            if (c.ConnectionType != ConnectionType.Unknown) score += 2;
            if (!string.IsNullOrEmpty(c.BluetoothAddress)) score += 2;
            return score;
        }

        // ------------------------------------------------------------------ logging / events

        private void LogTransition(ControllerInfo controller, ControllerConnectionState from, ControllerConnectionState to, string reason, string kind = "TRANSITION")
        {
            var line = $"[State] {kind} {from} -> {to} device='{controller.Name}' type={controller.Type} " +
                       $"transport={controller.ConnectionType} source={controller.ConnectionStateSource ?? reason} reason={reason} " +
                       $"addr={controller.BluetoothAddress ?? "<none>"} container={controller.ContainerId ?? "<none>"} id={controller.DeviceId}";

            DiagnosticLogger.Info(line);
            _logger.LogInformation(line);
        }

        private void OnLiveConnectionStateChanged(object? sender, LiveConnectionChangedEventArgs e)
        {
            // Verified power-on/off: refresh immediately on a pool thread so one physical
            // transition produces exactly one reported transition (the debounce in
            // RefreshCoreAsync decides whether it is confirmed).
            _ = Task.Run(() => RefreshAsync());
        }

        private void OnDetectorControllerDetected(object? sender, ControllerEventArgs e)
        {
            // Intentionally ignored: ControllerManager.RefreshAsync is the single source of
            // truth, so a detector poll cannot duplicate a manager poll.
        }

        private void OnDetectorControllerRemoved(object? sender, ControllerEventArgs e)
        {
            // Intentionally ignored (see OnDetectorControllerDetected).
        }

        private void OnDetectorControllerUpdated(object? sender, ControllerEventArgs e)
        {
            // Intentionally ignored (see OnDetectorControllerDetected).
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _liveMonitor.StateChanged -= OnLiveConnectionStateChanged; } catch { }
            _refreshTimer?.Dispose();
        }

        private void OnControllerConnected(ControllerInfo controller, bool isInitial)
        {
            _logger.LogInformation(
                "Controller connected{Initial}: {Controller}",
                isInitial ? " (initial observation - no notification)" : string.Empty,
                controller);
            ControllerConnected?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.Connected, isInitial));
        }

        private void OnControllerDisconnected(ControllerInfo controller, bool isInitial)
        {
            _logger.LogInformation(
                "Controller disconnected{Initial}: {Controller}",
                isInitial ? " (initial observation - no notification)" : string.Empty,
                controller);
            ControllerDisconnected?.Invoke(this, new ControllerEventArgs(controller, ControllerEventType.Disconnected, isInitial));
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

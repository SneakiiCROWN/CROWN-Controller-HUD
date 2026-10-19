using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Power;
using Windows.Gaming.Input;
using Windows.Storage.Streams;
using Windows.System.Power;

namespace CrownControllerHUD.Services
{
    public interface IBatteryTelemetryService : IDisposable
    {
        /// <summary>
        /// Returns genuine battery telemetry for a connected controller, or null when no
        /// source can supply any. Never invents a value, and never queries a device that
        /// is not verified as connected.
        /// </summary>
        /// <param name="duplicateVidPidCount">
        /// How many OTHER connected controllers share this VID/PID. When that is non-zero,
        /// an identity-less source (Windows.Gaming.Input) cannot be attributed safely and
        /// is skipped rather than guessed.
        /// </param>
        Task<BatteryReading?> ReadAsync(ControllerInfo controller, bool force, int duplicateVidPidCount, CancellationToken ct);

        void Forget(string deviceId);
        void Reset();

        /// <summary>Human readable cache/source summary for the diagnostics panel.</summary>
        string DescribeCache();

        /// <summary>Why the most recent read produced nothing.</summary>
        string LastError { get; }
    }

    /// <summary>
    /// Genuine battery telemetry for controllers.
    ///
    /// Source order (first genuine value wins):
    ///   1. Windows.Gaming.Input RawGameController.TryGetBatteryReport
    ///      -> Windows.Devices.Power.BatteryReport (capacity + charge status)
    ///   2. Bluetooth GATT Battery Service 0x180F / Battery Level 0x2A19, read from the
    ///      Windows cache first and otherwise over the ALREADY ESTABLISHED connection
    ///   3. XInputGetBatteryInformation (categorical levels only)
    ///
    /// Hard rules enforced here:
    ///   - A powered-off / unverified device is never queried, so no Bluetooth
    ///     connection is ever forced just to read a battery.
    ///   - Readings are cached and refreshed on a minutes-scale interval, never polled
    ///     aggressively.
    ///   - A percentage is only produced when the source genuinely reports one, or when
    ///     BOTH remaining and full capacity are valid.
    ///   - XInput's BATTERY_TYPE_DISCONNECTED (which an Xbox Elite Series 2 over
    ///     Bluetooth returns) is treated as "no data", never as an empty battery.
    ///   - Charging is only ever reported when device telemetry says so; a cable being
    ///     present is not evidence.
    /// </summary>
    public sealed class BatteryTelemetryService : IBatteryTelemetryService
    {
        private static readonly Guid BatteryServiceUuid = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
        private static readonly Guid BatteryLevelUuid = new Guid("00002A19-0000-1000-8000-00805F9B34FB");

        private readonly ILogger<BatteryTelemetryService> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

        private volatile string _lastError = string.Empty;
        private bool _disposed;

        public BatteryTelemetryService(
            ILogger<BatteryTelemetryService> logger,
            IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public string LastError => _lastError;

        private sealed class CacheEntry
        {
            public BatteryReading? Reading { get; init; }
            public DateTime FetchedAtUtc { get; init; }
            public string? FailureReason { get; init; }

            /// <summary>
            /// Consecutive failed attempts. Drives an exponential backoff so a controller
            /// that simply cannot report battery is asked less and less often instead of
            /// being polled forever at full rate.
            /// </summary>
            public int ConsecutiveFailures { get; init; }
        }

        public async Task<BatteryReading?> ReadAsync(
            ControllerInfo controller,
            bool force,
            int duplicateVidPidCount,
            CancellationToken ct)
        {
            if (_disposed || controller == null)
                return null;

            var settings = _settings.CurrentValue.Battery;
            if (!settings.EnableBatteryTelemetry)
                return null;

            // Never touch a device that is not verified as switched on: a GATT query
            // against a sleeping controller would force a Bluetooth connection.
            if (controller.ConnectionState != ControllerConnectionState.Connected)
                return null;

            var id = controller.DeviceId;
            if (string.IsNullOrWhiteSpace(id))
                return null;

            if (!force && TryGetFresh(id, settings, out var fresh))
                return fresh;

            var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

            // Skip rather than queue: battery data is not urgent enough to build up work.
            if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("[Battery] A read is already in progress for {Device}; reusing the cached value", controller.Name);
                return _cache.TryGetValue(id, out var current) ? current.Reading : null;
            }

            try
            {
                if (!force && TryGetFresh(id, settings, out var nowFresh))
                    return nowFresh;

                BatteryReading? reading;
                try
                {
                    reading = await ReadCoreAsync(controller, settings, duplicateVidPidCount, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.LogError(ex, "[Battery] Unexpected failure while reading battery for {Device}", controller.Name);
                    reading = null;
                }

                _cache[id] = new CacheEntry
                {
                    Reading = reading,
                    FetchedAtUtc = DateTime.UtcNow,
                    FailureReason = reading == null ? _lastError : null,
                    ConsecutiveFailures = reading == null
                        ? (_cache.TryGetValue(id, out var prior) ? prior.ConsecutiveFailures + 1 : 1)
                        : 0
                };

                return reading;
            }
            finally
            {
                gate.Release();
            }
        }

        private bool TryGetFresh(string deviceId, BatterySettings settings, out BatteryReading? reading)
        {
            reading = null;

            if (!_cache.TryGetValue(deviceId, out var entry))
                return false;

            var age = DateTime.UtcNow - entry.FetchedAtUtc;

            // A successful reading is trusted for the configured interval. A failure is
            // retried with exponential backoff (30s, 60s, 120s, 240s, then the normal
            // refresh interval) so a controller that cannot report battery is not polled
            // aggressively, and is never asked at all while it is switched off.
            var ttl = entry.Reading != null
                ? TimeSpan.FromSeconds(settings.RefreshIntervalSeconds)
                : TimeSpan.FromSeconds(FailureBackoffSeconds(entry.ConsecutiveFailures, settings.RefreshIntervalSeconds));

            if (age >= ttl)
                return false;

            reading = entry.Reading;
            return true;
        }

        /// <summary>
        /// 30s, 60s, 120s, 240s, then the configured refresh interval. Exposed for tests
        /// because "never poll aggressively" is a requirement, not an implementation detail.
        /// </summary>
        public static int FailureBackoffSeconds(int consecutiveFailures, int refreshIntervalSeconds)
        {
            if (consecutiveFailures <= 0)
                return Math.Max(30, refreshIntervalSeconds);

            double backoff = 30 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
            return (int)Math.Min(backoff, Math.Max(30, refreshIntervalSeconds));
        }

        private async Task<BatteryReading?> ReadCoreAsync(
            ControllerInfo controller,
            BatterySettings settings,
            int duplicateVidPidCount,
            CancellationToken ct)
        {
            var failures = new List<string>();

            if (settings.EnableGameControllerApiBattery)
            {
                var reading = TryGameControllerApi(controller, duplicateVidPidCount, failures);
                if (reading != null)
                {
                    LogReading(controller, reading);
                    return reading;
                }
            }

            if (settings.EnableGattBattery && IsBluetooth(controller))
            {
                var reading = await TryGattAsync(controller, settings, failures, ct).ConfigureAwait(false);
                if (reading != null)
                {
                    LogReading(controller, reading);
                    return reading;
                }
            }

            if (settings.EnableXInputBattery)
            {
                var reading = TryXInput(controller, failures);
                if (reading != null)
                {
                    LogReading(controller, reading);
                    return reading;
                }
            }

            _lastError = failures.Count > 0
                ? string.Join(" | ", failures)
                : "no battery source applies to this controller";

            _logger.LogInformation(
                "[Battery] No genuine battery telemetry for {Device} ({Transport}) - {Reason}",
                controller.Name, controller.ConnectionType, _lastError);

            return null;
        }

        private static bool IsBluetooth(ControllerInfo controller) =>
            controller.ConnectionType == ConnectionType.Bluetooth
            || !string.IsNullOrWhiteSpace(controller.BluetoothAddress);

        // ---------------------------------------------------------------------------
        // Source 1: Windows.Gaming.Input -> Windows.Devices.Power.BatteryReport
        // ---------------------------------------------------------------------------
        private BatteryReading? TryGameControllerApi(ControllerInfo controller, int duplicateVidPidCount, List<string> failures)
        {
            try
            {
                var pads = RawGameController.RawGameControllers;
                if (pads == null || pads.Count == 0)
                {
                    failures.Add("Windows.Gaming.Input reports no raw game controllers");
                    return null;
                }

                // Windows.Gaming.Input exposes no usable device identity, so a VID/PID
                // shared with another connected controller makes attribution unsafe.
                if (duplicateVidPidCount > 0)
                {
                    failures.Add(
                        $"ambiguous: {duplicateVidPidCount + 1} connected controllers share VID {controller.VendorId:X4}/PID {controller.ProductId:X4}, " +
                        "so a Windows.Gaming.Input battery report cannot be attributed to the right physical device");
                    return null;
                }

                var matches = pads
                    .Where(p => p.HardwareVendorId == controller.VendorId && p.HardwareProductId == controller.ProductId)
                    .ToList();

                if (matches.Count == 0)
                {
                    failures.Add($"no RawGameController matches VID {controller.VendorId:X4}/PID {controller.ProductId:X4}");
                    return null;
                }

                if (matches.Count > 1)
                {
                    failures.Add($"{matches.Count} RawGameControllers share VID {controller.VendorId:X4}/PID {controller.ProductId:X4}; attribution is unsafe");
                    return null;
                }

                BatteryReport? report;
                try
                {
                    report = matches[0].TryGetBatteryReport();
                }
                catch (Exception ex)
                {
                    failures.Add("RawGameController.TryGetBatteryReport threw: " + ex.Message);
                    return null;
                }

                if (report == null)
                {
                    failures.Add("RawGameController.TryGetBatteryReport returned null (this controller does not report battery through Windows.Gaming.Input)");
                    return null;
                }

                return FromBatteryReport(report, "RawGameController.TryGetBatteryReport", failures);
            }
            catch (Exception ex)
            {
                failures.Add("Windows.Gaming.Input battery query failed: " + ex.Message);
                return null;
            }
        }

        private static BatteryReading? FromBatteryReport(BatteryReport report, string source, List<string> failures)
        {
            int? remaining = report.RemainingCapacityInMilliwattHours;
            int? full = report.FullChargeCapacityInMilliwattHours;
            int? chargeRate = report.ChargeRateInMilliwatts;

            if (remaining is < 0) remaining = null;
            if (full is <= 0) full = null;
            if (chargeRate is 0) chargeRate = null; // 0 means unknown, not idle

            // Percentage only when BOTH values are valid; otherwise we stay categorical.
            var percentage = BatteryReading.PercentageFromCapacity(remaining, full);

            var status = report.Status;

            if (status == BatteryStatus.NotPresent && percentage == null)
            {
                failures.Add($"{source}: no battery is present");
                return null;
            }

            // Use ChargeRateInMilliwatts for genuine charging detection:
            // positive = charging, negative = discharging, null/zero = unknown/idle.
            // This is more reliable than Status alone because Status can be Idle when plugged in.
            bool supportsCharging = chargeRate.HasValue || status != BatteryStatus.NotPresent;
            bool charging = chargeRate.GetValueOrDefault(0) > 0;

            // Fallback to Status if ChargeRate is unavailable
            if (!chargeRate.HasValue && status == BatteryStatus.Charging)
                charging = true;

            BatteryState state;
            if (charging)
                state = percentage is >= 95 ? BatteryState.Full : BatteryState.Charging;
            else if (percentage.HasValue)
                state = BatteryReading.StateFromPercentage(percentage.Value);
            else if (status == BatteryStatus.Discharging || (chargeRate.HasValue && chargeRate.Value < 0))
                state = BatteryState.Discharging;
            else
                state = BatteryState.Unknown; // status is known, but the level is not

            return new BatteryReading
            {
                Source = BatterySourceKind.GameControllerApi,
                SourceDescription = source,
                Percentage = percentage,
                SupportsPercentage = percentage.HasValue,
                Charging = charging,
                SupportsCharging = supportsCharging,
                RemainingMwh = remaining,
                FullChargeMwh = full,
                ChargeRateMw = chargeRate,
                State = state,
                Error = percentage.HasValue
                    ? null
                    : $"Windows reported no valid capacity pair (remaining={(remaining?.ToString() ?? "null")} mWh, full={(full?.ToString() ?? "null")} mWh)"
            };
        }

        // ---------------------------------------------------------------------------
        // Source 2: Bluetooth GATT 0x180F / 0x2A19
        // ---------------------------------------------------------------------------
        private async Task<BatteryReading?> TryGattAsync(
            ControllerInfo controller,
            BatterySettings settings,
            List<string> failures,
            CancellationToken ct)
        {
            if (!DeviceIdentity.TryParseAddress(controller.BluetoothAddress, out var address))
            {
                failures.Add("no usable Bluetooth address, so the GATT battery service cannot be read");
                return null;
            }

            if (controller.ConnectionState != ControllerConnectionState.Connected)
            {
                failures.Add("device is not connected; refusing to force a Bluetooth connection just to read a battery level");
                return null;
            }

            BluetoothLEDevice? device = null;

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromMilliseconds(settings.GattTimeoutMs));

                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(linked.Token).ConfigureAwait(false);
                if (device == null)
                {
                    failures.Add("Windows could not open a Bluetooth device object for this address");
                    return null;
                }

                // Cached first: this issues no Bluetooth traffic at all. Windows populates
                // this cache when the controller connects.
                var reading = await ReadBatteryLevelAsync(device, BluetoothCacheMode.Cached, failures, linked.Token).ConfigureAwait(false);
                if (reading != null)
                {
                    _logger.LogInformation("[Battery] {Device}: read battery level {Percent}% from the Windows Bluetooth cache (no connection was forced)", controller.Name, reading.Percentage);
                    return reading;
                }

                if (settings.UseCachedGattReadsOnly)
                {
                    failures.Add("cached GATT read had no value and uncached GATT reads are disabled in settings");
                    return null;
                }

                // One uncached read, over the connection that already exists, at most once
                // per refresh interval. This never dials a powered-off controller because
                // the caller only reaches here for a verified Connected device.
                _logger.LogInformation(
                    "[Battery] {Device}: Windows cache had no battery level; performing one uncached GATT read over the existing connection",
                    controller.Name);

                return await ReadBatteryLevelAsync(device, BluetoothCacheMode.Uncached, failures, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                failures.Add($"GATT battery read timed out after {settings.GattTimeoutMs} ms");
                return null;
            }
            catch (Exception ex)
            {
                failures.Add("GATT battery read failed: " + ex.Message);
                return null;
            }
            finally
            {
                try { device?.Dispose(); } catch { /* best effort */ }
            }
        }

        private static async Task<BatteryReading?> ReadBatteryLevelAsync(
            BluetoothLEDevice device,
            BluetoothCacheMode mode,
            List<string> failures,
            CancellationToken ct)
        {
            GattDeviceService? service = null;

            try
            {
                var services = await device.GetGattServicesForUuidAsync(BatteryServiceUuid, mode).AsTask(ct).ConfigureAwait(false);
                if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                {
                    failures.Add($"battery service 0x180F unavailable ({services.Status}, {mode})");
                    return null;
                }

                service = services.Services[0];

                var characteristics = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, mode).AsTask(ct).ConfigureAwait(false);
                if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                {
                    failures.Add($"battery level characteristic 0x2A19 unavailable ({characteristics.Status}, {mode})");
                    return null;
                }

                var characteristic = characteristics.Characteristics[0];

                var result = await characteristic.ReadValueAsync(mode).AsTask(ct).ConfigureAwait(false);
                if (result.Status != GattCommunicationStatus.Success || result.Value == null || result.Value.Length == 0)
                {
                    failures.Add($"reading 0x2A19 failed ({result.Status}, {mode})");
                    return null;
                }

                var buffer = new byte[result.Value.Length];
                using (var reader = DataReader.FromBuffer(result.Value))
                    reader.ReadBytes(buffer);

                var level = buffer[0];

                // A wild value means we do not understand this device's report: say so
                // rather than clamping it into a believable-looking number.
                if (level > 100)
                {
                    failures.Add($"GATT 0x2A19 returned an out-of-range level ({level}); ignoring it instead of clamping");
                    return null;
                }

                return new BatteryReading
                {
                    Source = BatterySourceKind.BluetoothGatt,
                    SourceDescription = $"GATT 0x180F/0x2A19 ({mode})",
                    Percentage = level,
                    SupportsPercentage = true,
                    // The Battery Level characteristic carries no charge status.
                    Charging = false,
                    SupportsCharging = false,
                    State = BatteryReading.StateFromPercentage(level)
                };
            }
            finally
            {
                try { service?.Dispose(); } catch { /* best effort */ }
            }
        }

        // ---------------------------------------------------------------------------
        // Source 3: XInput (categorical only)
        // ---------------------------------------------------------------------------
        private BatteryReading? TryXInput(ControllerInfo controller, List<string> failures)
        {
            if (controller.XInputSlot is not int slot)
            {
                failures.Add("controller is not in an XInput slot, so XInput battery reporting does not apply");
                return null;
            }

            var info = XInputInterop.GetBatteryInformation(slot);
            if (info == null)
            {
                failures.Add($"XInputGetBatteryInformation returned no data for slot {slot}");
                return null;
            }

            if (!info.Usable)
            {
                failures.Add(info.Wired
                    ? $"XInput reports the device as {(info.Type == XInputInterop.BATTERY_TYPE_WIRED ? "wired" : "disconnected")} with no battery measurement (type=0x{info.Type:X2}, level=0x{info.Level:X2}); this is NOT an empty battery"
                    : $"XInput battery type 0x{info.Type:X2} is not a measurable battery");
                return null;
            }

            var state = info.Level switch
            {
                XInputInterop.BATTERY_LEVEL_EMPTY => BatteryState.Critical,
                XInputInterop.BATTERY_LEVEL_LOW => BatteryState.Low,
                XInputInterop.BATTERY_LEVEL_MEDIUM => BatteryState.Medium,
                XInputInterop.BATTERY_LEVEL_FULL => BatteryState.Full,
                _ => BatteryState.Unknown
            };

            if (state == BatteryState.Unknown)
            {
                failures.Add($"XInput reported an unrecognised battery level 0x{info.Level:X2}");
                return null;
            }

            return new BatteryReading
            {
                Source = BatterySourceKind.XInputLevel,
                SourceDescription = $"{info.Source} (slot {slot})",
                Percentage = null,            // XInput has no percentage concept
                SupportsPercentage = false,
                Charging = false,             // and no charging concept
                SupportsCharging = false,
                State = state,
                Error = "XInput reports a categorical level only; no exact percentage is available"
            };
        }

        private void LogReading(ControllerInfo controller, BatteryReading reading)
        {
            _lastError = string.Empty;

            _logger.LogInformation(
                "[Battery] {Device}: {Description} -> {Percent} state={State} charging={Charging} " +
                "(percentage={SupportsPct}, chargingKnown={ChargingKnown}, chargeRate={ChargeRate}{Extra})",
                controller.Name,
                reading.SourceDescription,
                reading.Percentage.HasValue ? reading.Percentage.Value + "%" : "no percentage",
                reading.State,
                reading.Charging,
                reading.SupportsPercentage ? "yes" : "no",
                reading.SupportsCharging ? "yes" : "no",
                reading.ChargeRateMw.HasValue ? reading.ChargeRateMw.Value + "mW" : "unknown",
                string.IsNullOrEmpty(reading.Error) ? string.Empty : ", note: " + reading.Error);
        }

        public void Forget(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return;

            _cache.TryRemove(deviceId, out _);
            _gates.TryRemove(deviceId, out _);
        }

        public void Reset()
        {
            _cache.Clear();
            _gates.Clear();
            _lastError = string.Empty;
        }

        public string DescribeCache()
        {
            if (_cache.IsEmpty)
                return "no battery readings cached";

            var parts = _cache
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv =>
                {
                    var age = DateTime.UtcNow - kv.Value.FetchedAtUtc;
                    if (kv.Value.Reading == null)
                        return $"{Shorten(kv.Key)}: unavailable ({age.TotalSeconds:F0}s ago)";

                    var r = kv.Value.Reading;
                    return $"{Shorten(kv.Key)}: {(r.Percentage.HasValue ? r.Percentage.Value + "%" : r.State.ToString())} via {r.SourceDescription} ({age.TotalSeconds:F0}s ago)";
                });

            return string.Join("; ", parts);
        }

        private static string Shorten(string deviceId)
        {
            // Keep the log readable and avoid dumping long device paths.
            var parts = deviceId.Split('\\');
            return parts.Length >= 2 ? parts[^1] : deviceId;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Reset();

            foreach (var gate in _gates.Values)
            {
                try { gate.Dispose(); } catch { /* shutting down */ }
            }

            _gates.Clear();
        }
    }
}

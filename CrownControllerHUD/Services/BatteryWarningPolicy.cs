using System;
using System.Collections.Generic;
using System.Linq;
using CrownControllerHUD.Models;

namespace CrownControllerHUD.Services
{
    public enum BatteryWarningKind
    {
        None = 0,

        /// <summary>An exact percentage crossed one of the configured warning levels.</summary>
        Threshold = 1,

        /// <summary>Only a categorical level was available and it is LOW.</summary>
        CategoricalLow = 2,

        /// <summary>Only a categorical level was available and it is EMPTY.</summary>
        CategoricalEmpty = 3,

        /// <summary>The battery climbed back above every enabled level.</summary>
        Recovered = 4
    }

    public sealed class BatteryWarningDecision
    {
        public bool ShouldNotify { get; init; }
        public BatteryWarningKind Kind { get; init; } = BatteryWarningKind.None;

        /// <summary>The configured level that was crossed (percentage warnings only).</summary>
        public int? ThresholdPercent { get; init; }

        public bool IsCritical { get; init; }

        /// <summary>True when the warning came from a categorical level (no percentage exists).</summary>
        public bool IsCategorical { get; init; }

        public int? Percentage { get; init; }
        public BatteryState State { get; init; } = BatteryState.Unknown;

        /// <summary>Why this decision was made - written verbatim to the log.</summary>
        public string Reason { get; init; } = string.Empty;

        public static BatteryWarningDecision Suppressed(string reason) =>
            new() { ShouldNotify = false, Reason = reason };
    }

    public interface IBatteryWarningPolicy
    {
        BatteryWarningDecision Evaluate(string deviceId, int? percentage, BatteryState state, bool charging);

        /// <summary>Forgets a device (it went away), so a future appearance warns again.</summary>
        void Forget(string deviceId);

        void Reset();
    }

    /// <summary>
    /// Decides when a battery warning is worth showing.
    ///
    /// Rules, in order:
    ///  - Confirmed charging re-arms the whole ladder and never warns.
    ///  - With an exact percentage, the MOST SEVERE enabled level that has been crossed is
    ///    announced, and only if that level is more severe than the last one announced.
    ///    A controller that appears at 3% therefore produces ONE 5% warning, not five.
    ///  - With only categorical telemetry (XInput), LOW and EMPTY each warn once.
    ///  - The ladder is only re-armed when the battery climbs above the highest enabled
    ///    level by a hysteresis margin, so a level jittering around a threshold cannot
    ///    repeat its warning.
    ///  - No telemetry (Unknown / null percentage) never warns, so a missing reading can
    ///    never be presented as 0%.
    /// </summary>
    public sealed class BatteryWarningPolicy : IBatteryWarningPolicy
    {
        /// <summary>How far above the highest level the battery must climb to re-arm.</summary>
        public const int RecoveryHysteresis = 5;

        private readonly Func<NotificationSettings> _settingsAccessor;
        private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public BatteryWarningPolicy(Func<NotificationSettings> settingsAccessor)
        {
            _settingsAccessor = settingsAccessor ?? throw new ArgumentNullException(nameof(settingsAccessor));
        }

        private sealed class DeviceState
        {
            public int? LastNotifiedBand;
            public bool CategoricalLowNotified;
            public bool CategoricalEmptyNotified;
            public bool RecoveredNotified;
        }

        public BatteryWarningDecision Evaluate(string deviceId, int? percentage, BatteryState state, bool charging)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return BatteryWarningDecision.Suppressed("no device identity");

            var settings = _settingsAccessor() ?? new NotificationSettings();

            var levels = (settings.BatteryWarnings ?? new List<BatteryWarningLevel>())
                .Where(l => l.Enabled)
                .OrderByDescending(l => l.Percent)
                .ToList();

            lock (_gate)
            {
                var device = GetState(deviceId);

                if (charging)
                {
                    bool wasArmed = device.LastNotifiedBand.HasValue
                                    || device.CategoricalLowNotified
                                    || device.CategoricalEmptyNotified;

                    device.LastNotifiedBand = null;
                    device.CategoricalLowNotified = false;
                    device.CategoricalEmptyNotified = false;
                    device.RecoveredNotified = false;

                    return BatteryWarningDecision.Suppressed(
                        wasArmed ? "charging: warning ladder re-armed" : "charging: no warnings while charging");
                }

                if (levels.Count == 0)
                    return BatteryWarningDecision.Suppressed("no battery warning levels are enabled");

                if (percentage.HasValue)
                    return EvaluatePercentage(device, percentage.Value, state, levels, settings);

                if (state == BatteryState.Critical)
                {
                    if (device.CategoricalEmptyNotified)
                        return BatteryWarningDecision.Suppressed("categorical EMPTY already announced");

                    device.CategoricalEmptyNotified = true;
                    device.LastNotifiedBand = levels[^1].Percent;

                    return new BatteryWarningDecision
                    {
                        ShouldNotify = true,
                        Kind = BatteryWarningKind.CategoricalEmpty,
                        IsCritical = true,
                        IsCategorical = true,
                        State = state,
                        Reason = "categorical battery level is EMPTY (no percentage available)"
                    };
                }

                if (state == BatteryState.Low)
                {
                    if (device.CategoricalLowNotified)
                        return BatteryWarningDecision.Suppressed("categorical LOW already announced");

                    device.CategoricalLowNotified = true;

                    return new BatteryWarningDecision
                    {
                        ShouldNotify = true,
                        Kind = BatteryWarningKind.CategoricalLow,
                        IsCategorical = true,
                        State = state,
                        Reason = "categorical battery level is LOW (no percentage available)"
                    };
                }

                return BatteryWarningDecision.Suppressed(
                    state == BatteryState.Unknown
                        ? "no genuine battery telemetry available"
                        : $"categorical level {state} does not warrant a warning");
            }
        }

        private static BatteryWarningDecision EvaluatePercentage(
            DeviceState device,
            int percentage,
            BatteryState state,
            List<BatteryWarningLevel> levels,
            NotificationSettings settings)
        {
            int highest = levels[0].Percent;

            if (percentage > highest + RecoveryHysteresis)
            {
                device.LastNotifiedBand = null;
                device.CategoricalLowNotified = false;
                device.CategoricalEmptyNotified = false;

                bool announceRecovery = settings.NotifyOnBatteryRecovered && !device.RecoveredNotified;
                device.RecoveredNotified = true;

                if (announceRecovery)
                {
                    return new BatteryWarningDecision
                    {
                        ShouldNotify = true,
                        Kind = BatteryWarningKind.Recovered,
                        Percentage = percentage,
                        State = state,
                        Reason = $"battery recovered to {percentage}% (above every warning level)"
                    };
                }

                return BatteryWarningDecision.Suppressed(
                    $"battery {percentage}% is above all warning levels - ladder re-armed");
            }

            // levels are ordered highest -> lowest, so the LAST match is the most severe
            // level that the current percentage has reached.
            var applicable = levels.LastOrDefault(l => percentage <= l.Percent);

            if (applicable == null)
                return BatteryWarningDecision.Suppressed($"no warning level crossed at {percentage}%");

            if (device.LastNotifiedBand.HasValue && applicable.Percent >= device.LastNotifiedBand.Value)
            {
                return BatteryWarningDecision.Suppressed(
                    $"{applicable.Percent}% already announced (last announced band {device.LastNotifiedBand}%)");
            }

            device.LastNotifiedBand = applicable.Percent;

            return new BatteryWarningDecision
            {
                ShouldNotify = true,
                Kind = BatteryWarningKind.Threshold,
                ThresholdPercent = applicable.Percent,
                IsCritical = applicable.Critical,
                Percentage = percentage,
                State = state,
                Reason = $"crossed the {applicable.Percent}% warning level (battery at {percentage}%)"
            };
        }

        private DeviceState GetState(string deviceId)
        {
            if (!_devices.TryGetValue(deviceId, out var state))
            {
                state = new DeviceState();
                _devices[deviceId] = state;
            }

            return state;
        }

        public void Forget(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return;

            lock (_gate)
                _devices.Remove(deviceId);
        }

        public void Reset()
        {
            lock (_gate)
                _devices.Clear();
        }
    }
}

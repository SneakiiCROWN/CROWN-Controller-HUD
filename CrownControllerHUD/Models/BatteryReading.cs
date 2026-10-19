using System;

namespace CrownControllerHUD.Models
{
    /// <summary>
    /// Where a battery reading genuinely came from. Every reading carries its source so
    /// the UI and the log can state exactly what is known and what is unsupported,
    /// instead of inventing a value.
    /// </summary>
    public enum BatterySourceKind
    {
        /// <summary>No genuine telemetry was available. Never rendered as 0%.</summary>
        None = 0,

        /// <summary>Windows.Gaming.Input RawGameController/Gamepad TryGetBatteryReport (Windows.Devices.Power.BatteryReport).</summary>
        GameControllerApi = 1,

        /// <summary>Bluetooth GATT Battery Service 0x180F / Battery Level 0x2A19 on an already-connected device.</summary>
        BluetoothGatt = 2,

        /// <summary>XInputGetBatteryInformation. Categorical only - XInput never reports a percentage.</summary>
        XInputLevel = 3,

        /// <summary>
        /// Raw HID feature report. The byte layout of these vendor reports is not
        /// documented, so this is DIAGNOSTIC ONLY and must never produce a percentage.
        /// </summary>
        HidFeatureReport = 4
    }

    /// <summary>
    /// One genuine battery reading. Fields that the source cannot supply stay null /
    /// false, so "unknown" is always distinguishable from "0".
    /// </summary>
    public sealed class BatteryReading
    {
        public BatterySourceKind Source { get; init; } = BatterySourceKind.None;

        /// <summary>Human readable source, e.g. "GATT 0x180F/0x2A19".</summary>
        public string SourceDescription { get; init; } = string.Empty;

        /// <summary>Exact percentage, only when the source genuinely reports one.</summary>
        public int? Percentage { get; init; }

        /// <summary>Categorical level. Always set (Unknown when nothing is known).</summary>
        public BatteryState State { get; init; } = BatteryState.Unknown;

        /// <summary>True only when the device telemetry confirms it is charging.</summary>
        public bool Charging { get; init; }

        /// <summary>False when the source cannot report a percentage at all (e.g. XInput).</summary>
        public bool SupportsPercentage { get; init; }

        /// <summary>False when the source cannot report charging state at all.</summary>
        public bool SupportsCharging { get; init; }

        /// <summary>Remaining capacity in mWh, only when Windows reports a valid value.</summary>
        public int? RemainingMwh { get; init; }

        /// <summary>Full charge capacity in mWh, only when Windows reports a valid value.</summary>
        public int? FullChargeMwh { get; init; }

        /// <summary>Charge rate in mW. Positive = charging, negative = discharging, zero = idle.
        /// Only when Windows reports a valid value.</summary>
        public int? ChargeRateMw { get; init; }

        /// <summary>Why the read failed or why a field is unsupported. Null when clean.</summary>
        public string? Error { get; init; }

        public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Maps an exact percentage onto the categorical level used by the UI and the
        /// warning policy. Only ever called with a genuine percentage.
        /// </summary>
        public static BatteryState StateFromPercentage(int percentage) => percentage switch
        {
            >= 95 => BatteryState.Full,
            >= 50 => BatteryState.High,
            >= 15 => BatteryState.Medium,
            >= 5 => BatteryState.Low,
            _ => BatteryState.Critical
        };

        /// <summary>
        /// Percentage derived from remaining/full capacity, but only when BOTH values are
        /// present and plausible. Returns null rather than guessing.
        /// </summary>
        public static int? PercentageFromCapacity(int? remainingMwh, int? fullChargeMwh)
        {
            if (remainingMwh is not int remaining || fullChargeMwh is not int full)
                return null;

            // A full capacity of zero (or less) means Windows has no usable data, and
            // remaining > full is nonsense; both must not become a fabricated number.
            if (full <= 0 || remaining < 0 || remaining > full)
                return null;

            var percent = (int)Math.Round(remaining * 100.0 / full, MidpointRounding.AwayFromZero);
            return Math.Clamp(percent, 0, 100);
        }
    }
}

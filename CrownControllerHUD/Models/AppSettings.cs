using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CrownControllerHUD.Models
{
    public class AppSettings
    {
        public GeneralSettings General { get; set; } = new();
        public NotificationSettings Notifications { get; set; } = new();
        public UISettings UI { get; set; } = new();
        public ControllerSettings Controllers { get; set; } = new();
        public AdvancedSettings Advanced { get; set; } = new();
        public BatterySettings Battery { get; set; } = new();

        /// <summary>
        /// Clamps anything that would produce nonsense at runtime (out-of-range
        /// percentages, absurd intervals, an empty warning ladder). Called after load so
        /// a hand-edited or partially written settings file can never break the app.
        /// </summary>
        public void Normalize()
        {
            Battery ??= new BatterySettings();
            Battery.Normalize();

            Notifications ??= new NotificationSettings();
            Notifications.Normalize();
        }
    }

    public class BatterySettings
    {
        /// <summary>Master switch for all genuine battery telemetry.</summary>
        public bool EnableBatteryTelemetry { get; set; } = true;

        /// <summary>Use Windows.Gaming.Input TryGetBatteryReport (Windows.Devices.Power.BatteryReport).</summary>
        public bool EnableGameControllerApiBattery { get; set; } = true;

        /// <summary>Use Bluetooth GATT Battery Service 0x180F / Battery Level 0x2A19.</summary>
        public bool EnableGattBattery { get; set; } = true;

        /// <summary>Use XInputGetBatteryInformation (categorical levels only).</summary>
        public bool EnableXInputBattery { get; set; } = true;

        /// <summary>Read the unverified HID feature report and LOG it for diagnosis only.
        /// It is never used as a percentage because the byte layout is undocumented.</summary>
        public bool EnableHidBatteryDiagnostics { get; set; } = true;

        /// <summary>
        /// How long a reading is trusted before a refresh is attempted. Battery levels
        /// change slowly, so this is deliberately minutes rather than seconds: the app
        /// must never poll the controller aggressively or force Bluetooth connections.
        /// </summary>
        public int RefreshIntervalSeconds { get; set; } = 300;

        /// <summary>Timeout for a single Bluetooth GATT battery read.</summary>
        public int GattTimeoutMs { get; set; } = 6000;

        /// <summary>After this long without a fresh reading the value is marked stale.</summary>
        public int StaleAfterSeconds { get; set; } = 900;

        /// <summary>
        /// When true, only the Windows Bluetooth cache is read and the app never issues
        /// an uncached GATT read. (The cached value is normally populated by Windows when
        /// the controller connects, so this avoids any extra Bluetooth traffic at all.)
        /// </summary>
        public bool UseCachedGattReadsOnly { get; set; } = false;

        public void Normalize()
        {
            RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 30, 3600);
            GattTimeoutMs = Math.Clamp(GattTimeoutMs, 1000, 20000);
            StaleAfterSeconds = Math.Clamp(StaleAfterSeconds, RefreshIntervalSeconds, 86400);
        }
    }

    /// <summary>One configurable battery warning level (e.g. 20%).</summary>
    public class BatteryWarningLevel
    {
        public int Percent { get; set; } = 20;
        public bool Enabled { get; set; } = true;

        /// <summary>Critical levels are rendered with the alert styling and no sound softening.</summary>
        public bool Critical { get; set; }

        public BatteryWarningLevel() { }

        public BatteryWarningLevel(int percent, bool enabled = true, bool critical = false)
        {
            Percent = percent;
            Enabled = enabled;
            Critical = critical;
        }

        public override string ToString() => $"{Percent}%{(Enabled ? "" : " (off)")}{(Critical ? " critical" : "")}";
    }

    public class GeneralSettings
    {
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool CheckForUpdates { get; set; } = true;
        public string Language { get; set; } = "en-US";
        public string Theme { get; set; } = "Dark"; // Dark, Light, System
    }

    public class NotificationSettings
    {
        public bool EnableNotifications { get; set; } = true;
        public bool NotifyOnConnect { get; set; } = true;
        public bool NotifyOnDisconnect { get; set; } = true;
        public bool NotifyOnLowBattery { get; set; } = true;
        public bool NotifyOnCriticalBattery { get; set; } = true;
        public bool NotifyOnCharging { get; set; } = false;
        public bool NotifyOnFullCharge { get; set; } = false;

        /// <summary>
        /// Master switch for the configurable battery warning ladder (50/20/15/10/5 by
        /// default). The legacy per-severity toggles below are still honoured on top of
        /// this, so an existing install that disabled low battery notifications keeps them
        /// disabled.
        /// </summary>
        public bool NotifyOnBatteryWarnings { get; set; } = true;
        public int LowBatteryThreshold { get; set; } = 15;
        public int CriticalBatteryThreshold { get; set; } = 5;
        public int NotificationDurationMs { get; set; } = 5000;
        public bool PlaySound { get; set; } = true;
        public string SoundPath { get; set; } = string.Empty;
        public bool ShowInActionCenter { get; set; } = true;

        /// <summary>
        /// The configurable warning ladder used when a controller reports an exact
        /// percentage. Each level warns at most once per discharge, and only the most
        /// severe newly-crossed level is announced, so a single low reading cannot
        /// produce a burst of notifications.
        /// </summary>
        public List<BatteryWarningLevel> BatteryWarnings { get; set; } = CreateDefaultBatteryWarnings();

        /// <summary>Notify once when the battery climbs back above every enabled level
        /// (i.e. after charging), so the next discharge warns again.</summary>
        public bool NotifyOnBatteryRecovered { get; set; } = false;

        public static List<BatteryWarningLevel> CreateDefaultBatteryWarnings() => new()
        {
            new BatteryWarningLevel(50),
            new BatteryWarningLevel(20),
            new BatteryWarningLevel(15),
            new BatteryWarningLevel(10, critical: true),
            new BatteryWarningLevel(5, critical: true)
        };

        public void Normalize()
        {
            LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 99);
            CriticalBatteryThreshold = Math.Clamp(CriticalBatteryThreshold, 1, 99);
            NotificationDurationMs = Math.Clamp(NotificationDurationMs, 1500, 60000);

            if (BatteryWarnings == null || BatteryWarnings.Count == 0)
                BatteryWarnings = CreateDefaultBatteryWarnings();

            foreach (var level in BatteryWarnings)
                level.Percent = Math.Clamp(level.Percent, 1, 99);

            // De-duplicate and order from highest to lowest so "most severe crossed" is
            // well defined even if the user hand-edits the file.
            BatteryWarnings = BatteryWarnings
                .GroupBy(l => l.Percent)
                .Select(g => g.First())
                .OrderByDescending(l => l.Percent)
                .ToList();
        }
    }

    public class UISettings
    {
        public double WindowWidth { get; set; } = 800;
        public double WindowHeight { get; set; } = 600;
        public double WindowLeft { get; set; } = -1; // -1 = center
        public double WindowTop { get; set; } = -1;
        public bool RememberWindowPosition { get; set; } = true;
        public bool ShowBatteryPercentage { get; set; } = true;
        public bool ShowConnectionType { get; set; } = true;
        public bool ShowControllerModel { get; set; } = true;
        public bool ShowFirmwareVersion { get; set; } = false;
        public bool CompactMode { get; set; } = false;
        public int RefreshIntervalMs { get; set; } = 2000;
        public bool EnableAnimations { get; set; } = true;
        public string AccentColor { get; set; } = "#0078D4"; // Xbox Green
        public string NotificationPosition { get; set; } = "BottomRight"; // BottomRight, BottomLeft, TopRight, TopLeft, TopCenter, BottomCenter
    }

    public class ControllerSettings
    {
        public bool AutoDetectControllers { get; set; } = true;
        public bool ShowDisconnectedControllers { get; set; } = false;
        public bool ShowGenericControllers { get; set; } = true;
        public int DisconnectedTimeoutMinutes { get; set; } = 30;
        public List<string> HiddenControllerIds { get; set; } = new();
        public Dictionary<string, ControllerOverrideSettings> Overrides { get; set; } = new();
    }

    public class ControllerOverrideSettings
    {
        public string CustomName { get; set; } = string.Empty;
        public bool EnableNotifications { get; set; } = true;
        public int? CustomLowBatteryThreshold { get; set; }
        public int? CustomCriticalBatteryThreshold { get; set; }
    }

    public class AdvancedSettings
    {
        public bool EnableDebugLogging { get; set; } = false;
        public bool UseWinRTForXbox { get; set; } = true;
        public bool UseHIDForBattery { get; set; } = true;
        public bool UseBluetoothLE { get; set; } = true;
        public int DeviceScanIntervalMs { get; set; } = 5000;
        public bool EnableHidRawReports { get; set; } = false;
        public string LogPath { get; set; } = string.Empty;
        public int MaxLogSizeMB { get; set; } = 10;
        public int MaxLogFiles { get; set; } = 5;
    }
}
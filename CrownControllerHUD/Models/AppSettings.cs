using System;
using System.Collections.Generic;
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
        public int LowBatteryThreshold { get; set; } = 15;
        public int CriticalBatteryThreshold { get; set; } = 5;
        public int NotificationDurationMs { get; set; } = 5000;
        public bool PlaySound { get; set; } = true;
        public string SoundPath { get; set; } = string.Empty;
        public bool ShowInActionCenter { get; set; } = true;
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
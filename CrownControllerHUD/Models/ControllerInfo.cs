using System;
using System.Collections.Generic;

namespace CrownControllerHUD.Models
{
    public enum ControllerType
    {
        Unknown,
        XboxOne,
        XboxOneS,
        XboxOneElite,
        XboxEliteSeries2,
        XboxSeriesX,
        XboxSeriesS,
        Xbox360,
        XboxAdaptive,
        PlayStation4,
        PlayStation5,
        NintendoSwitchPro,
        NintendoSwitchJoyConLeft,
        NintendoSwitchJoyConRight,
        SteamController,
        SteamDeck,
        GenericXInput,
        GenericHID
    }

    public enum ConnectionType
    {
        Unknown,
        USB,
        Bluetooth,
        XboxWirelessAdapter,
        NintendoWireless,
        PlayStationWireless
    }

    /// <summary>
    /// Verified physical connection state of a controller.
    ///
    /// Deliberately distinct from simply being enumerated: a paired Bluetooth
    /// controller keeps its device nodes installed and "present" while it is
    /// powered off, so device presence can never prove that it is switched on.
    /// </summary>
    public enum ControllerConnectionState
    {
        /// <summary>No live signal has confirmed either state yet. Never shown as connected.</summary>
        Unknown = 0,

        /// <summary>A live signal (or the absence of a wired device) confirms it is not connected.</summary>
        Disconnected = 1,

        /// <summary>A live signal confirms the controller is powered on and connected.</summary>
        Connected = 2
    }

    public enum BatteryState
    {
        Unknown,
        Charging,
        Discharging,
        Full,
        Critical,
        Low,
        Medium,
        High
    }

    public class ControllerInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// User-defined nickname for this controller. When set, this takes precedence
        /// over the detected Name in all UI displays. Persisted in ControllerSettings.Overrides.
        /// </summary>
        public string? CustomName { get; set; }
        
        /// <summary>
        /// The effective display name - returns CustomName if set, otherwise Name.
        /// </summary>
        public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName : Name;

        public ControllerType Type { get; set; } = ControllerType.Unknown;
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
        public bool IsConnected { get; set; }
        public DateTime ConnectedAt { get; set; }
        public DateTime? LastSeen { get; set; }

        // Verified live connection state. "IsConnected" is always kept in step with
        // this value; it is true only for ControllerConnectionState.Connected.
        public ControllerConnectionState ConnectionState { get; set; } = ControllerConnectionState.Unknown;

        /// <summary>
        /// Which live signal last decided the connection state, e.g.
        /// "System.Devices.Aep.IsConnected=false" or "pnp-present" (wired device).
        /// </summary>
        public string? ConnectionStateSource { get; set; }

        public DateTime? ConnectionStateChangedAt { get; set; }

        // Which detection provider produced this record (WinRT / HID / PnP ...)
        public string Provider { get; set; } = string.Empty;

        // Reliable physical-device identity used to correlate the same controller
        // reported by several providers / several HID top-level collections.
        // ContainerId is the Windows device container (one per physical device).
        public string? ContainerId { get; set; }
        public string? PnpInstanceId { get; set; }
        public int? XInputSlot { get; set; }

        /// <summary>
        /// Bluetooth MAC address (12 uppercase hex digits), when the device is a
        /// Bluetooth controller. This is the key used to look up live connection
        /// state from <c>ILiveConnectionMonitor</c>.
        /// </summary>
        public string? BluetoothAddress { get; set; }

        // Battery information.
        // Every value here comes from genuine device telemetry; a missing reading stays
        // null/Unknown and is NEVER rendered as 0%.
        public int? BatteryPercentage { get; set; } // Null if hardware doesn't report percentage
        public BatteryState BatteryState { get; set; } = BatteryState.Unknown;
        public bool IsCharging { get; set; }
        public bool HasBattery { get; set; }
        public string? BatteryChemistry { get; set; }
        public int? BatteryCapacityWh { get; set; }
        public int? BatteryVoltageMv { get; set; }
        public int? BatteryCurrentMa { get; set; }
        public DateTime? BatteryLastUpdated { get; set; }

        /// <summary>
        /// Which genuine source produced the current reading, e.g. "GATT 0x180F/0x2A19"
        /// or "RawGameController.TryGetBatteryReport". Null until a reading exists.
        /// </summary>
        public string? BatterySource { get; set; }

        /// <summary>Why the last battery read failed, or which field is unsupported.</summary>
        public string? BatteryError { get; set; }

        /// <summary>Remaining capacity in mWh, only when Windows reports a valid value.</summary>
        public int? BatteryRemainingMwh { get; set; }

        /// <summary>Full charge capacity in mWh, only when Windows reports a valid value.</summary>
        public int? BatteryFullChargeMwh { get; set; }

        /// <summary>Charge rate in mW. Positive = charging, negative = discharging, zero/unknown = not reported.</summary>
        public int? BatteryChargeRateMw { get; set; }

        /// <summary>True when the source genuinely reports an exact percentage.</summary>
        public bool BatterySupportsPercentage { get; set; }

        /// <summary>True when the source genuinely reports charging state.</summary>
        public bool BatterySupportsCharging { get; set; }

        /// <summary>True when the cached reading has exceeded its freshness window.</summary>
        public bool BatteryIsStale { get; set; }

        /// <summary>
        /// When the current battery telemetry is unavailable (e.g., over USB), this
        /// holds the last successfully read percentage and its timestamp. Clearly
        /// labelled as stale - never presented as current.
        /// </summary>
        public int? LastKnownBatteryPercentage { get; set; }
        public DateTime? LastKnownBatteryReadAt { get; set; }
        public string? LastKnownBatterySource { get; set; }

        // Hardware identification
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public ushort VersionNumber { get; set; }
        public string? SerialNumber { get; set; }
        public string? Manufacturer { get; set; }
        public string? FirmwareVersion { get; set; }
        public string? HardwareRevision { get; set; }

        // Capabilities
        public bool SupportsVibration { get; set; }
        public bool SupportsAudio { get; set; }
        public bool SupportsBatteryReporting { get; set; }
        public bool SupportsChargingStatus { get; set; }
        public bool SupportsWireless { get; set; }
        public List<string> SupportedFeatures { get; set; } = new();

        // Raw data for debugging
        public Dictionary<string, object> RawProperties { get; set; } = new();

        public override string ToString()
        {
            return $"{Name} ({Type}) - {ConnectionType} - {ConnectionState} - Battery: {(BatteryPercentage.HasValue ? $"{BatteryPercentage}%" : "Unknown")}";
        }
    }

    public class ControllerEventArgs : EventArgs
    {
        public ControllerInfo Controller { get; }
        public ControllerEventType EventType { get; }

        /// <summary>
        /// True when this is the very first observation of the device rather than a
        /// real power/plug transition. Initial observations must not raise user-facing
        /// notifications (they are not transitions, and would otherwise fire on every
        /// application start).
        /// </summary>
        public bool IsInitial { get; }

        public ControllerEventArgs(ControllerInfo controller, ControllerEventType eventType, bool isInitial = false)
        {
            Controller = controller;
            EventType = eventType;
            IsInitial = isInitial;
        }
    }

    public enum ControllerEventType
    {
        Connected,
        Disconnected,
        BatteryChanged,
        ChargingStateChanged,
        PropertiesChanged
    }

    public static class ControllerTypeExtensions
    {
        public static string GetDisplayName(this ControllerType type)
        {
            return type switch
            {
                ControllerType.XboxOne => "Xbox One Controller",
                ControllerType.XboxOneS => "Xbox One S Controller",
                ControllerType.XboxOneElite => "Xbox One Elite Controller",
                ControllerType.XboxEliteSeries2 => "Xbox Elite Series 2",
                ControllerType.XboxSeriesX => "Xbox Series X Controller",
                ControllerType.XboxSeriesS => "Xbox Series S Controller",
                ControllerType.Xbox360 => "Xbox 360 Controller",
                ControllerType.XboxAdaptive => "Xbox Adaptive Controller",
                ControllerType.PlayStation4 => "DualShock 4",
                ControllerType.PlayStation5 => "DualSense",
                ControllerType.NintendoSwitchPro => "Nintendo Switch Pro Controller",
                ControllerType.NintendoSwitchJoyConLeft => "Joy-Con (L)",
                ControllerType.NintendoSwitchJoyConRight => "Joy-Con (R)",
                ControllerType.SteamController => "Steam Controller",
                ControllerType.SteamDeck => "Steam Deck",
                ControllerType.GenericXInput => "XInput Controller",
                ControllerType.GenericHID => "HID Gamepad",
                _ => "Unknown Controller"
            };
        }

        public static string GetIconPath(this ControllerType type)
        {
            var basePath = "Assets/Xbox Series";
            return type switch
            {
                ControllerType.XboxOne or ControllerType.XboxOneS or ControllerType.XboxOneElite or ControllerType.XboxEliteSeries2 or ControllerType.XboxSeriesX or ControllerType.XboxSeriesS => "Assets/Xbox Series/Controller.svg",
                ControllerType.PlayStation4 => "Assets/PlayStation Series/Controller.svg",
                ControllerType.PlayStation5 => "Assets/PlayStation Series/Controller.svg",
                ControllerType.NintendoSwitchPro => "Assets/Nintendo Switch/Controller.svg",
                ControllerType.NintendoSwitchJoyConLeft => "Assets/Nintendo Switch/Joy-Con (L).svg",
                ControllerType.NintendoSwitchJoyConRight => "Assets/Nintendo Switch/Joy-Con (R).svg",
                ControllerType.SteamController => "Assets/Steam Controller/Controller.svg",
                ControllerType.SteamDeck => "Assets/Steam Deck/Controller.svg",
                _ => "Assets/Generic/Controller.svg"
            };
        }

        public static bool IsXbox(this ControllerType type)
        {
            return type >= ControllerType.XboxOne && type <= ControllerType.XboxAdaptive;
        }

        public static bool IsPlayStation(this ControllerType type)
        {
            return type == ControllerType.PlayStation4 || type == ControllerType.PlayStation5;
        }

        public static bool IsNintendo(this ControllerType type)
        {
            return type >= ControllerType.NintendoSwitchPro && type <= ControllerType.NintendoSwitchJoyConRight;
        }
    }

    public static class KnownControllerIds
    {
        // Xbox Controllers
        public static readonly Dictionary<(ushort VID, ushort PID), ControllerType> XboxControllers = new()
        {
            // Xbox One
            { (0x045E, 0x02DD), ControllerType.XboxOne },
            { (0x045E, 0x02DE), ControllerType.XboxOne },
            // Xbox One S
            { (0x045E, 0x02E0), ControllerType.XboxOneS },
            { (0x045E, 0x02E1), ControllerType.XboxOneS },
            { (0x045E, 0x02E3), ControllerType.XboxOneS },
            { (0x045E, 0x02EA), ControllerType.XboxOneS },
            { (0x045E, 0x02FD), ControllerType.XboxOneS },
            // Xbox One Elite
            { (0x045E, 0x02E4), ControllerType.XboxOneElite },
            { (0x045E, 0x02E5), ControllerType.XboxOneElite },
            // Xbox Elite Series 2
            { (0x045E, 0x0B00), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B01), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B02), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B03), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B04), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B05), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B06), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B07), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B08), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B09), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B0A), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B0B), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B0C), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B0D), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B0E), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B0F), ControllerType.XboxEliteSeries2 },
            // Xbox Elite Series 2 over Bluetooth LE reports PID 0x0B22
            // (Bluetooth Classic reports 0x0B05, USB reports 0x0B00).
            // Reference: xpadneo quirks -- "only the XBE2 controller identifies
            // with PID 0x0B22 while the other models identify with PID 0x0B13".
            { (0x045E, 0x0B20), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B21), ControllerType.XboxEliteSeries2 },
            { (0x045E, 0x0B22), ControllerType.XboxEliteSeries2 },
            // Xbox Series X|S
            { (0x045E, 0x0B12), ControllerType.XboxSeriesX },
            { (0x045E, 0x0B13), ControllerType.XboxSeriesX },
            { (0x045E, 0x0B14), ControllerType.XboxSeriesS },
            { (0x045E, 0x0B15), ControllerType.XboxSeriesS },
            { (0x045E, 0x0B16), ControllerType.XboxSeriesX },
            { (0x045E, 0x0B17), ControllerType.XboxSeriesX },
            { (0x045E, 0x0B18), ControllerType.XboxSeriesS },
            { (0x045E, 0x0B19), ControllerType.XboxSeriesS },
            // Xbox 360
            { (0x045E, 0x028E), ControllerType.Xbox360 },
            { (0x045E, 0x028F), ControllerType.Xbox360 },
            { (0x045E, 0x0291), ControllerType.Xbox360 },
            { (0x045E, 0x0719), ControllerType.Xbox360 },
            // Xbox Adaptive
            { (0x045E, 0x0B1A), ControllerType.XboxAdaptive },
            { (0x045E, 0x0B1B), ControllerType.XboxAdaptive },
        };

        // PlayStation Controllers
        public static readonly Dictionary<(ushort VID, ushort PID), ControllerType> PlayStationControllers = new()
        {
            { (0x054C, 0x05C4), ControllerType.PlayStation4 }, // DualShock 4
            { (0x054C, 0x09CC), ControllerType.PlayStation4 }, // DualShock 4 v2
            { (0x054C, 0x0CE6), ControllerType.PlayStation5 }, // DualSense
            { (0x054C, 0x0DF2), ControllerType.PlayStation5 }, // DualSense Edge
        };

        // Nintendo Controllers
        public static readonly Dictionary<(ushort VID, ushort PID), ControllerType> NintendoControllers = new()
        {
            { (0x057E, 0x2009), ControllerType.NintendoSwitchPro }, // Pro Controller
            { (0x057E, 0x200E), ControllerType.NintendoSwitchJoyConLeft }, // Joy-Con Left
            { (0x057E, 0x200F), ControllerType.NintendoSwitchJoyConRight }, // Joy-Con Right
        };

        // Steam Controllers
        public static readonly Dictionary<(ushort VID, ushort PID), ControllerType> SteamControllers = new()
        {
            { (0x28DE, 0x1102), ControllerType.SteamController },
            { (0x28DE, 0x1142), ControllerType.SteamDeck },
        };

        public static ControllerType IdentifyController(ushort vid, ushort pid)
        {
            if (TryIdentifyExact(vid, pid, out var exact))
                return exact;

            // Check for generic XInput (VID 0x045E is Microsoft)
            if (vid == 0x045E) return ControllerType.GenericXInput;

            return ControllerType.Unknown;
        }

        /// <summary>
        /// Identifies a controller from the exact VID/PID tables only, without the
        /// generic "any Microsoft VID is an Xbox controller" fallback. The fallback is
        /// what made Windows' internal BUTTONCONVERTER collections (VID 045E / PID 0000)
        /// look like Xbox controllers.
        /// </summary>
        public static bool TryIdentifyExact(ushort vid, ushort pid, out ControllerType type)
        {
            var key = (vid, pid);

            if (XboxControllers.TryGetValue(key, out type)) return true;
            if (PlayStationControllers.TryGetValue(key, out type)) return true;
            if (NintendoControllers.TryGetValue(key, out type)) return true;
            if (SteamControllers.TryGetValue(key, out type)) return true;

            type = ControllerType.Unknown;
            return false;
        }

        /// <summary>
        /// Vendor IDs that make game controllers. Used only together with a
        /// descriptor/name that actually says "gamepad/controller", never on its own.
        /// </summary>
        public static bool IsGamepadVendor(ushort vid)
        {
            return vid == 0x045E // Microsoft (Xbox)
                || vid == 0x054C // Sony
                || vid == 0x057E // Nintendo
                || vid == 0x28DE // Valve (Steam)
                || vid == 0x0F0D // Hori
                || vid == 0x0E6F // PDP
                || vid == 0x24C6 // PowerA
                || vid == 0x2DC8 // 8BitDo
                || vid == 0x1949; // Amazon (Luna)
        }
    }
}
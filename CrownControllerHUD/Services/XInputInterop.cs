using System;
using System.Runtime.InteropServices;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Minimal XInput probe used for diagnostics only. XInput gives us the occupied
    /// slot numbers and the wired/wireless capability flag, and it is the canonical way
    /// to tell how many *XInput-visible* controllers Windows sees.
    ///
    /// Note: Xbox controllers connected over Bluetooth LE are NOT exposed through XInput
    /// (XInputGetState returns ERROR_DEVICE_NOT_CONNECTED), which is why XInput alone is
    /// not used as a detection source.
    /// </summary>
    public static class XInputInterop
    {
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_DEVICE_NOT_CONNECTED = 1167;
        private const ushort XINPUT_CAPS_WIRELESS = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_CAPABILITIES
        {
            public byte Type;
            public byte SubType;
            public ushort Flags;
            public XINPUT_GAMEPAD Gamepad;
            public XINPUT_VIBRATION Vibration;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
        private static extern uint XInputGetCapabilities(uint dwUserIndex, uint dwFlags, out XINPUT_CAPABILITIES pCapabilities);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

        // XINPUT_BATTERY_TYPE_*
        public const byte BATTERY_TYPE_DISCONNECTED = 0x00;
        public const byte BATTERY_TYPE_WIRED = 0x01;
        public const byte BATTERY_TYPE_ALKALINE = 0x02;
        public const byte BATTERY_TYPE_NIMH = 0x03;
        public const byte BATTERY_TYPE_UNKNOWN = 0xFF;

        // XINPUT_BATTERY_LEVEL_*
        public const byte BATTERY_LEVEL_EMPTY = 0x00;
        public const byte BATTERY_LEVEL_LOW = 0x01;
        public const byte BATTERY_LEVEL_MEDIUM = 0x02;
        public const byte BATTERY_LEVEL_FULL = 0x03;

        private const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;

        /// <summary>
        /// Result of XInputGetBatteryInformation.
        ///
        /// XInput only ever reports a CATEGORICAL level, never a percentage, and
        /// BATTERY_TYPE_DISCONNECTED with level EMPTY is what it returns for a device it
        /// cannot measure at all (this is what an Xbox Elite Series 2 over Bluetooth
        /// returns). <see cref="Usable"/> is false in that case so the caller can never
        /// mistake "no data" for "empty battery".
        /// </summary>
        public sealed class BatteryInfo
        {
            public int Slot { get; init; }
            public byte Type { get; init; }
            public byte Level { get; init; }
            public string Source { get; init; } = string.Empty;

            /// <summary>True only when XInput genuinely measured a battery.</summary>
            public bool Usable => Type == BATTERY_TYPE_ALKALINE || Type == BATTERY_TYPE_NIMH;

            /// <summary>True when the device is powered over the cable (no battery reading).</summary>
            public bool Wired => Type == BATTERY_TYPE_WIRED || Type == BATTERY_TYPE_DISCONNECTED;

            public override string ToString() =>
                $"slot {Slot} type=0x{Type:X2} level=0x{Level:X2} usable={Usable}";
        }

        /// <summary>
        /// Reads XInput battery information for a slot. Never throws. Callers must check
        /// <see cref="BatteryInfo.Usable"/> before treating the result as telemetry.
        /// </summary>
        public static BatteryInfo? GetBatteryInformation(int slot)
        {
            if (slot < 0 || slot > 3)
                return null;

            try
            {
                var info = default(XINPUT_BATTERY_INFORMATION);
                var result = XInputGetBatteryInformation((uint)slot, BATTERY_DEVTYPE_GAMEPAD, out info);

                if (result != ERROR_SUCCESS)
                    return null;

                return new BatteryInfo
                {
                    Slot = slot,
                    Type = info.BatteryType,
                    Level = info.BatteryLevel,
                    Source = "XInputGetBatteryInformation"
                };
            }
            catch
            {
                // xinput1_4.dll unavailable - no XInput battery telemetry.
                return null;
            }
        }

        public sealed class SlotInfo
        {
            public int Slot { get; init; }
            public bool Connected { get; init; }
            public uint ResultCode { get; init; }
            public byte Type { get; init; }
            public byte SubType { get; init; }
            public ushort Flags { get; init; }
            public bool Wireless => (Flags & XINPUT_CAPS_WIRELESS) != 0;

            public override string ToString()
            {
                if (!Connected)
                    return $"slot {Slot}: empty (0x{ResultCode:X})";
                return $"slot {Slot}: connected type=0x{Type:X2} sub=0x{SubType:X2} flags=0x{Flags:X2}{(Wireless ? " [wireless]" : " [wired]")}";
            }
        }

        /// <summary>Returns the state of XInput slots 0..3. Never throws.</summary>
        public static SlotInfo[] EnumerateSlots()
        {
            var slots = new SlotInfo[4];

            for (int slot = 0; slot < 4; slot++)
            {
                uint stateResult = uint.MaxValue;
                uint capsResult = uint.MaxValue;
                var caps = default(XINPUT_CAPABILITIES);

                try
                {
                    var state = default(XINPUT_STATE);
                    stateResult = XInputGetState((uint)slot, out state);
                    if (stateResult == ERROR_SUCCESS)
                        capsResult = XInputGetCapabilities((uint)slot, 0, out caps);
                }
                catch
                {
                    // xinput1_4.dll missing: treat every slot as unavailable.
                    slots[slot] = new SlotInfo { Slot = slot, Connected = false, ResultCode = ERROR_DEVICE_NOT_CONNECTED };
                    continue;
                }

                slots[slot] = new SlotInfo
                {
                    Slot = slot,
                    Connected = stateResult == ERROR_SUCCESS,
                    ResultCode = stateResult,
                    Type = caps.Type,
                    SubType = caps.SubType,
                    Flags = caps.Flags
                };
            }

            return slots;
        }
    }
}

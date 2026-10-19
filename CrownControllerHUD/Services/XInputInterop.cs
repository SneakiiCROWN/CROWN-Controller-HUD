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

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
        private static extern uint XInputGetCapabilities(uint dwUserIndex, uint dwFlags, out XINPUT_CAPABILITIES pCapabilities);

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

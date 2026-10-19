using System;
using System.Text;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Helpers for extracting a canonical Bluetooth address so the same physical
    /// controller can be correlated between the PnP device tree, HID interface paths
    /// and WinRT device enumeration (which all format the address differently).
    ///
    /// The canonical form is 12 uppercase hex digits, e.g. "3CFA061915B2".
    /// </summary>
    public static class DeviceIdentity
    {
        /// <summary>
        /// Normalizes a MAC-like string ("3c:fa:06:19:15:b2", "3CFA061915B2",
        /// "3c-fa-06-19-15-b2") into 12 uppercase hex digits, or null if it does not
        /// contain exactly one address worth of hex digits.
        /// </summary>
        public static string? NormalizeAddress(string? macLike)
        {
            if (string.IsNullOrWhiteSpace(macLike))
                return null;

            var hex = new StringBuilder(12);
            foreach (var c in macLike!)
            {
                if (Uri.IsHexDigit(c))
                {
                    hex.Append(char.ToUpperInvariant(c));
                    if (hex.Length > 12)
                        return null; // more than one address worth of hex: not a plain address
                }
                else if (c != ':' && c != '-' && c != ' ')
                {
                    // Any other separator/character means this is not a plain MAC string.
                    if (hex.Length == 0)
                        continue;
                    return hex.Length == 12 ? hex.ToString() : null;
                }
            }

            return hex.Length == 12 ? hex.ToString() : null;
        }

        /// <summary>
        /// Extracts the Bluetooth address embedded in a Windows Plug-and-Play instance
        /// id. Uses the *last* contiguous run of exactly 12 hex digits, because a
        /// Bluetooth instance id embeds a 16/32-bit service GUID beforehand (whose last
        /// group, e.g. "00805F9B34FB", is also 12 hex digits) and the device MAC comes
        /// last.
        /// </summary>
        public static string? FromPnpInstanceId(string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return null;

            int start = -1;
            int length = 0;
            string? last = null;

            for (int i = 0; i <= instanceId!.Length; i++)
            {
                bool hex = i < instanceId.Length && Uri.IsHexDigit(instanceId[i]);
                if (hex)
                {
                    if (length == 0) start = i;
                    length++;
                }
                else
                {
                    if (length == 12)
                        last = instanceId.Substring(start, 12).ToUpperInvariant();
                    length = 0;
                }
            }

            return last;
        }

        /// <summary>
        /// Extracts the device Bluetooth address from a WinRT association-endpoint id,
        /// e.g. "BluetoothLE#BluetoothLE84:5c:f3:f6:22:ef-3c:fa:06:19:15:b2" (local
        /// adapter address first, remote device address last).
        /// </summary>
        public static string? FromAssociationEndpointId(string? endpointId)
        {
            if (string.IsNullOrWhiteSpace(endpointId))
                return null;

            var id = endpointId!;

            // The remote device address follows the last '-' (the local adapter address
            // comes first and is separated by the same character).
            int dash = id.LastIndexOf('-');
            if (dash >= 0 && dash < id.Length - 1)
            {
                var tail = id.Substring(dash + 1);
                var normalized = NormalizeAddress(tail);
                if (normalized != null)
                    return normalized;
            }

            return NormalizeAddress(StripNonAddressPrefix(id));
        }

        /// <summary>Formats a WinRT <c>BluetoothLEDevice.BluetoothAddress</c> (ulong) as 12 hex digits.</summary>
        public static string FromBluetoothAddress(ulong address)
            => address.ToString("X12");

        private static string StripNonAddressPrefix(string text)
        {
            // Keep only the trailing "mac-ish" tail so NormalizeAddress has a chance.
            int lastColon = text.LastIndexOf(':');
            if (lastColon < 0)
                return text;

            // Take the last 18 characters ("xx:xx:xx:xx:xx:xx").
            int start = Math.Max(0, lastColon - 14);
            return text.Substring(start);
        }
    }
}

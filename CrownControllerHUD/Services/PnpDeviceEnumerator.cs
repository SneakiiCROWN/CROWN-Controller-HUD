using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// A single present Plug-and-Play device node with the identity data we need to
    /// correlate controllers reported by different providers.
    /// </summary>
    public sealed class PnpDeviceRecord
    {
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>Bus enumerator, e.g. "HID", "USB", "BTHLEDEVICE", "BTHLE", "BTHENUM".</summary>
        public string Enumerator
        {
            get
            {
                var i = InstanceId.IndexOf('\\');
                return i > 0 ? InstanceId.Substring(0, i).ToUpperInvariant() : InstanceId.ToUpperInvariant();
            }
        }

        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public bool HasVidPid { get; set; }

        public string? ContainerIdRaw { get; set; }
        public Guid ContainerId { get; set; }

        public string? DeviceDescription { get; set; }
        public string? FriendlyName { get; set; }
        public string? Service { get; set; }
        public string? ClassGuid { get; set; }

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName! :
            !string.IsNullOrWhiteSpace(DeviceDescription) ? DeviceDescription! : InstanceId;

        /// <summary>Lower-cased blob of everything textual, used for keyword matching.</summary>
        public string SearchText =>
            $"{FriendlyName} {DeviceDescription} {InstanceId}".ToLowerInvariant();
    }

    /// <summary>
    /// Enumerates present Windows device nodes through SetupAPI and reads the identity
    /// properties we need (container id, VID/PID, description) from the device registry.
    /// This is the only provider that can see Bluetooth LE HID controllers, which are not
    /// exposed as classic HID collections to HidSharp and are not visible to XInput.
    /// </summary>
    public static class PnpDeviceEnumerator
    {
        // The "default"/"none" container Windows assigns to devnodes that have no
        // physical parent. It must never be used to correlate devices.
        private static readonly Guid NullContainerId = new Guid("00000000-0000-0000-ffff-ffffffffffff");

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        /// <summary>
        /// Returns the device-instance IDs of every currently present node on the given bus
        /// enumerator (pass null for all buses).
        /// </summary>
        public static IReadOnlyList<string> EnumeratePresentInstanceIds(string? enumerator)
        {
            var result = new List<string>();

            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                return result;

            try
            {
                int size = Marshal.SizeOf<SP_DEVINFO_DATA>();
                for (uint index = 0; ; index++)
                {
                    var data = new SP_DEVINFO_DATA { cbSize = (uint)size };
                    if (!SetupDiEnumDeviceInfo(set, index, ref data))
                        break;

                    var sb = new StringBuilder(1024);
                    if (SetupDiGetDeviceInstanceId(set, ref data, sb, (uint)sb.Capacity, out _))
                        result.Add(sb.ToString());
                }
            }
            catch
            {
                // Enumeration is best-effort; never let a device error kill detection.
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return result;
        }

        /// <summary>Reads identity properties for one present device node.</summary>
        public static PnpDeviceRecord Describe(string instanceId)
        {
            var record = new PnpDeviceRecord { InstanceId = instanceId };

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
                if (key != null)
                {
                    record.ContainerIdRaw = key.GetValue("ContainerID") as string;
                    record.DeviceDescription = CleanDisplayValue(key.GetValue("DeviceDesc") as string);
                    record.FriendlyName = CleanDisplayValue(key.GetValue("FriendlyName") as string);
                    record.Service = key.GetValue("Service") as string;
                    record.ClassGuid = key.GetValue("ClassGUID") as string;
                }
            }
            catch
            {
                // Missing/unreadable keys just mean less metadata.
            }

            if (!string.IsNullOrWhiteSpace(record.ContainerIdRaw) &&
                Guid.TryParse(record.ContainerIdRaw, out var container))
            {
                record.ContainerId = container;
            }

            var (ok, vid, pid) = ParseVidPid(instanceId);
            record.HasVidPid = ok;
            record.VendorId = vid;
            record.ProductId = pid;

            return record;
        }

        /// <summary>
        /// True when the container id actually identifies a physical device and is safe
        /// to use for correlation.
        /// </summary>
        public static bool IsUsableContainer(string? containerIdRaw)
        {
            if (string.IsNullOrWhiteSpace(containerIdRaw))
                return false;

            if (!Guid.TryParse(containerIdRaw, out var guid))
                return false;

            return guid != Guid.Empty && guid != NullContainerId;
        }

        public static bool IsUsableContainer(Guid containerId)
            => containerId != Guid.Empty && containerId != NullContainerId;

        /// <summary>
        /// Converts a device *interface* path (what HidSharp and WinRT hand out) into the
        /// device *instance* id (what the registry and SetupAPI use).
        /// </summary>
        public static string? InterfacePathToInstanceId(string? devicePath)
        {
            if (string.IsNullOrWhiteSpace(devicePath))
                return null;

            var path = devicePath!;

            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                path = path.Substring(4);
            else if (path.StartsWith(@"\??\", StringComparison.Ordinal))
                path = path.Substring(4);

            // The interface GUID is the LAST "#{...}" segment. Bluetooth HID paths contain
            // an earlier "#{" (e.g. "hid#{00001812-...}&dev&vid_..."), so we must search
            // from the end or the whole instance id collapses to just "hid".
            int interfaceGuid = path.LastIndexOf("#{", StringComparison.Ordinal);
            if (interfaceGuid < 0)
            {
                int brace = path.LastIndexOf('{');
                interfaceGuid = brace > 0 ? brace - 1 : -1;
            }
            if (interfaceGuid > 0)
                path = path.Substring(0, interfaceGuid);

            path = path.Replace('#', '\\').TrimEnd('\\');
            return path.ToUpperInvariant();
        }

        /// <summary>Reads the container id for a device interface path, if available.</summary>
        public static Guid GetContainerIdForInterfacePath(string? devicePath)
        {
            var instanceId = InterfacePathToInstanceId(devicePath);
            if (instanceId == null)
                return Guid.Empty;

            return Describe(instanceId).ContainerId;
        }

        /// <summary>
        /// Extracts VID/PID from a device instance id. Handles both the USB style
        /// (VID_045E&amp;PID_0B00) and the Bluetooth style (VID&amp;02045E_PID&amp;0B22,
        /// where the leading byte is a length prefix).
        /// </summary>
        public static (bool ok, ushort vid, ushort pid) ParseVidPid(string instanceId)
        {
            var vid = ParseHexAfter(instanceId, "VID&") ?? ParseHexAfter(instanceId, "VID_");
            var pid = ParseHexAfter(instanceId, "PID&") ?? ParseHexAfter(instanceId, "PID_");

            if (vid.HasValue && pid.HasValue)
                return (true, vid.Value, pid.Value);

            return (false, 0, 0);
        }

        private static ushort? ParseHexAfter(string text, string marker)
        {
            int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            int start = index + marker.Length;
            int end = start;
            while (end < text.Length && Uri.IsHexDigit(text[end]))
                end++;

            int length = end - start;
            if (length < 4)
                return null;

            // The value is the last 4 hex digits (a longer run carries a length prefix).
            var hex = text.Substring(end - 4, 4);
            return ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Device descriptions are stored as "@some.inf,%key%;Human readable text".
        /// Returns just the human-readable part.
        /// </summary>
        private static string? CleanDisplayValue(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var value = raw!;
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                int semicolon = value.LastIndexOf(';');
                if (semicolon >= 0 && semicolon < value.Length - 1)
                    value = value.Substring(semicolon + 1);
            }

            value = value.Trim();
            return value.Length == 0 ? null : value;
        }
    }
}

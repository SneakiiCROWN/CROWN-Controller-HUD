using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Minimal Win32 interop used by the custom CROWN notification renderer.
    ///
    /// Placement is done in *physical* pixels via SetWindowPos so it is correct on
    /// mixed-DPI desktops regardless of how WPF maps device-independent units for
    /// each monitor. The process is PerMonitorV2 aware (see app.manifest), so raw
    /// screen coordinates from GetMonitorInfo are exactly what the window manager
    /// expects.
    /// </summary>
    internal static class NativeWindowMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_EFFECTIVE_DPI = 0;
        public const uint MONITORINFOF_PRIMARY = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        /// <summary>A monitor's usable desktop area in physical pixels.</summary>
        public sealed class MonitorWorkArea
        {
            public IntPtr Handle { get; init; }
            public RECT Work { get; init; }
            public uint Dpi { get; init; } = 96;
            public bool IsPrimary { get; init; }

            /// <summary>Converts a device-independent value (96 dpi baseline) to physical pixels.</summary>
            public int Scale(double dip) => (int)Math.Round(dip * (Dpi / 96.0));
        }

        /// <summary>Enumerates the attached monitors. Never throws; returns an empty list on failure.</summary>
        public static IReadOnlyList<MonitorWorkArea> GetMonitors()
        {
            var result = new List<MonitorWorkArea>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT _, IntPtr _) =>
                {
                    var info = new MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                        szDevice = string.Empty
                    };

                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        result.Add(new MonitorWorkArea
                        {
                            Handle = hMonitor,
                            Work = info.rcWork,
                            Dpi = TryGetDpi(hMonitor),
                            IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                        });
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // Fall through to the (possibly empty) result list.
            }

            return result;
        }

        private static uint TryGetDpi(IntPtr hMonitor)
        {
            try
            {
                if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                    return dpiX;
            }
            catch
            {
                // shcore.dll unavailable (pre Windows 8.1) - assume 96 dpi.
            }

            return 96;
        }

        public static IntPtr MonitorHandleFromWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
        {
            rect = default;
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                return GetWindowRect(hwnd, out rect);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Moves a window to absolute desktop coordinates without resizing or activating it.</summary>
        public static bool MoveTo(IntPtr hwnd, int x, int y)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                return SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Re-asserts topmost stacking without moving, resizing or activating the window.</summary>
        public static void ReassertTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            try
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch
            {
                // Ignore - topmost is a best-effort nicety, never a hard requirement.
            }
        }

        /// <summary>
        /// Marks the window as "never activate" and hides it from Alt+Tab / the task
        /// switcher. This is what keeps a notification from stealing focus: clicking
        /// it can never make it the foreground window.
        /// </summary>
        public static void ApplyNoActivateToolWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            try
            {
                var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
            }
            catch
            {
                // Ignore - ShowActivated=false still prevents focus stealing on most systems.
            }
        }
    }
}

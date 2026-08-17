using System.Runtime.InteropServices;
using System.Text;

namespace BeamSplit.Core;

/// <summary>A physical display, as Windows reports it.</summary>
/// <param name="DeviceName">e.g. \\.\DISPLAY2 - stable across restarts, unlike index.</param>
public readonly record struct MonitorInfo(
    string DeviceName, int X, int Y, int Width, int Height, bool Primary)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public string Label => $"{Width}x{Height}" + (Primary ? "  (primary)" : "");
}

public static partial class Native
{
    // --------------------------------------------------------------- memory
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    public static (ulong Total, ulong Available) GetPhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.ullTotalPhys, status.ullAvailPhys)
            : (0, 0);
    }

    // ---------------------------------------------------------------- monitors
    // We enumerate via Win32 rather than WinForms' Screen: it avoids pulling the
    // whole WinForms stack into a WPF app (which collides on Application,
    // RadioButton, etc.), and it gives us DeviceName directly.
    //
    // IMPORTANT: enumeration ORDER IS NOT STABLE. During development the primary
    // display moved between index 0 and index 2 in the same session, so nothing
    // may key off the index - always use DeviceName.

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    private const uint MONITORINFOF_PRIMARY = 1;

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(
                    mi.szDevice, r.Left, r.Top,
                    r.Right - r.Left, r.Bottom - r.Top,
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo GetPrimaryMonitor()
    {
        var all = GetMonitors();
        foreach (var m in all) if (m.Primary) return m;
        return all.Count > 0 ? all[0] : new MonitorInfo("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true);
    }

    // ----------------------------------------------------------------- windows
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr hWnd, int index, int value);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? cls, string? title);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int command);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    /// <summary>
    /// BeamNG sometimes creates a visible console companion whose caption is the full
    /// Bin64 executable path. Hide only that auxiliary window for the supplied process;
    /// the actual "BeamNG.drive - ..." render window remains untouched.
    /// </summary>
    public static int HideBeamNgConsoleWindows(uint processId)
    {
        var hidden = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != processId) return true;
            var buffer = new StringBuilder(512);
            if (GetWindowText(hwnd, buffer, buffer.Capacity) <= 0) return true;
            var title = buffer.ToString();
            var auxiliary = title.EndsWith("BeamNG.drive.x64.exe", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains(@"\Bin64\BeamNG.drive", StringComparison.OrdinalIgnoreCase);
            if (!auxiliary || title.StartsWith("BeamNG.drive -", StringComparison.OrdinalIgnoreCase)) return true;
            if (ShowWindow(hwnd, SW_HIDE)) hidden++;
            return true;
        }, IntPtr.Zero);
        return hidden;
    }

    public static (int X, int Y, int W, int H)? WindowBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public const int GWL_STYLE = -16;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_OVERLAPPEDWINDOW = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU |
                                           WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_RESTORE = 9;
    public const int SW_HIDE = 0;

    // ------------------------------------------------------------------ xinput
    // Query pad presence directly rather than depending on SharpDX.
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint index, ref XINPUT_STATE state);

    public static bool PadConnected(uint index)
    {
        var s = new XINPUT_STATE();
        try { return XInputGetState(index, ref s) == 0; } catch { return false; }
    }

    /// <summary>True if any button/trigger on that pad is currently pressed (used by Identify).</summary>
    public static bool PadAnyInput(uint index)
    {
        var s = new XINPUT_STATE();
        try
        {
            if (XInputGetState(index, ref s) != 0) return false;
            return s.Gamepad.wButtons != 0 || s.Gamepad.bLeftTrigger > 30 || s.Gamepad.bRightTrigger > 30;
        }
        catch { return false; }
    }
}

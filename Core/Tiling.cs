using System.Diagnostics;

namespace BeamSplit.Core;

/// <summary>Where one player's window goes.</summary>
public readonly record struct Rect(int X, int Y, int W, int H);

public static class Tiling
{
    /// <summary>
    /// The region rectangle for a slot: its monitor, divided by its split mode.
    /// Monitors are looked up by DeviceName - never by index, because enumeration
    /// order is not stable between runs.
    /// </summary>
    public static Rect RegionFor(PlayerSlot slot, IReadOnlyList<MonitorInfo> monitors)
    {
        var mon = monitors.FirstOrDefault(m => m.DeviceName == slot.MonitorDevice);
        if (mon.DeviceName is null or "")
            mon = monitors.FirstOrDefault(m => m.Primary, monitors.Count > 0 ? monitors[0] : default);

        return RegionIn(mon, slot.Split, slot.Region);
    }

    public static Rect RegionIn(MonitorInfo mon, SplitMode split, int region) => split switch
    {
        SplitMode.Full => new Rect(mon.X, mon.Y, mon.Width, mon.Height),

        SplitMode.TwoStacked => new Rect(
            mon.X,
            mon.Y + (region % 2) * (mon.Height / 2),
            mon.Width,
            mon.Height / 2),

        SplitMode.TwoSideBySide => new Rect(
            mon.X + (region % 2) * (mon.Width / 2),
            mon.Y,
            mon.Width / 2,
            mon.Height),

        SplitMode.FourGrid => new Rect(
            mon.X + (region % 2) * (mon.Width / 2),
            mon.Y + (region / 2 % 2) * (mon.Height / 2),
            mon.Width / 2,
            mon.Height / 2),

        _ => new Rect(mon.X, mon.Y, mon.Width, mon.Height)
    };

    /// <summary>How many players a split mode can hold.</summary>
    public static int Capacity(SplitMode m) => m switch
    {
        SplitMode.Full => 1,
        SplitMode.TwoStacked or SplitMode.TwoSideBySide => 2,
        SplitMode.FourGrid => 4,
        _ => 1
    };

    public static void Place(IntPtr hwnd, Rect r, bool borderless)
    {
        if (hwnd == IntPtr.Zero) return;
        if (borderless)
        {
            // strip the title bar and resize frame so tiles sit flush
            var style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
            Native.SetWindowLong(hwnd, Native.GWL_STYLE, style & ~(Native.WS_CAPTION | Native.WS_THICKFRAME));
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.W, r.H, Native.SWP_NOZORDER);
    }

    /// <summary>
    /// Windows sends raw HID input only to the FOCUSED window, and that path ignores the
    /// per-instance controller filtering - so a focused instance answers to EVERY pad.
    /// Parking focus on the shell is what makes one-pad-per-player work at all.
    /// </summary>
    public static bool ParkFocus()
    {
        var shell = Native.FindWindow("Progman", null);
        if (shell == IntPtr.Zero) shell = Native.FindWindow("Shell_TrayWnd", null);
        return shell != IntPtr.Zero && Native.SetForegroundWindow(shell);
    }

    public static bool IsGameFocused()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(fg, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName.Equals("BeamNG.drive.x64", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Game windows currently on screen, oldest first (launch order).</summary>
    public static List<Process> GameWindows() =>
        Process.GetProcessesByName("BeamNG.drive.x64")
               .Where(p => p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.StartsWith("BeamNG", StringComparison.OrdinalIgnoreCase))
               .OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } })
               .ToList();
}

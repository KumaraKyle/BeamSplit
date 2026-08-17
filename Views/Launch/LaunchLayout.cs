using BeamSplit.Core;

namespace BeamSplit.Views.Launch;

/// <summary>A rectangle in 0..1 space, relative to the bounding box of the hosting monitors.</summary>
public readonly record struct NormRect(float X, float Y, float W, float H)
{
    public float Cx => X + W * .5f;
    public float Cy => Y + H * .5f;
}

/// <summary>
/// Where the film's panes land. This is the real splitscreen geometry, resolved through the
/// launcher's own <see cref="Tiling.RegionFor"/> so the animation cannot drift from what the
/// game windows actually do — landing a generic 2x2 on someone who configured two full
/// monitors would be worse than saying nothing.
///
/// Deliberately Skia-free: it is pure geometry, and it is the one part of the film that is
/// worth checking against Core.
/// </summary>
public sealed class LaunchLayout
{
    /// <summary>One per player, in slot order.</summary>
    public IReadOnlyList<NormRect> Panes { get; init; } = [];

    /// <summary>Regions of a split that no player occupies — 3 players in a quad grid.</summary>
    public IReadOnlyList<NormRect> Vacant { get; init; } = [];

    /// <summary>Width / height of the desktop area being described.</summary>
    public float Aspect { get; init; } = 16f / 9f;

    public static LaunchLayout Build(int players)
    {
        players = Math.Clamp(players, 1, 4);
        var monitors = SafeMonitors();
        var slots = Slots(players, monitors);

        var rects = new Core.Rect[players];
        for (var i = 0; i < players; i++)
            rects[i] = Tiling.RegionFor(slots[i], monitors);

        // The bounding box is the union of the hosting *monitors*, in true desktop
        // coordinates including the gaps between them. ScreensPage deliberately re-orders
        // displays because it is an assignment UI; here the real left/right relationship is
        // the whole payoff of the resolve beat.
        var hosts = new List<MonitorInfo>();
        foreach (var slot in slots)
        {
            var mon = Host(slot, monitors);
            if (!hosts.Any(m => m.DeviceName == mon.DeviceName)) hosts.Add(mon);
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in hosts)
        {
            left = Math.Min(left, m.X);
            top = Math.Min(top, m.Y);
            right = Math.Max(right, m.Right);
            bottom = Math.Max(bottom, m.Bottom);
        }
        var boxW = Math.Max(1, right - left);
        var boxH = Math.Max(1, bottom - top);

        NormRect Normalise(Core.Rect r) => new(
            (r.X - left) / (float)boxW,
            (r.Y - top) / (float)boxH,
            r.W / (float)boxW,
            r.H / (float)boxH);

        var panes = new List<NormRect>(players);
        foreach (var r in rects) panes.Add(Normalise(r));

        return new LaunchLayout
        {
            Panes = panes,
            Vacant = Vacancies(slots, monitors).Select(Normalise).ToList(),
            Aspect = boxW / (float)boxH
        };
    }

    /// <summary>
    /// Regions the split mode defines but no player fills. Drawn as a dim unlit outline
    /// rather than omitted, so a 3-player quad grid explains itself instead of looking broken.
    /// </summary>
    private static List<Core.Rect> Vacancies(IReadOnlyList<PlayerSlot> slots, IReadOnlyList<MonitorInfo> monitors)
    {
        var vacant = new List<Core.Rect>();
        foreach (var group in slots.GroupBy(s => (Host(s, monitors).DeviceName, s.Split)))
        {
            var mon = Host(group.First(), monitors);
            var taken = group.Select(s => s.Region).ToHashSet();
            for (var region = 0; region < Tiling.Capacity(group.Key.Split); region++)
                if (!taken.Contains(region))
                    vacant.Add(Tiling.RegionIn(mon, group.Key.Split, region));
        }
        return vacant;
    }

    private static MonitorInfo Host(PlayerSlot slot, IReadOnlyList<MonitorInfo> monitors)
    {
        var mon = monitors.FirstOrDefault(m => m.DeviceName == slot.MonitorDevice);
        return mon.DeviceName is null or "" ? Primary(monitors) : mon;
    }

    private static MonitorInfo Primary(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => m.Primary, monitors.Count > 0 ? monitors[0] : Fallback);

    private static readonly MonitorInfo Fallback = new("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true);

    private static List<MonitorInfo> SafeMonitors()
    {
        try
        {
            var found = Native.GetMonitors();
            if (found.Count > 0) return found;
        }
        catch { /* the film is not worth failing a launch over */ }
        return [Fallback];
    }

    /// <summary>
    /// The configured slots, or synthesised ones. The Settings preview can legitimately ask
    /// for a player count the config has no slots for, so it falls through the same
    /// Tiling code path with a synthetic split rather than a second layout implementation.
    /// </summary>
    private static List<PlayerSlot> Slots(int players, IReadOnlyList<MonitorInfo> monitors)
    {
        var configured = AppState.Current.Config.Players;
        if (configured.Count >= players)
            return configured.Take(players).ToList();

        var split = players switch
        {
            1 => SplitMode.Full,
            2 => SplitMode.TwoStacked,
            _ => SplitMode.FourGrid
        };
        var device = Primary(monitors).DeviceName;
        return Enumerable.Range(0, players)
            .Select(i => new PlayerSlot { Index = i, MonitorDevice = device, Split = split, Region = i })
            .ToList();
    }
}

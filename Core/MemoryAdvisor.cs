namespace BeamSplit.Core;

public sealed record MemoryAdvice(string MapName, int Players, long TotalMemoryMb,
    long AvailableMemoryMb, string Reason);

/// <summary>Pre-launch advice for maps whose world assets multiply across processes.</summary>
public static class MemoryAdvisor
{
    private static readonly Dictionary<string, string> HeavyMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["italy"] = "Italy",
        ["utah"] = "Utah",
        ["west_coast_usa"] = "West Coast, USA",
        ["johnson_valley"] = "Johnson Valley"
    };

    public static MemoryAdvice? Evaluate(AppConfig cfg, int players, string? mapPath,
        long totalMemoryMb, long availableMemoryMb)
    {
        if (cfg.LowMemoryGraphics || players < 2 || string.IsNullOrWhiteSpace(mapPath)) return null;
        var id = mapPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(part => !part.Equals("levels", StringComparison.OrdinalIgnoreCase))
            .Skip(1).FirstOrDefault();
        if (id is null || !HeavyMaps.TryGetValue(id, out var name)) return null;

        var single = cfg.SessionEngine == SessionEngine.SingleInstanceExperimental;
        var installedThresholdMb = single ? 12 * 1024L : 20 * 1024L;
        var availableThresholdMb = single ? 3 * 1024L : 6 * 1024L;
        if (totalMemoryMb > installedThresholdMb && availableMemoryMb > availableThresholdMb) return null;

        var reason = totalMemoryMb <= installedThresholdMb
            ? single
                ? "The map is loaded once, but its assets and both simulated vehicles still need memory."
                : "Large-world assets are loaded separately by every BeamNG instance."
            : "Other applications are already using most of the available memory.";
        return new MemoryAdvice(name, players, totalMemoryMb, availableMemoryMb, reason);
    }
}

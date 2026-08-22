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

        const long installedThresholdMb = 20 * 1024;
        const long availableThresholdMb = 6 * 1024;
        if (totalMemoryMb > installedThresholdMb && availableMemoryMb > availableThresholdMb) return null;

        var reason = totalMemoryMb <= installedThresholdMb
            ? "Large-world assets are loaded separately by every BeamNG instance."
            : "Other applications are already using most of the available memory.";
        return new MemoryAdvice(name, players, totalMemoryMb, availableMemoryMb, reason);
    }
}

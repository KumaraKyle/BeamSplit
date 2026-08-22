using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace BeamSplit.Core;

public sealed record BeamMap(string Id, string Title, string ServerPath, byte[]? Thumbnail);

/// <summary>
/// Discovers BeamMP-compatible vanilla maps and their real preview artwork from the
/// installed BeamNG level archives. Nothing is extracted or written into the game.
/// </summary>
public static class MapCatalog
{
    private static readonly string[] SupportedIds =
    [
        "automation_test_track", "derby", "driver_training", "east_coast_usa",
        "gridmap_v2", "hirochi_raceway", "industrial", "italy", "johnson_valley",
        "jungle_rock_island", "small_island", "smallgrid", "utah", "west_coast_usa"
    ];

    private static readonly Dictionary<string, IReadOnlyList<BeamMap>> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static IReadOnlyList<BeamMap> Discover(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.GameRoot)) return Fallback();
        var root = Path.GetFullPath(cfg.GameRoot);
        lock (CacheLock)
            if (Cache.TryGetValue(root, out var cached)) return cached;

        var maps = ReadInstall(root);
        lock (CacheLock) Cache[root] = maps;
        return maps;
    }

    private static IReadOnlyList<BeamMap> ReadInstall(string gameRoot)
    {
        var levels = Path.Combine(gameRoot, "content", "levels");
        if (!Directory.Exists(levels)) return Fallback();
        var supported = SupportedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var translations = ReadTranslations(gameRoot);
        var maps = new List<BeamMap>();

        foreach (var zipPath in Directory.GetFiles(levels, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var info = archive.Entries.FirstOrDefault(entry =>
                {
                    var parts = entry.FullName.Replace('\\', '/').Split('/');
                    return parts.Length == 3 && parts[0].Equals("levels", StringComparison.OrdinalIgnoreCase) &&
                           parts[2].Equals("info.json", StringComparison.OrdinalIgnoreCase) &&
                           supported.Contains(parts[1]);
                });
                if (info is null) continue;

                var id = info.FullName.Replace('\\', '/').Split('/')[1];
                using var reader = new StreamReader(info.Open());
                var json = reader.ReadToEnd();
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                var titleKey = document.RootElement.TryGetProperty("title", out var titleNode)
                    ? titleNode.GetString()
                    : null;
                var title = titleKey is not null && translations.TryGetValue(titleKey, out var translated)
                    ? translated
                    : Humanize(id);

                byte[]? thumbnail = null;
                if (document.RootElement.TryGetProperty("previews", out var previews) &&
                    previews.ValueKind == JsonValueKind.Array && previews.GetArrayLength() > 0)
                {
                    var previewName = previews[0].GetString();
                    var root = $"levels/{id}/";
                    var preview = archive.Entries.FirstOrDefault(entry => previewName is not null &&
                        entry.FullName.Equals(root + previewName, StringComparison.OrdinalIgnoreCase));
                    if (preview is { Length: > 0 and <= 8_388_608 })
                    {
                        using var input = preview.Open();
                        using var output = new MemoryStream((int)preview.Length);
                        input.CopyTo(output);
                        thumbnail = output.ToArray();
                    }
                }

                maps.Add(new BeamMap(id, title, $"/levels/{id.ToLowerInvariant()}/info.json", thumbnail));
            }
            catch { /* one damaged archive must not hide the other installed maps */ }
        }

        return maps.Count == 0
            ? Fallback()
            : maps.OrderBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadTranslations(string gameRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(gameRoot, "locales", "translations", "en-US", "main.translation.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = property.Value.GetString() ?? property.Name;
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<BeamMap> Fallback() => SupportedIds
        .Select(id => new BeamMap(id, Humanize(id), $"/levels/{id}/info.json", null))
        .ToList();

    private static string Humanize(string id) => CultureInfo.InvariantCulture.TextInfo
        .ToTitleCase(id.Replace('_', ' '))
        .Replace("Usa", "USA", StringComparison.Ordinal);
}

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BeamSplit.Core;

/// <summary>
/// Picks the BeamMP client that matches the installed BeamNG.
///
/// This exists because the BeamMP launcher ALWAYS downloads the newest client, and a
/// client built for a newer BeamNG deactivates itself on an older one - you get a
/// connected socket and no Multiplayer button. Patching the version check is not a
/// fix: the newer client's UI is built for the newer game and never renders.
///
/// On the newest BeamNG this simply returns the newest release, so it is correct in
/// both directions.
/// </summary>
public static partial class BeamMpCatalog
{
    private const string ReleasesUrl = "https://api.github.com/repos/BeamMP/BeamMP/releases?per_page=30";
    private const string ServerLatestUrl = "https://api.github.com/repos/BeamMP/BeamMP-Server/releases/latest";

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = [];
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BeamSplit");
        return c;
    }

    /// <summary>Reads compatibleVersion out of a BeamMP client zip (0 if unreadable).</summary>
    public static int ModTargetVersion(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("modScript.lua", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return 0;
            using var sr = new StreamReader(entry.Open());
            var m = CompatRe().Match(sr.ReadToEnd());
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch { return 0; }
    }

    public sealed record MatchResult(string? ZipPath, string? Tag, List<string> Log);

    /// <summary>
    /// Walks releases newest-first and returns the first client targeting <paramref name="gameMajor"/>.
    /// Caches into %LOCALAPPDATA%\BeamSplit\mods so repeat runs are instant.
    /// </summary>
    public static async Task<MatchResult> FindMatchingAsync(int gameMajor, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var lines = new List<string>();
        void Say(string s) { lines.Add(s); log?.Report(s); }

        if (gameMajor <= 0)
        {
            Say("Could not determine the BeamNG version.");
            return new MatchResult(null, null, lines);
        }

        Directory.CreateDirectory(Paths.ModsDir);

        // Only trust caches created after digest verification.
        foreach (var f in Directory.GetFiles(Paths.ModsDir, "*.zip"))
        {
            var sidecar = f + ".sha256";
            if (File.Exists(sidecar) && ModTargetVersion(f) == gameMajor)
            {
                try
                {
                    await DownloadVerifier.VerifyAsync(f, "sha256:" + (await File.ReadAllTextAsync(sidecar, ct)).Trim(), ct);
                    Say($"Using verified cache {Path.GetFileName(f)}");
                    return new MatchResult(f, Path.GetFileNameWithoutExtension(f), lines);
                }
                catch { Say($"Ignoring invalid cache {Path.GetFileName(f)}"); }
            }
        }

        Say($"Looking for a BeamMP client for BeamNG 0.{gameMajor}.x ...");
        using var http = NewClient();
        var releases = await http.GetFromJsonAsync<List<GhRelease>>(ReleasesUrl, ct) ?? [];

        foreach (var rel in releases)
        {
            ct.ThrowIfCancellationRequested();
            var asset = rel.Assets.FirstOrDefault(a => a.Name.Equals("BeamMP.zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null) continue;
            var digest = asset.Digest;
            if (digest is null)
            {
                var checksum = rel.Assets.FirstOrDefault(a =>
                    a.Name.Equals("BeamMP.zip.sha256", StringComparison.OrdinalIgnoreCase));
                if (checksum is not null)
                {
                    try
                    {
                        var content = await http.GetStringAsync(checksum.Url, ct);
                        var hash = Regex.Match(content, @"(?i)\b[0-9a-f]{64}\b");
                        if (hash.Success) digest = "sha256:" + hash.Value;
                    }
                    catch (Exception ex) { Say($"  {rel.Tag}: checksum fetch failed ({ex.Message})"); }
                }
            }

            var tmp = Path.Combine(Path.GetTempPath(), $"BeamMP-{Guid.NewGuid():N}-{rel.Tag}.zip");
            try
            {
                await DownloadVerifier.DownloadAsync(http, asset.Url, tmp, digest, ct);
            }
            catch (Exception ex) { Say($"  {rel.Tag}: rejected ({ex.Message})"); continue; }

            var target = ModTargetVersion(tmp);
            Say($"  {rel.Tag,-9} targets 0.{target}.x");

            if (target == gameMajor)
            {
                var dest = Path.Combine(Paths.ModsDir, $"BeamMP-{rel.Tag}.zip");
                File.Copy(tmp, dest, true);
                await File.WriteAllTextAsync(dest + ".sha256", DownloadVerifier.RequireSha256(digest), ct);
                try { File.Delete(tmp); } catch { }
                Say($"  match: {rel.Tag}");
                return new MatchResult(dest, rel.Tag, lines);
            }
            try { File.Delete(tmp); } catch { }
        }

        Say($"No BeamMP release targets BeamNG 0.{gameMajor}.x yet.");
        return new MatchResult(null, null, lines);
    }

    /// <summary>Downloads the latest BeamMP-Server.exe into %LOCALAPPDATA%\BeamSplit\server.</summary>
    public static async Task<string?> DownloadServerAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Paths.ServerDirDefault);
            using var http = NewClient();
            var rel = await http.GetFromJsonAsync<GhRelease>(ServerLatestUrl, ct);
            var asset = rel?.Assets.FirstOrDefault(a => a.Name.Equals("BeamMP-Server.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null) { log?.Report("No BeamMP-Server.exe in the latest release."); return null; }

            var dest = Path.Combine(Paths.ServerDirDefault, "BeamMP-Server.exe");
            await DownloadVerifier.DownloadAsync(http, asset.Url, dest, asset.Digest, ct);

            log?.Report($"Downloaded BeamMP-Server {rel!.Tag}");
            return Paths.ServerDirDefault;
        }
        catch (Exception ex)
        {
            log?.Report($"Server download failed: {ex.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"compatibleVersion\s*=\s*(\d+)")]
    private static partial Regex CompatRe();
}

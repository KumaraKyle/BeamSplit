using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BeamSplit.Core;

public sealed record UpdateInfo(
    bool Available,
    Version Current,
    Version? Latest,
    string Status,
    string? Notes = null,
    string? ReleaseUrl = null,
    string? AssetName = null,
    string? AssetUrl = null,
    string? Sha256 = null,
    long AssetSize = 0);

/// <summary>Verified, portable updates from the public KumaraKyle/BeamSplit release feed.</summary>
public static class AppUpdater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/KumaraKyle/BeamSplit/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateInfo(false, CurrentVersion, null,
                "No public release feed is available yet. The GitHub repository or its releases must be public for OTA updates.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return new UpdateInfo(false, CurrentVersion, null, $"Release tag '{tag}' is not a valid version.");

        JsonElement? chosen = null;
        var preferred = new[] { "BeamSplit.exe", "BeamSplit-portable.zip", "BeamSplit.zip" };
        foreach (var name in preferred)
        {
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                chosen = asset;
                break;
            }
            if (chosen != null) break;
        }

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        if (chosen == null)
            return new UpdateInfo(false, CurrentVersion, latest,
                latest > CurrentVersion ? "The release exists but has no BeamSplit portable asset." : "BeamSplit is up to date.", notes, page);

        var item = chosen.Value;
        var digest = item.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() : null;
        var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        var available = latest > CurrentVersion;
        var status = !available ? "BeamSplit is up to date."
            : sha == null ? "Update found, but GitHub did not provide its SHA-256 digest; automatic installation is blocked."
            : $"BeamSplit {latest} is ready to download.";

        return new UpdateInfo(available && sha != null, CurrentVersion, latest, status, notes, page,
            item.GetProperty("name").GetString(), item.GetProperty("browser_download_url").GetString(), sha,
            item.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
    }

    public static async Task<string> DownloadAndStageAsync(UpdateInfo info,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!info.Available || info.Latest == null || info.AssetUrl == null || info.AssetName == null || info.Sha256 == null)
            throw new InvalidOperationException("No verified update is available.");

        var root = Path.Combine(Paths.AppData, "updates", info.Latest.ToString());
        Directory.CreateDirectory(root);
        var download = Path.Combine(root, info.AssetName);
        using (var response = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.AssetSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(done * 100d / total);
            }
        }

        await using (var file = File.OpenRead(download))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded update failed SHA-256 verification.");
        }

        progress?.Report(100);
        if (Path.GetExtension(download).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return download;

        var extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);
        ZipFile.ExtractToDirectory(download, extracted, overwriteFiles: true);
        return Directory.GetFiles(extracted, "BeamSplit.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException("The update archive contains no BeamSplit.exe.");
    }

    public static void ApplyAndRestart(string stagedExe, Version version)
    {
        var current = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot locate the running BeamSplit.exe.");
        if (!current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic replacement is available only from the packaged EXE.");

        var folder = Path.GetDirectoryName(current)!;
        var probe = Path.Combine(folder, $".beamsplit-write-{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch { throw new UnauthorizedAccessException("BeamSplit's folder is not writable. Move the portable folder somewhere writable or run as administrator."); }

        var pending = Path.Combine(folder, "BeamSplit.update.exe");
        File.Copy(stagedExe, pending, true);
        var script = Path.Combine(Path.GetTempPath(), $"BeamSplit-update-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        var safeVersion = version.ToString().Replace("&", "").Replace("|", "");
        var content = $"""
@echo off
setlocal
:wait
tasklist /FI "PID eq {pid}" /NH | find "{pid}" >nul
if not errorlevel 1 (
  ping 127.0.0.1 -n 2 >nul
  goto wait
)
copy /Y "{current}" "{current}.previous" >nul
move /Y "{pending}" "{current}" >nul
start "" "{current}" --updated "{safeVersion}"
del "%~f0"
""";
        File.WriteAllText(script, content, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BeamSplit/{CurrentVersion}");
        return client;
    }
}

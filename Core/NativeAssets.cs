using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BeamSplit.Core;

/// <summary>
/// The two native binaries we build ourselves, embedded in the exe and extracted to
/// %LOCALAPPDATA%\BeamSplit\bin on demand.
///
/// They cannot be loaded from inside the single-file bundle: the game loads
/// xinput1_4.dll by path out of its own Bin64, and dilist.exe is a separate process.
/// Extraction is hash-checked so an upgraded BeamSplit replaces stale copies.
///
///   xinput1_4.dll - per-instance XInput proxy, exposes ONE pad as user 0
///   dilist.exe    - lists DirectInput controllers + instance GUIDs (identical pads
///                   share a name, so GUIDs are the only way to tell them apart)
/// </summary>
public static class NativeAssets
{
    private static readonly string[] Assets = ["xinput1_4.dll", "dilist.exe"];
    private static readonly string[] ProtoInputAssets =
    [
        "ProtoInputLoader64.dll", "ProtoInputHooks64.dll", "ProtoInputIJ64.exe",
        "ProtoInputIJP64.dll", "EasyHook.dll", "EasyHook64.dll",
        "EasyHook64Svc.exe", "EasyHookSvc.exe", "ProtoInput-LICENSE.txt"
    ];
    private static readonly string[] NoticeAssets = ["THIRD-PARTY-NOTICES.txt"];
    private const string DevreorderLatestUrl = "https://api.github.com/repos/briankendall/devreorder/releases/latest";
    private const string DevreorderPinnedTag = "v1.0.4";
    private const string DevreorderPinnedAsset = "devreorder_v1.0.4.zip";
    private const string DevreorderPinnedDigest = "sha256:250114168f29f3e02eccca2db004d51a3759d924816c70accaaba2e20798cc10";

    public static string XInputProxy => Path.Combine(Paths.BinDir, "xinput1_4.dll");
    public static string DiList => Path.Combine(Paths.BinDir, "dilist.exe");
    public static string Devreorder => Path.Combine(Paths.BinDir, "dinput8.dll");

    public static bool Ready => File.Exists(XInputProxy) && File.Exists(DiList);

    public static void Extract(IProgress<string>? log = null, bool force = false)
    {
        Directory.CreateDirectory(Paths.BinDir);
        var asm = Assembly.GetExecutingAssembly();

        foreach (var name in Assets)
        {
            var resource = $"BeamSplit.Resources.{name}";
            using var src = asm.GetManifestResourceStream(resource);
            if (src is null)
            {
                log?.Report($"MISSING EMBEDDED RESOURCE: {resource}");
                continue;
            }

            var dest = Path.Combine(Paths.BinDir, name);
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            var bytes = ms.ToArray();

            if (!force && File.Exists(dest) && SameContent(dest, bytes))
            {
                log?.Report($"{name}: up to date");
                continue;
            }

            try
            {
                File.WriteAllBytes(dest, bytes);
                log?.Report($"{name}: extracted ({bytes.Length:N0} bytes)");
            }
            catch (IOException ex)
            {
                // in use by a running instance - not fatal, the deployed copies still work
                log?.Report($"{name}: in use, keeping existing copy ({ex.Message})");
            }
        }

        ExtractGroup(asm, ProtoInputAssets, Paths.ProtoInputDir, log, force);
        ExtractGroup(asm, NoticeAssets, Paths.AppData, log, force);
    }

    private static void ExtractGroup(Assembly asm, IEnumerable<string> names, string folder,
        IProgress<string>? log, bool force)
    {
        Directory.CreateDirectory(folder);
        foreach (var name in names)
        {
            using var src = asm.GetManifestResourceStream($"BeamSplit.Resources.{name}");
            if (src is null) { log?.Report($"Proto Input resource missing: {name}"); continue; }
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            var bytes = ms.ToArray();
            var dest = Path.Combine(folder, name);
            if (!force && File.Exists(dest) && SameContent(dest, bytes)) continue;
            try { File.WriteAllBytes(dest, bytes); }
            catch (IOException ex) { log?.Report($"{name}: in use ({ex.Message})"); }
        }
    }

    public static bool ProtoInputReady => ProtoInputAssets
        .Where(n => !n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        .All(n => File.Exists(Path.Combine(Paths.ProtoInputDir, n)));

    private static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            var a = SHA256.HashData(File.ReadAllBytes(path));
            var b = SHA256.HashData(bytes);
            return a.SequenceEqual(b);
        }
        catch { return false; }
    }

    /// <summary>
    /// devreorder is third-party so we don't ship it. Copy it in if the user happens to
    /// have Nucleus Co-op, which bundles it.
    /// </summary>
    public static bool LocateDevreorder(IProgress<string>? log = null)
    {
        if (File.Exists(Devreorder)) return true;

        string[] candidates =
        [
            @"C:\NucleusCoop\utils\devreorder\x64\dinput8.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NucleusCoop\utils\devreorder\x64\dinput8.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NucleusCoop\utils\devreorder\x64\dinput8.dll")
        ];

        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            Directory.CreateDirectory(Paths.BinDir);
            File.Copy(c, Devreorder, true);
            log?.Report($"devreorder: copied from {c}");
            return true;
        }

        log?.Report("devreorder not found on this PC.");
        return false;
    }

    /// <summary>
    /// Downloads the official devreorder release and extracts only x64/dinput8.dll.
    ///
    /// We keep this app-local: no system32 replacement, no global ProgramData config.
    /// BeamSplit writes one devreorder.ini beside each instance's game exe instead.
    /// </summary>
    public static async Task<bool> DownloadDevreorderAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (File.Exists(Devreorder))
        {
            log?.Report("devreorder: already installed");
            return true;
        }

        string? tmp = null;
        try
        {
            Directory.CreateDirectory(Paths.BinDir);
            using var http = NewClient();
            var rel = await http.GetFromJsonAsync<GhRelease>(DevreorderLatestUrl, ct);
            var asset = rel?.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && a.Name.Contains("devreorder", StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                log?.Report("devreorder: latest GitHub release has no zip asset");
                return false;
            }

            tmp = Path.Combine(Path.GetTempPath(), $"BeamSplit-{Guid.NewGuid():N}-{asset.Name}");
            var tag = rel?.Tag ?? "latest";
            log?.Report($"devreorder: downloading {tag} ...");
            // v1.0.4 predates GitHub asset digests. Its official ZIP was audited and
            // pinned here; any future tag must carry GitHub's own digest or is rejected.
            var digest = asset.Digest ?? (string.Equals(rel?.Tag, DevreorderPinnedTag, StringComparison.OrdinalIgnoreCase)
                && string.Equals(asset.Name, DevreorderPinnedAsset, StringComparison.OrdinalIgnoreCase)
                    ? DevreorderPinnedDigest : null);
            await DownloadVerifier.DownloadAsync(http, asset.Url, tmp, digest, ct);

            using var zip = ZipFile.OpenRead(tmp);
            var dll = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("x64/dinput8.dll", StringComparison.OrdinalIgnoreCase));
            if (dll is null)
            {
                log?.Report("devreorder: release zip did not contain x64/dinput8.dll");
                return false;
            }

            dll.ExtractToFile(Devreorder, overwrite: true);
            log?.Report($"devreorder: installed {tag}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"devreorder download failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (tmp is not null && File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>DirectInput controllers, via dilist.exe. Empty if it can't run.</summary>
    public static List<(int Index, string Guid, string Name)> ListDirectInputPads()
    {
        var result = new List<(int, string, string)>();
        if (!File.Exists(DiList)) return result;

        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiList)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return result;

            var outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            foreach (var line in outText.Split('\n'))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length >= 3 && int.TryParse(parts[0], out var idx))
                    result.Add((idx, parts[1].Trim('{', '}'), parts[2]));
            }
        }
        catch { }
        return result;
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BeamSplit");
        return c;
    }

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
}

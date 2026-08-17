using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BeamSplit.Core;

/// <summary>
/// Reads and writes BeamMP's ServerConfig.toml.
///
/// Two things matter here:
///  * Write it BOM-free. The server's TOML parser rejects a byte-order mark, and a
///    stray BOM is exactly what broke a config earlier in this project's life.
///  * Rewrite lines in place rather than regenerating the file, so comments and any
///    keys we don't model survive untouched.
/// </summary>
public static partial class ServerConfig
{
    public static string? TomlPath(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.ServerDir) ? null : Path.Combine(cfg.ServerDir, "ServerConfig.toml");

    public static string? ExePath(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.ServerDir) ? null : Path.Combine(cfg.ServerDir, "BeamMP-Server.exe");

    public static Dictionary<string, string> Read(AppConfig cfg)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = TomlPath(cfg);
        if (path is null || !File.Exists(path)) return result;

        foreach (var line in File.ReadLines(path))
        {
            var m = KeyValueRe().Match(line);
            if (m.Success) result[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"');
        }
        return result;
    }

    public static bool Write(AppConfig cfg, IDictionary<string, string> values)
    {
        var path = TomlPath(cfg);
        if (path is null || !File.Exists(path)) return false;

        var outLines = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var m = KeyValueRe().Match(line);
            if (m.Success && values.TryGetValue(m.Groups[1].Value, out var v))
            {
                var key = m.Groups[1].Value;
                var isBool = v is "true" or "false";
                var isNum = long.TryParse(v, out _);
                outLines.Add(isBool || isNum ? $"{key} = {v}" : $"{key} = \"{v}\"");
            }
            else outLines.Add(line);
        }

        // UTF8 WITHOUT BOM - required
        File.WriteAllLines(path, outLines, new UTF8Encoding(false));
        return true;
    }

    public static string AuthKey(AppConfig cfg) => Read(cfg).GetValueOrDefault("AuthKey", "");

    public static int Port(AppConfig cfg) =>
        int.TryParse(Read(cfg).GetValueOrDefault("Port", "30814"), out var port) && port is > 0 and <= 65535
            ? port
            : 30814;

    public static bool HasAuthKey(AppConfig cfg) => AuthKey(cfg).Length > 5;

    public static bool IsRunning() => Process.GetProcessesByName("BeamMP-Server").Length > 0;

    /// <summary>
    /// First run generates ServerConfig.toml then exits (it has no AuthKey yet).
    /// We start it briefly just to get that file created.
    /// </summary>
    public static async Task InitializeConfigAsync(AppConfig cfg, IProgress<string>? log = null)
    {
        var toml = TomlPath(cfg);
        var exe = ExePath(cfg);
        if (toml is null || exe is null || File.Exists(toml) || !File.Exists(exe)) return;

        log?.Report("Generating ServerConfig.toml ...");
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = cfg.ServerDir!,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (p is null) return;

        for (var i = 0; i < 15 && !File.Exists(toml); i++) await Task.Delay(1000);
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }

    public static Process? Start(AppConfig cfg)
    {
        var exe = ExePath(cfg);
        if (exe is null || !File.Exists(exe)) return null;
        return Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = cfg.ServerDir!,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static void Stop()
    {
        foreach (var p in Process.GetProcessesByName("BeamMP-Server"))
        {
            try { p.Kill(true); } catch { }
        }
    }

    public static string? LogPath(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.ServerDir) ? null : Path.Combine(cfg.ServerDir, "Server.log");

    [GeneratedRegex(@"^\s*([A-Za-z]+)\s*=\s*(.+?)\s*$")]
    private static partial Regex KeyValueRe();
}

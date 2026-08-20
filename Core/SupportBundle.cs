using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeamSplit.Core;

public static partial class SupportBundle
{
    public static string Create(AppConfig cfg, IEnumerable<LogLine> liveLog)
    {
        var supportDir = Path.Combine(Paths.AppData, "support");
        Directory.CreateDirectory(supportDir);
        var output = Path.Combine(supportDir, $"BeamSplit-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var temp = Path.Combine(Path.GetTempPath(), $"BeamSplit-support-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var diagnostics = new StringBuilder()
                .AppendLine("BeamSplit support bundle")
                .AppendLine($"created: {DateTimeOffset.Now:O}")
                .AppendLine($"version: {Assembly.GetExecutingAssembly().GetName().Version}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"game version: {Detect.GameVersion(cfg) ?? "unknown"}")
                .AppendLine($"mode: {cfg.Mode}; players: {cfg.Players.Count}; base port: {cfg.BasePort}")
                .AppendLine($"game: {cfg.GameRoot}")
                .AppendLine($"instances: {cfg.InstancesDir}")
                .AppendLine($"input: isolate={cfg.Isolate}; proto={cfg.UseProtoInput}; watchdog={cfg.Watchdog}");
            foreach (var m in Native.GetMonitors())
                diagnostics.AppendLine($"monitor: {m.DeviceName} {m.Width}x{m.Height}@{m.X},{m.Y} primary={m.Primary}");
            File.WriteAllText(Path.Combine(temp, "diagnostics.txt"), Redact(diagnostics.ToString(), cfg));

            // AppConfig intentionally contains no AuthKey, but paths still need privacy redaction.
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(temp, "config.redacted.json"), Redact(json, cfg));
            File.WriteAllText(Path.Combine(temp, "live-log.txt"),
                Redact(string.Join(Environment.NewLine, liveLog.TakeLast(1500)), cfg));

            CopyTail(Paths.LogFile, Path.Combine(temp, "beamsplit.log"), cfg, 4000);
            var serverLog = ServerConfig.LogPath(cfg);
            if (serverLog is not null) CopyTail(serverLog, Path.Combine(temp, "server.log"), cfg, 2000);
            var toml = ServerConfig.TomlPath(cfg);
            if (toml is not null && File.Exists(toml))
                File.WriteAllText(Path.Combine(temp, "ServerConfig.redacted.toml"),
                    Redact(File.ReadAllText(toml), cfg));

            if (File.Exists(output)) File.Delete(output);
            ZipFile.CreateFromDirectory(temp, output, CompressionLevel.Optimal, false);
            return output;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static void CopyTail(string source, string destination, AppConfig cfg, int count)
    {
        if (!File.Exists(source)) return;
        try { File.WriteAllLines(destination, File.ReadLines(source).TakeLast(count).Select(x => Redact(x, cfg))); }
        catch (IOException) { }
    }

    public static string Redact(string text, AppConfig? cfg = null)
    {
        var result = AuthKeyRe().Replace(text, "$1\"[REDACTED]\"");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (cfg is not null)
        {
            try
            {
                var auth = ServerConfig.AuthKey(cfg);
                if (auth.Length > 0) result = result.Replace(auth, "[REDACTED]", StringComparison.Ordinal);
            }
            catch { /* regex redaction still applies when BeamMP's file is temporarily locked */ }
        }
        return result;
    }

    [GeneratedRegex("(?im)^(\\s*\\\"?AuthKey\\\"?\\s*[:=]\\s*)\\\"?[^\\r\\n,}\\\"]*\\\"?")]
    private static partial Regex AuthKeyRe();
}

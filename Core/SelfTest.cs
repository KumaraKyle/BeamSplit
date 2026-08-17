using System.IO;
using System.Text;

namespace BeamSplit.Core;

/// <summary>
/// Headless check of the Core layer: BeamSplit.exe --selftest [outfile]
/// Verifies detection, version matching, monitors and pads against the real machine
/// without needing the UI. Kept in the shipping build - it is the fastest way to see
/// why something isn't working on someone else's setup.
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(string outFile)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        try
        {
            W("BeamSplit self-test");
            W("===================");
            W("");

            var cfg = ConfigStore.Load();
            var version = Detect.GameVersion(cfg);
            var major = Detect.GameMajor(version);

            W("-- detection --");
            W($"GameRoot     : {cfg.GameRoot ?? "(not found)"}");
            W($"  valid      : {Detect.IsGameRoot(cfg.GameRoot)}");
            var installs = Detect.FindAllBeamNG();
            W($"  installs   : {installs.Count}");
            foreach (var i in installs) W($"     {(string.Equals(i, cfg.GameRoot, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {i}");
            W($"GameVersion  : {version ?? "(unknown)"}   -> major {major}");
            W($"LauncherExe  : {cfg.LauncherExe ?? "(not found)"}");
            W($"ServerDir    : {cfg.ServerDir ?? "(not found)"}");
            W($"ModZip       : {cfg.ModZip ?? "(not set)"}");
            W($"InstancesDir : {cfg.InstancesDir}");
            W($"AppData      : {Paths.AppData}");
            W("");

            W("-- monitors (order is NOT stable; keyed by DeviceName) --");
            foreach (var m in Native.GetMonitors())
                W($"  {m.DeviceName,-14} {m.Width}x{m.Height} at ({m.X},{m.Y}){(m.Primary ? "  primary" : "")}");
            W("");

            W("-- xinput pads --");
            for (uint i = 0; i < 4; i++)
                W($"  pad {i}: {(Native.PadConnected(i) ? "connected" : "-")}");
            W("");

            W("-- server config --");
            var toml = ServerConfig.TomlPath(cfg);
            W($"  toml       : {toml ?? "(no server dir)"}  exists={(toml != null && File.Exists(toml))}");
            W($"  AuthKey    : {(ServerConfig.HasAuthKey(cfg) ? "present" : "missing")}");
            var sc = ServerConfig.Read(cfg);
            foreach (var k in new[] { "Port", "MaxPlayers", "MaxCars", "Map", "Private" })
                if (sc.TryGetValue(k, out var v)) W($"  {k,-10} : {v}");
            W("");

            W("-- BeamMP client match --");
            var match = await BeamMpCatalog.FindMatchingAsync(major);
            foreach (var l in match.Log) W($"  {l}");
            W($"  RESULT     : {match.Tag ?? "(none)"}  {match.ZipPath ?? ""}");
            if (match.ZipPath != null)
                W($"  targets    : 0.{BeamMpCatalog.ModTargetVersion(match.ZipPath)}.x");
            W("");

            W("-- setup checklist --");
            foreach (var item in SetupStatus.Evaluate(cfg))
                W($"  [{(item.Ok ? "OK" : "--")}] {item.Name,-24} {item.Detail}");
            W("");

            var blockers = SetupStatus.Blockers(cfg);
            W(blockers.Count == 0 ? "No blockers - ready to launch." : $"Blockers: {blockers.Count}");
            foreach (var b in blockers) W($"  - {b}");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("SELF-TEST THREW:");
            sb.AppendLine(ex.ToString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllTextAsync(outFile, sb.ToString());
    }
}

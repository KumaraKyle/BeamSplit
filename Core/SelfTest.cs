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
    public static async Task<bool> RunAsync(string outFile)
    {
        var sb = new StringBuilder();
        var passed = true;
        void W(string s) => sb.AppendLine(s);

        try
        {
            W("BeamSplit self-test");
            W("===================");
            W("");

            W("-- release safety --");
            var safetyProbe = Path.Combine(Path.GetTempPath(), $"BeamSplit-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(safetyProbe);
            try
            {
                var config = Path.Combine(safetyProbe, "config.json");
                var backup = config + ".backup";
                ConfigStore.WriteAtomic(config, backup, "one");
                ConfigStore.WriteAtomic(config, backup, "two");
                var atomic = File.ReadAllText(config) == "two" && File.ReadAllText(backup) == "one";
                W($"  atomic config : {(atomic ? "PASS" : "FAIL")}");
                if (!atomic) throw new InvalidOperationException("atomic config regression");

                var payload = Path.Combine(safetyProbe, "payload.bin");
                await File.WriteAllTextAsync(payload, "BeamSplit");
                var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("BeamSplit"))).ToLowerInvariant();
                await DownloadVerifier.VerifyAsync(payload, digest);
                var redacted = SupportBundle.Redact("AuthKey = \"secret-key\"");
                var safety = !redacted.Contains("secret-key", StringComparison.Ordinal);
                W($"  digest check  : PASS");
                W($"  key redaction : {(safety ? "PASS" : "FAIL")}");
                if (!safety) throw new InvalidOperationException("support redaction regression");
            }
            finally { try { Directory.Delete(safetyProbe, true); } catch { } }
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

            W("-- mod manager --");
            var modProbe = Path.Combine(Path.GetTempPath(), $"BeamSplit-mods-{Guid.NewGuid():N}");
            try
            {
                var source = Path.Combine(modProbe, "source");
                var instances = Path.Combine(modProbe, "instances");
                var server = Path.Combine(modProbe, "server");
                Directory.CreateDirectory(Path.Combine(source, "repo"));
                Directory.CreateDirectory(Path.Combine(source, "multiplayer"));
                Directory.CreateDirectory(Path.Combine(server, "Resources", "Client"));
                await File.WriteAllTextAsync(Path.Combine(source, "repo", "car.zip"), "car");
                await File.WriteAllTextAsync(Path.Combine(source, "multiplayer", "BeamMP.zip"), "client");
                await File.WriteAllTextAsync(Path.Combine(server, "Resources", "Client", "hand-installed.zip"), "keep");
                var probeCfg = new AppConfig
                {
                    InstancesDir = instances,
                    ServerDir = server,
                    ModsSourceDir = source,
                    ModsConfigured = true,
                    UsePlayerMods = true,
                    PlayerModFiles = [Path.Combine("repo", "car.zip")],
                    ServerModFiles = [Path.Combine("repo", "car.zip")]
                };
                ModManager.Apply(probeCfg, 2);
                var mount0 = Path.Combine(Instances.CurrentProfile(probeCfg, 0), "mods", ModManager.PlayerFolderName);
                var mount1 = Path.Combine(Instances.CurrentProfile(probeCfg, 1), "mods", ModManager.PlayerFolderName);
                var local0 = Path.Combine(mount0, "car.zip");
                var local1 = Path.Combine(mount1, "car.zip");
                var serverCopy = Path.Combine(server, "Resources", "Client", "car.zip");
                var ignoredMp = ModManager.Discover(source).All(m => !m.Name.Equals("BeamMP.zip", StringComparison.OrdinalIgnoreCase));
                var installPass = File.Exists(local0) && File.Exists(local1) &&
                                  new DirectoryInfo(mount0).LinkTarget != null && new DirectoryInfo(mount1).LinkTarget != null &&
                                  File.Exists(serverCopy) && ignoredMp &&
                                  File.Exists(Path.Combine(server, "Resources", "Client", "hand-installed.zip"));
                probeCfg.UsePlayerMods = false;
                probeCfg.PlayerModFiles.Clear();
                probeCfg.ServerModFiles.Clear();
                ModManager.Apply(probeCfg, 2);
                var removePass = !File.Exists(local0) && !File.Exists(local1) && !File.Exists(serverCopy) &&
                                 File.Exists(Path.Combine(server, "Resources", "Client", "hand-installed.zip")) &&
                                 File.Exists(Path.Combine(source, "repo", "car.zip"));
                W($"  zero-copy sync: {(installPass && removePass ? "PASS" : "FAIL")} (junction/remove, source preserved)");
                if (!installPass || !removePass) throw new InvalidOperationException("managed mod sync regression");
            }
            finally
            {
                try { if (Directory.Exists(modProbe)) Directory.Delete(modProbe, true); } catch { }
            }
            W("");

            W("-- audio outputs --");
            var audioDevices = AudioDevices.GetRenderDeviceNames();
            W($"  configured  : {cfg.AudioDevice ?? "Windows default"}");
            W($"  mix         : master {cfg.AudioMaster}%, effects {cfg.AudioEffects}%, music {cfg.AudioMusic}%, UI {cfg.AudioUi}%");
            W($"  background  : {(cfg.AudioInBackground ? "plays" : "muted")}");
            W($"  BeamMP mix  : {cfg.AudioMixMode}");
            var sharedMixProbe = new AppConfig { AudioMaster = 75, AudioMixMode = "P0Only" };
            var sharedMixPass = GameSettings.AudioMasterLevel(sharedMixProbe, 0) == 0.75 &&
                                GameSettings.AudioMasterLevel(sharedMixProbe, 1) == 0;
            W($"  P0 fallback : {(sharedMixPass ? "PASS" : "FAIL")} (P0=75%, P1=0%)");
            if (!sharedMixPass) throw new InvalidOperationException("shared-speaker audio mix regression");
            var audioHook = BeamMpAudioIsolation.ResourceAvailable();
            W($"  local hook  : {(audioHook ? "PASS" : "FAIL")}");
            if (!audioHook) throw new InvalidOperationException("remote-vehicle audio hook missing");
            if (!string.IsNullOrWhiteSpace(cfg.ModZip) && File.Exists(cfg.ModZip))
            {
                var probeZip = Path.Combine(Path.GetTempPath(), $"BeamSplit-audio-{Guid.NewGuid():N}.zip");
                try
                {
                    File.Copy(cfg.ModZip, probeZip);
                    BeamMpAudioIsolation.PatchZip(probeZip, true);
                    var installPass = BeamMpAudioIsolation.IsPatched(probeZip);
                    BeamMpAudioIsolation.PatchZip(probeZip, false);
                    var removePass = !BeamMpAudioIsolation.IsPatched(probeZip);
                    W($"  hook zip    : {(installPass && removePass ? "PASS" : "FAIL")} (install/remove)");
                    if (!installPass || !removePass)
                        throw new InvalidOperationException("remote-vehicle audio hook zip regression");
                }
                finally { try { File.Delete(probeZip); } catch { } }
            }
            var autoJoinHook = BeamMpAutoJoin.ResourceAvailable();
            W($"  auto-join    : {(autoJoinHook ? "PASS" : "FAIL")} (guest + 127.0.0.1:{ServerConfig.Port(cfg)})");
            if (!autoJoinHook) throw new InvalidOperationException("BeamMP auto-join hook missing");
            if (!string.IsNullOrWhiteSpace(cfg.ModZip) && File.Exists(cfg.ModZip))
            {
                var probeZip = Path.Combine(Path.GetTempPath(), $"BeamSplit-autojoin-{Guid.NewGuid():N}.zip");
                try
                {
                    File.Copy(cfg.ModZip, probeZip);
                    const int probePort = 30814;
                    BeamMpAutoJoin.PatchZip(probeZip, true, probePort);
                    var installPass = BeamMpAutoJoin.IsPatched(probeZip, probePort);
                    BeamMpAutoJoin.PatchZip(probeZip, false, probePort);
                    var removePass = !BeamMpAutoJoin.IsPatched(probeZip, probePort);
                    W($"  join zip     : {(installPass && removePass ? "PASS" : "FAIL")} (install/remove)");
                    if (!installPass || !removePass)
                        throw new InvalidOperationException("BeamMP auto-join zip regression");
                }
                finally { try { File.Delete(probeZip); } catch { } }
            }
            foreach (var device in audioDevices) W($"  device      : {device}");
            if (AudioDevices.LastError != null) W($"  enumeration : {AudioDevices.LastError}");
            W("");

            W("-- monitors (order is NOT stable; keyed by DeviceName) --");
            foreach (var m in Native.GetMonitors())
                W($"  {m.DeviceName,-14} {m.Width}x{m.Height} at ({m.X},{m.Y}){(m.Primary ? "  primary" : "")}");
            W("");

            W("-- layout regression --");
            var monitor = Native.GetPrimaryMonitor();
            var layoutProbe = new AppConfig
            {
                Players =
                [
                    new PlayerSlot { Index = 0, MonitorDevice = monitor.DeviceName, Split = SplitMode.TwoStacked, Region = 0, Pad = 0 },
                    new PlayerSlot { Index = 1, MonitorDevice = monitor.DeviceName, Split = SplitMode.TwoStacked, Region = 1, Pad = 1 }
                ]
            };
            layoutProbe.EnsureDefaultPlayers(2);
            var layoutPreserved = layoutProbe.Players.Count == 2 &&
                                  layoutProbe.Players.All(p => p.MonitorDevice == monitor.DeviceName &&
                                                               p.Split == SplitMode.TwoStacked) &&
                                  layoutProbe.Players.Select(p => p.Region).SequenceEqual([0, 1]);
            W($"  custom split survives launch validation: {(layoutPreserved ? "PASS" : "FAIL")}");
            if (!layoutPreserved) throw new InvalidOperationException("custom screen layout was reset");
            var normalStyle = Native.WS_VISIBLE | Native.WS_OVERLAPPEDWINDOW;
            var borderlessStyle = Tiling.DesiredStyle(normalStyle, true);
            var restoredStyle = Tiling.DesiredStyle(borderlessStyle, false);
            var windowStylePass = Tiling.StyleValueMatches(borderlessStyle, true) &&
                                  Tiling.StyleValueMatches(restoredStyle, false) &&
                                  !Tiling.StyleValueMatches(normalStyle, true);
            W($"  retile normalizes window style: {(windowStylePass ? "PASS" : "FAIL")}");
            if (!windowStylePass) throw new InvalidOperationException("retile window-style regression");
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
            passed = false;
            sb.AppendLine();
            sb.AppendLine("SELF-TEST THREW:");
            sb.AppendLine(ex.ToString());
        }

        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (outputDir is not null) Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(outFile, sb.ToString());
        return passed;
    }
}

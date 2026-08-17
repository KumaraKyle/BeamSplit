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
            sb.AppendLine();
            sb.AppendLine("SELF-TEST THREW:");
            sb.AppendLine(ex.ToString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllTextAsync(outFile, sb.ToString());
    }
}

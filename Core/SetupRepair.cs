using System.Diagnostics;
using System.IO;

namespace BeamSplit.Core;

/// <summary>Shared setup actions used by both the first-run guide and Play page.</summary>
public static class SetupRepair
{
    public static readonly string[] AutomaticKeys =
        ["game", "launcher", "proxy", "protoinput", "devreorder", "server", "mod"];

    public static async Task RepairAutomaticAsync(AppState state,
        Action<int, string>? progress = null)
    {
        for (var index = 0; index < AutomaticKeys.Length; index++)
        {
            var key = AutomaticKeys[index];
            var item = SetupStatus.Evaluate(state.Config).FirstOrDefault(i => i.Key == key);
            progress?.Invoke(index, item?.Name ?? key);
            if (item is { Ok: false }) await FixAsync(key, state);
        }
        progress?.Invoke(AutomaticKeys.Length, "Setup checked");
        state.Save();
    }

    public static async Task FixAsync(string key, AppState state)
    {
        var cfg = state.Config;
        switch (key)
        {
            case "game":
            {
                var all = Detect.FindAllBeamNG();
                if (all.Count > 0)
                {
                    cfg.GameRoot = all[0];
                    state.Log($"BeamNG: {all[0]}");
                    if (all.Count > 1)
                        state.Log($"Note: {all.Count} installs found. Confirm the right one in Settings.");
                    foreach (var path in all.Skip(1)) state.Log($"  also: {path}");
                }
                else state.Log("BeamNG not found - choose its folder in Settings.");
                break;
            }
            case "launcher":
            {
                var path = Detect.FindLauncher();
                if (path != null) { cfg.LauncherExe = path; state.Log($"BeamMP launcher: {path}"); }
                else state.Log("BeamMP launcher not found - install BeamMP, or use Solo mode.");
                break;
            }
            case "mod":
            {
                var major = Detect.GameMajor(Detect.GameVersion(cfg));
                var match = await BeamMpCatalog.FindMatchingAsync(major, state.Progress());
                if (match.ZipPath != null) cfg.ModZip = match.ZipPath;
                break;
            }
            case "server":
            {
                var dir = await BeamMpCatalog.DownloadServerAsync(state.Progress());
                if (dir != null)
                {
                    cfg.ServerDir = dir;
                    await ServerConfig.InitializeConfigAsync(cfg, state.Progress());
                    state.Log("Server installed. Add a free AuthKey on the Server page.");
                }
                break;
            }
            case "authkey":
                Process.Start(new ProcessStartInfo("https://keymaster.beammp.com") { UseShellExecute = true });
                state.Log("Opened BeamMP Keymaster - paste the key on the Server page.");
                break;
            case "proxy":
            case "protoinput":
                NativeAssets.Extract(state.Progress());
                break;
            case "devreorder":
                if (!NativeAssets.LocateDevreorder(state.Progress()) &&
                    !await NativeAssets.DownloadDevreorderAsync(state.Progress()))
                    state.Log($"Could not install devreorder automatically. Put its x64 dinput8.dll in {Path.Combine(Paths.BinDir, "dinput8.dll")}");
                break;
            case "instances":
                state.Log("Instances are built automatically on the first launch.");
                break;
        }
        state.Save();
    }
}

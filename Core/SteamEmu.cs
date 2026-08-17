using System.IO;
using System.Text;

namespace BeamSplit.Core;

/// <summary>
/// Optional Steam API emulation (Goldberg) for the per-instance game copies.
///
/// WHY IT CAN BE NEEDED: one running Steam client serves one session, so several
/// copies of the same Steam game launched side by side can fight over it - the second
/// instance bounces back to Steam, or both share one set of cloud saves. Replacing
/// steam_api64.dll inside each INSTANCE lets them start independently and offline.
/// This is the same reason Nucleus Co-op bundles it.
///
/// SCOPE, deliberately narrow:
///  * Applied only to BeamSplit's instance folders. The real game install is never
///    touched - instances have their own real Bin64 copy, which is what we modify.
///  * The original steam_api64.dll is backed up next to it, so Restore() puts things
///    back exactly.
///  * BeamSplit does not download or bundle Goldberg. The user points at their own copy.
///  * If the install already has a third-party steam_api64 replacement, we say so
///    rather than stacking another one on top.
/// </summary>
public static class SteamEmu
{
    private const string SteamApi = "steam_api64.dll";
    private const string Backup = "steam_api64.dll.beamsplit-backup";

    /// <summary>Files Goldberg ships that we copy if present.</summary>
    private static readonly string[] Optional = ["steam_api.dll", "steamclient64.dll", "local_save.txt"];

    public static bool LooksLikeGoldberg(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, SteamApi));

    /// <summary>Nucleus Co-op bundles a copy; offer it rather than making the user hunt.</summary>
    public static string? FindExisting()
    {
        // Nucleus' bundle puts steam_api64.dll straight in the root, with variant builds
        // in subfolders - so check the plain root too, not just an x64 subdirectory.
        string[] roots =
        [
            @"C:\NucleusCoop\utils\GoldbergEmu",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NucleusCoop\utils\GoldbergEmu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NucleusCoop\utils\GoldbergEmu")
        ];
        string[] suffixes = ["experimental", "regular", "x64", "experimental\\x64", ""];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var s in suffixes)
            {
                var candidate = s.Length == 0 ? root : Path.Combine(root, s);
                if (LooksLikeGoldberg(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// True if the game's own steam_api64.dll has already been replaced by something
    /// else (OnlineFix, Steamless, another emulator). Stacking Goldberg on top of that
    /// usually breaks the game rather than helping.
    /// </summary>
    public static bool AlreadyPatched(AppConfig cfg)
    {
        if (!Detect.IsGameRoot(cfg.GameRoot)) return false;
        var bin = Path.Combine(cfg.GameRoot!, "Bin64");
        return File.Exists(Path.Combine(bin, "OnlineFix64.dll"))
            || File.Exists(Path.Combine(bin, "steam_api64_o.dll"))
            || File.Exists(Path.Combine(bin, "steam_api64.of"));
    }

    public static void Apply(AppConfig cfg, int instance, IProgress<string>? log = null)
    {
        if (!cfg.UseSteamEmu || !LooksLikeGoldberg(cfg.SteamEmuPath)) return;
        if (!Instances.Exists(cfg, instance)) return;

        var bin = Instances.Bin64(cfg, instance);
        var target = Path.Combine(bin, SteamApi);
        var backup = Path.Combine(bin, Backup);

        try
        {
            // keep the instance's original exactly once
            if (File.Exists(target) && !File.Exists(backup))
                File.Copy(target, backup);

            File.Copy(Path.Combine(cfg.SteamEmuPath!, SteamApi), target, true);

            foreach (var extra in Optional)
            {
                var src = Path.Combine(cfg.SteamEmuPath!, extra);
                if (File.Exists(src)) File.Copy(src, Path.Combine(bin, extra), true);
            }

            // a per-instance identity, so the instances don't look like one account
            var settings = Path.Combine(bin, "steam_settings");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "force_account_name.txt"),
                $"BeamSplit P{instance + 1}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(settings, "force_steamid.txt"),
                (76561197960265728L + 1000 + instance).ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(settings, "offline.txt"), "", new UTF8Encoding(false));

            log?.Report($"  P{instance}: Steam emulation applied (original saved as {Backup})");
        }
        catch (Exception ex)
        {
            log?.Report($"  P{instance}: Steam emulation failed - {ex.Message}");
        }
    }

    public static void Restore(AppConfig cfg, int instance, IProgress<string>? log = null)
    {
        var bin = Instances.Bin64(cfg, instance);
        var target = Path.Combine(bin, SteamApi);
        var backup = Path.Combine(bin, Backup);
        if (!File.Exists(backup)) return;

        try
        {
            File.Copy(backup, target, true);
            File.Delete(backup);
            var settings = Path.Combine(bin, "steam_settings");
            if (Directory.Exists(settings)) Directory.Delete(settings, true);
            log?.Report($"  P{instance}: original steam_api64.dll restored");
        }
        catch (Exception ex)
        {
            log?.Report($"  P{instance}: restore failed - {ex.Message}");
        }
    }

    public static void RestoreAll(AppConfig cfg, IProgress<string>? log = null)
    {
        for (var i = 0; i < 8; i++)
            if (Instances.Exists(cfg, i)) Restore(cfg, i, log);
    }

    /// <summary>Is emulation currently deployed to this instance?</summary>
    public static bool IsApplied(AppConfig cfg, int instance) =>
        Instances.Exists(cfg, instance) &&
        File.Exists(Path.Combine(Instances.Bin64(cfg, instance), Backup));
}

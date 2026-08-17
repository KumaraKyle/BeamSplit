using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BeamSplit.Core;

/// <summary>
/// Builds one game folder per player.
///
/// Why a whole folder each: this build of BeamNG rejects -userpath on the command line
/// (the process exits instantly with no log), and it resolves %LOCALAPPDATA% through the
/// Windows known-folder API so an environment override does nothing. The only mechanism
/// left is <gamedir>\startup.ini, which is one file per game folder.
///
/// To avoid N copies of a ~50GB install: content directories become junctions, root
/// files become hardlinks, and only Bin64 is a real copy (~500MB) because the game
/// writes there - and because each instance needs its own input proxy DLLs in it.
/// </summary>
public static partial class Instances
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFile, string existingFile, IntPtr attrs);

    public static string InstanceDir(AppConfig cfg, int i) => Path.Combine(cfg.InstancesDir, $"P{i}");
    public static string GameDir(AppConfig cfg, int i) => Path.Combine(InstanceDir(cfg, i), "game");
    public static string Bin64(AppConfig cfg, int i) => Path.Combine(GameDir(cfg, i), "Bin64");
    public static string GameExe(AppConfig cfg, int i) => Path.Combine(Bin64(cfg, i), "BeamNG.drive.x64.exe");
    public static string UserPath(AppConfig cfg, int i) => Path.Combine(InstanceDir(cfg, i), "userpath");
    public static string MpDir(AppConfig cfg, int i) => Path.Combine(InstanceDir(cfg, i), "mp");
    public static string CurrentProfile(AppConfig cfg, int i) => Path.Combine(UserPath(cfg, i), "current");

    public static bool Exists(AppConfig cfg, int i) => File.Exists(GameExe(cfg, i));

    public static int CountBuilt(AppConfig cfg) =>
        Directory.Exists(cfg.InstancesDir)
            ? Directory.GetDirectories(cfg.InstancesDir, "P*").Count(d => File.Exists(Path.Combine(d, "game", "Bin64", "BeamNG.drive.x64.exe")))
            : 0;

    public static void EnsureBuilt(AppConfig cfg, int players, IProgress<string>? log = null, bool rebuild = false)
    {
        if (!Detect.IsGameRoot(cfg.GameRoot))
            throw new InvalidOperationException($"BeamNG not found at '{cfg.GameRoot}'");

        Directory.CreateDirectory(cfg.InstancesDir);
        for (var i = 0; i < players; i++) Build(cfg, i, log, rebuild);
    }

    private static void Build(AppConfig cfg, int i, IProgress<string>? log, bool rebuild)
    {
        var game = GameDir(cfg, i);
        if (Directory.Exists(game) && !rebuild)
        {
            EnsureStartupIni(cfg, i);
            RepairMissingBin64Files(cfg, i, log);
            return;
        }

        if (Directory.Exists(game)) RemoveInstanceGameDir(game, log);
        Directory.CreateDirectory(game);
        log?.Report($"Building instance {i} ...");

        var root = cfg.GameRoot!;

        // content directories -> junctions (no disk cost)
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("Bin64", StringComparison.OrdinalIgnoreCase)) continue;
            Junction(Path.Combine(game, name), dir);
        }

        // root files -> hardlinks, falling back to a copy across volumes
        foreach (var file in Directory.GetFiles(root))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("startup.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(game, name);
            if (!CreateHardLink(dest, file, IntPtr.Zero))
            {
                try { File.Copy(file, dest, true); } catch { }
            }
        }

        // Bin64 -> real copy
        log?.Report($"  copying Bin64 (about 500MB) ...");
        var srcBin = Path.Combine(root, "Bin64");
        CopyDirectory(srcBin, Bin64(cfg, i));

        // Antivirus can quarantine files DURING the copy, which leaves an instance that
        // launches and dies with 0xC0000906 or "DLL was not found" - impossible to
        // diagnose from the game's own (empty) log. Defender did exactly this to
        // OnlineFix64.dll here. Compare against the source and say so plainly.
        var missing = RepairMissingFiles(srcBin, Bin64(cfg, i), log);
        if (missing.Count > 0)
        {
            log?.Report($"  ERROR: {missing.Count} file(s) did not survive the copy/repair - antivirus?");
            foreach (var m in missing.Take(5)) log?.Report($"    missing: {m}");
            throw new IOException(
                $"Instance P{i} is incomplete. {missing.Count} Bin64 file(s) are missing after repair. " +
                $"Check security software access to '{cfg.InstancesDir}', then rebuild the instance.");
        }

        Directory.CreateDirectory(UserPath(cfg, i));
        EnsureStartupIni(cfg, i);
        log?.Report($"  instance {i} ready");
    }

    /// <summary>The whole point of the separate game folder: this instance's own profile.</summary>
    private static void EnsureStartupIni(AppConfig cfg, int i)
    {
        var ini = Path.Combine(GameDir(cfg, i), "startup.ini");
        var want = $"[filesystem]\nUserPath = {UserPath(cfg, i)}\n";
        if (!File.Exists(ini) || File.ReadAllText(ini) != want)
            File.WriteAllText(ini, want, new UTF8Encoding(false));
    }

    /// <summary>
    /// Deletes an instance game folder WITHOUT following junctions into the real install.
    /// A plain recursive delete here would eat the actual game.
    /// </summary>
    public static void RemoveInstanceGameDir(string game, IProgress<string>? log = null)
    {
        foreach (var dir in Directory.GetDirectories(game))
        {
            var info = new DirectoryInfo(dir);
            if (info.LinkTarget != null)
            {
                // junction: remove the link only
                try { Directory.Delete(dir); }
                catch { RunCmd($"rmdir \"{dir}\""); }
            }
            else
            {
                try { Directory.Delete(dir, true); } catch (Exception ex) { log?.Report($"  {dir}: {ex.Message}"); }
            }
        }
        foreach (var f in Directory.GetFiles(game))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
        }
        try { Directory.Delete(game, true); } catch { }
    }

    public static void ResetProfile(AppConfig cfg, int i)
    {
        var up = UserPath(cfg, i);
        if (!Directory.Exists(up)) return;
        foreach (var f in Directory.GetFiles(up, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        try { Directory.Delete(up, true); } catch { }
        Directory.CreateDirectory(up);
    }

    private static void Junction(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch { /* symlinks need admin or developer mode; junctions don't */ }
        RunCmd($"mklink /J \"{link}\" \"{target}\"");
    }

    private static void RunCmd(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(20000);
        }
        catch { }
    }

    /// <summary>
    /// Existing instances are normally reused. Repair files that disappeared since the
    /// original build before launching, then fail early if they still cannot be kept.
    /// </summary>
    private static void RepairMissingBin64Files(AppConfig cfg, int i, IProgress<string>? log)
    {
        var source = Path.Combine(cfg.GameRoot!, "Bin64");
        var dest = Bin64(cfg, i);
        var missing = MissingAfterCopy(source, dest);
        if (missing.Count == 0) return;

        log?.Report($"  P{i}: repairing {missing.Count} missing Bin64 file(s) ...");
        missing = RepairMissingFiles(source, dest, log);
        if (missing.Count == 0)
        {
            log?.Report($"  P{i}: Bin64 repair complete");
            return;
        }

        foreach (var file in missing.Take(5)) log?.Report($"    still missing: {file}");
        throw new IOException(
            $"Instance P{i} is incomplete. {missing.Count} Bin64 file(s) are missing after repair. " +
            $"Check security software access to '{cfg.InstancesDir}', then rebuild the instance.");
    }

    private static List<string> RepairMissingFiles(string source, string dest, IProgress<string>? log)
    {
        foreach (var relative in MissingAfterCopy(source, dest))
        {
            var sourceFile = Path.Combine(source, relative);
            var destFile = Path.Combine(dest, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(sourceFile, destFile, true);
            }
            catch (Exception ex)
            {
                log?.Report($"    could not restore {relative}: {ex.Message}");
            }
        }

        return MissingAfterCopy(source, dest);
    }

    /// <summary>Files present in the source Bin64 but absent in the instance copy.</summary>
    private static List<string> MissingAfterCopy(string source, string dest)
    {
        var missing = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, f);
                if (!File.Exists(Path.Combine(dest, relative))) missing.Add(relative);
            }
        }
        catch { }
        return missing;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), true);
    }
}

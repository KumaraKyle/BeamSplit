using System.Diagnostics;
using System.IO;

namespace BeamSplit.Core;

/// <summary>
/// Starts a session: builds instances, applies settings, deploys input isolation,
/// starts each BeamMP launcher and game, then tiles the windows.
///
/// Non-obvious rules, all learned the hard way:
///  * Launch <instance>\game\Bin64\BeamNG.drive.x64.exe DIRECTLY. The root
///    BeamNG.drive.exe is the DirectX/Vulkan chooser dialog.
///  * Do not pass -userpath: this build exits instantly. The per-instance userpath is
///    resolved from its copied folder layout. We do pass -nosteam so Steam Input cannot
///    install a second controller hook after Proto Input and merge pad 0 into every game.
///  * BeamMP launcher gets --port N --user-path <p> --no-launch; we start the game.
///  * Instance ports are spaced by 2, so a launcher's secondary port can never
///    collide with the next instance's primary.
/// </summary>
public sealed class Launcher(AppState state)
{
    private readonly AppState _state = state;

    public int PortFor(int instance) => _state.Config.BasePort + instance * 2;

    public async Task LaunchAsync(IProgress<string>? log = null, bool rebuild = false, CancellationToken ct = default)
    {
        var cfg = _state.Config;
        var players = Math.Max(1, cfg.Players.Count);
        var beamMp = cfg.Mode == "BeamMP";

        log?.Report($"Launching {players} instance(s) - {cfg.Mode}");

        // A second click on Launch used to leave the previous games and launchers
        // alive. The new launchers then fought the old ones for ports (WSAEADDRINUSE
        // 10048), and a stale launcher could replace just one instance's pinned
        // BeamMP.zip after BeamSplit repaired it. A launch now replaces only the
        // processes that live under BeamSplit's own instance root.
        StopPreviousInstanceProcesses(cfg, log);

        // 1. instances
        Instances.EnsureBuilt(cfg, players, log, rebuild);

        // 2. per-instance input isolation
        if (cfg.Isolate) InputSetup.Deploy(cfg, log);

        // 3. server (BeamMP only)
        if (beamMp && !ServerConfig.IsRunning())
        {
            log?.Report("Starting BeamMP server ...");
            ServerConfig.Start(cfg);
            await Task.Delay(3000, ct);
            log?.Report(ServerConfig.IsRunning() ? "  server up" : "  server exited - check the AuthKey");
        }

        // 4. all instances in parallel. Each player has its own launcher directory,
        // port, userpath and game copy, so serialising them only adds dead time.
        log?.Report($"Starting all {players} player pipelines in parallel ...");
        var launches = Enumerable.Range(0, players)
            .Select(i => LaunchInstanceAsync(i, beamMp, log, ct))
            .ToArray();
        var gameProcesses = await Task.WhenAll(launches);

        // 5. windows
        await TileAsync(log, ct, gameProcesses);
    }

    private async Task<Process?> LaunchInstanceAsync(int i, bool beamMp,
        IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var cfg = _state.Config;

            GameSettings.ApplyFocusFixes(cfg, i, log);
            GameSettings.ApplyGraphics(cfg, i, log);

            // Optional, off by default. Applied to the instance's own Bin64 copy only -
            // see SteamEmu for the scope and the backup/restore behaviour.
            if (cfg.UseSteamEmu) SteamEmu.Apply(cfg, i, log);

            if (beamMp)
            {
                GameSettings.SetLauncherPort(cfg, i, PortFor(i));
                GameSettings.EnableBeamMpMod(cfg, i);
                await StartBeamMpReadyAsync(i, log, ct);
            }

            var exe = Instances.GameExe(cfg, i);
            const string gameArgs = "-nosteam";
            var proc = ProtoInput.Start(cfg, i, exe, log, commandLine: gameArgs);
            if (proc is null)
            {
                var start = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Instances.Bin64(cfg, i),
                    UseShellExecute = false
                };
                start.ArgumentList.Add(gameArgs);
                proc = Process.Start(start);
            }
            log?.Report($"  P{i}: game pid {proc?.Id}");
            await ReportEarlyExitAsync(i, proc, log, ct);
            return proc;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Report($"  P{i}: launch failed - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Start the BeamMP launcher, let it finish its own update/download pass, then put
    /// the version-matched client back before BeamNG starts. That avoids the scary
    /// "Failed to open file directory: BeamMP.zip" log caused by ACL-locking the file.
    /// </summary>
    private async Task StartBeamMpReadyAsync(int i, IProgress<string>? log, CancellationToken ct)
    {
        var cfg = _state.Config;
        InputSetup.UnlockMatchingMod(cfg, i);

        var p = StartBeamMpLauncher(i, log, resetLog: true);
        var ready = await WaitForLauncherReadyAsync(cfg, i, p, log, ct);
        if (!ready)
            log?.Report($"  P{i}: launcher did not report ready in time; starting game anyway");

        InputSetup.InstallMatchingMod(cfg, i, log, lockFile: false);
    }

    private Process? StartBeamMpLauncher(int i, IProgress<string>? log, bool resetLog = false)
    {
        var cfg = _state.Config;
        var mp = Instances.MpDir(cfg, i);
        Directory.CreateDirectory(Path.Combine(mp, "Resources"));

        var exe = Path.Combine(mp, "BeamMP-Launcher.exe");
        try { File.Copy(cfg.LauncherExe!, exe, true); } catch { /* running: keep the existing copy */ }
        if (resetLog) ResetLauncherLog(cfg, i);

        var port = PortFor(i);
        var args = $"--port {port} --user-path \"{Instances.UserPath(cfg, i)}\" --no-launch";

        var start = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = mp,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var p = Process.Start(start);
        if (p != null)
        {
            p.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report($"  P{i} mp: {e.Data}");
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report($"  P{i} mp ! {e.Data}");
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        log?.Report($"  P{i}: launcher pid {p?.Id} on port {port}");
        return p;
    }

    private static void StopPreviousInstanceProcesses(AppConfig cfg, IProgress<string>? log)
    {
        var root = Path.GetFullPath(cfg.InstancesDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var stopped = 0;

        foreach (var name in new[] { "BeamMP-Launcher", "BeamNG.drive", "BeamNG.drive.x64" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) ||
                        !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        continue;

                    process.Kill(true);
                    process.WaitForExit(3000);
                    stopped++;
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        if (stopped > 0)
            log?.Report($"Stopped {stopped} process(es) from the previous BeamSplit session.");
    }

    private static void ResetLauncherLog(AppConfig cfg, int i)
    {
        try
        {
            var log = Path.Combine(Instances.MpDir(cfg, i), "Launcher.log");
            if (File.Exists(log)) File.WriteAllText(log, "");
        }
        catch { }
    }

    private static async Task<bool> WaitForLauncherReadyAsync(AppConfig cfg, int i, Process? p, IProgress<string>? log, CancellationToken ct)
    {
        var path = Path.Combine(Instances.MpDir(cfg, i), "Launcher.log");
        var deadline = DateTime.UtcNow.AddSeconds(75);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (p is { HasExited: true })
                {
                    log?.Report($"  P{i}: launcher exited early ({p.ExitCode})");
                    return false;
                }
            }
            catch { }

            if (TailContains(path, "Core Network on start", 80))
            {
                log?.Report($"  P{i}: launcher ready");
                return true;
            }

            await Task.Delay(1000, ct);
        }

        return false;
    }

    private static async Task ReportEarlyExitAsync(int i, Process? p, IProgress<string>? log, CancellationToken ct)
    {
        if (p is null) return;
        try
        {
            await Task.Delay(8000, ct);
            if (p.HasExited)
                log?.Report($"  P{i}: game exited immediately (code {p.ExitCode})");
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static bool TailContains(string path, string needle, int lines)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var q = new Queue<string>(lines);
            string? l;
            while ((l = sr.ReadLine()) != null)
            {
                if (q.Count == lines) q.Dequeue();
                q.Enqueue(l);
            }
            return q.Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Places each window on its slot's region, then parks focus off the games.</summary>
    public async Task TileAsync(IProgress<string>? log = null, CancellationToken ct = default,
        IReadOnlyList<Process?>? launched = null)
    {
        var cfg = _state.Config;
        var players = Math.Max(1, cfg.Players.Count);
        var monitors = Native.GetMonitors();

        var placed = new HashSet<int>();
        var deadline = DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline && placed.Count < players)
        {
            ct.ThrowIfCancellationRequested();
            var waitingForWindow = false;

            if (launched is not null)
            {
                // Parallel startup makes process enumeration order nondeterministic.
                // Track the exact process returned for each player instead.
                for (var idx = 0; idx < players; idx++)
                {
                    if (placed.Contains(idx)) continue;
                    var proc = idx < launched.Count ? launched[idx] : null;
                    if (proc is null) continue;

                    try
                    {
                        proc.Refresh();
                        if (proc.HasExited) continue;
                        waitingForWindow = true;
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;

                        var slot = cfg.Players.ElementAtOrDefault(idx) ?? new PlayerSlot { Index = idx };
                        var rect = Tiling.RegionFor(slot, monitors);
                        Tiling.Place(proc.MainWindowHandle, rect, cfg.Borderless);
                        placed.Add(idx);
                        log?.Report($"  P{idx}: window at {rect.X},{rect.Y} {rect.W}x{rect.H}");
                    }
                    catch { }
                }
            }
            else
            {
                var wins = Tiling.GameWindows();
                for (var idx = 0; idx < wins.Count && idx < players; idx++)
                {
                    if (!placed.Add(idx)) continue;
                    var proc = wins[idx];

                    var slot = cfg.Players.ElementAtOrDefault(idx) ?? new PlayerSlot { Index = idx };
                    var rect = Tiling.RegionFor(slot, monitors);
                    Tiling.Place(proc.MainWindowHandle, rect, cfg.Borderless);
                    log?.Report($"  P{idx}: window at {rect.X},{rect.Y} {rect.W}x{rect.H}");
                }
            }

            if (launched is not null && placed.Count < players && !waitingForWindow) break;
            if (placed.Count < players) await Task.Delay(4000, ct);
        }

        if (placed.Count < players)
            log?.Report($"Only found {placed.Count}/{players} BeamNG window(s). If game pids were logged, they likely exited or stalled before creating a window.");

        if (Tiling.ParkFocus())
            log?.Report("Focus parked on the desktop - do not click into a game window.");
    }

    /// <summary>
    /// Repositions the BeamNG windows that already exist, once, without waiting for
    /// missing instances. Screen-layout changes use this path so the UI remains
    /// responsive even while a game is still starting or has exited.
    /// </summary>
    public int RetileRunning(IProgress<string>? log = null)
    {
        var cfg = _state.Config;
        var monitors = Native.GetMonitors();
        var wins = Tiling.GameWindows();
        var count = Math.Min(wins.Count, cfg.Players.Count);

        for (var idx = 0; idx < count; idx++)
        {
            var slot = cfg.Players[idx];
            var rect = Tiling.RegionFor(slot, monitors);
            Tiling.Place(wins[idx].MainWindowHandle, rect, cfg.Borderless);
            log?.Report($"  P{idx}: window retiled at {rect.X},{rect.Y} {rect.W}x{rect.H}");
        }

        log?.Report(count == 0
            ? "No running BeamNG windows to retile; the layout is saved for launch."
            : $"Applied screen layout to {count} running window(s).");

        if (count > 0) Tiling.ParkFocus();
        return count;
    }

    public void StopAll(IProgress<string>? log = null)
    {
        foreach (var name in new[] { "BeamMP-Launcher", "BeamNG.drive", "BeamNG.drive.x64" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); } catch { }
            }
        }
        log?.Report("Stopped all instances and launchers.");
    }
}

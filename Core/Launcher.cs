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
    private readonly Dictionary<int, Process> _gameProcesses = [];

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

        // Personal mods live in a BeamSplit-owned subfolder of each profile; selected
        // BeamMP server mods live in Resources/Client. Sync before the server starts so
        // it indexes the current set, while leaving the user's source folder untouched.
        ModManager.Apply(cfg, players, log);
        _state.Save();

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
        _gameProcesses.Clear();
        for (var ordinal = 0; ordinal < gameProcesses.Length; ordinal++)
        {
            var process = gameProcesses[ordinal];
            if (process is null) continue;
            var instance = cfg.Players.ElementAtOrDefault(ordinal)?.Index ?? ordinal;
            _gameProcesses[instance] = process;
        }

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
            GameSettings.ApplyAudio(cfg, i, log);
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
            if (proc != null) _ = HideBeamNgConsoleAsync(proc, ct);
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

    private static async Task HideBeamNgConsoleAsync(Process process, CancellationToken ct)
    {
        try
        {
            // The companion window may be created before or after the renderer, so
            // watch across early engine startup. The launch cinematic covers this
            // interval and the captured output is rendered on its in-dash CRT.
            for (var pass = 0; pass < 80 && !process.HasExited; pass++)
            {
                Native.HideBeamNgConsoleWindows((uint)process.Id);
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
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
        BeamMpAudioIsolation.PatchClient(cfg, i,
            string.Equals(cfg.AudioMixMode, "LocalVehicle", StringComparison.OrdinalIgnoreCase), log);
        BeamMpAutoJoin.PatchClient(cfg, i, cfg.AutoJoinBeamMp, log);
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

    /// <summary>
    /// Places windows immediately when they appear, then keeps checking while BeamNG
    /// finishes graphics initialization. BeamNG can maximize, recreate or resize its
    /// window several seconds after MainWindowHandle first becomes non-zero, so a
    /// successful one-shot SetWindowPos is not a stable result.
    /// </summary>
    public async Task TileAsync(IProgress<string>? log = null, CancellationToken ct = default,
        IReadOnlyList<Process?>? launched = null)
    {
        var cfg = _state.Config;
        var players = Math.Max(1, cfg.Players.Count);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var firstWindowAt = DateTime.MinValue;
        var lastHandles = new Dictionary<int, IntPtr>();
        var stablePasses = new Dictionary<int, int>();
        var reported = new HashSet<int>();

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var monitors = Native.GetMonitors();
            var ready = 0;
            var settled = 0;
            for (var ordinal = 0; ordinal < players; ordinal++)
            {
                var slot = cfg.Players.ElementAtOrDefault(ordinal) ?? new PlayerSlot { Index = ordinal };
                Process? proc = launched is not null && ordinal < launched.Count
                    ? launched[ordinal]
                    : Tiling.WindowForInstance(cfg, slot.Index);
                if (proc is null)
                {
                    if (launched is not null) settled++;
                    continue;
                }

                try
                {
                    Native.HideBeamNgConsoleWindows((uint)proc.Id);
                    proc.Refresh();
                    if (proc.HasExited) { settled++; continue; }
                    var hwnd = proc.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    if (firstWindowAt == DateTime.MinValue) firstWindowAt = DateTime.UtcNow;

                    if (!lastHandles.TryGetValue(slot.Index, out var previous) || previous != hwnd)
                    {
                        lastHandles[slot.Index] = hwnd;
                        stablePasses[slot.Index] = 0;
                    }

                    var rect = Tiling.RegionFor(slot, monitors);
                    if (!Tiling.Matches(hwnd, rect, cfg.Borderless))
                    {
                        Tiling.Place(hwnd, rect, cfg.Borderless);
                        stablePasses[slot.Index] = 0;
                    }
                    else stablePasses[slot.Index] = stablePasses.GetValueOrDefault(slot.Index) + 1;

                    if (reported.Add(slot.Index))
                        log?.Report($"  P{slot.Index}: window acquired at {rect.X},{rect.Y} {rect.W}x{rect.H}; stabilizing ...");
                    if (stablePasses.GetValueOrDefault(slot.Index) >= 8) { ready++; settled++; }
                }
                catch { }
            }

            // Stay on guard for at least 15 seconds after the first window. This spans
            // BeamNG's late display-mode reset instead of declaring success too early.
            if (ready == players && firstWindowAt != DateTime.MinValue &&
                DateTime.UtcNow - firstWindowAt >= TimeSpan.FromSeconds(15))
                break;
            if (launched is not null && settled == players &&
                (firstWindowAt == DateTime.MinValue || DateTime.UtcNow - firstWindowAt >= TimeSpan.FromSeconds(15)))
                break;
            await Task.Delay(250, ct);
        }

        var finalCount = lastHandles.Count;
        if (finalCount < players)
            log?.Report($"Only found {finalCount}/{players} BeamNG window(s). If game pids were logged, they likely exited or stalled before creating a window.");
        else log?.Report($"All {players} window(s) verified in their assigned regions.");

        if (Tiling.ParkFocus())
            log?.Report("Focus parked on the desktop - do not click into a game window.");
    }

    /// <summary>
    /// Repositions existing windows repeatedly for a short verification period. Slots
    /// are matched to their instance exe path, so removing P0 does not make P1's window
    /// inherit P0 merely because it became the first item in the UI list.
    /// </summary>
    public async Task<int> RetileRunningAsync(IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var cfg = _state.Config;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var touched = new HashSet<int>();
        var stable = new Dictionary<int, int>();

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var monitors = Native.GetMonitors();
            var wins = Tiling.GameWindows();
            foreach (var slot in cfg.Players)
            {
                var proc = ResolveRunningProcess(cfg, slot.Index, wins);
                if (proc is null) continue;
                var rect = Tiling.RegionFor(slot, monitors);
                if (!Tiling.Matches(proc.MainWindowHandle, rect, cfg.Borderless))
                {
                    Tiling.Place(proc.MainWindowHandle, rect, cfg.Borderless);
                    stable[slot.Index] = 0;
                }
                else stable[slot.Index] = stable.GetValueOrDefault(slot.Index) + 1;
                touched.Add(slot.Index);
            }

            // Require three uninterrupted seconds. BeamNG often recreates its window
            // shortly after a resize, and the previous 1.5 s check finished before
            // that late fullscreen/caption reset happened.
            if (touched.Count > 0 && touched.All(i => stable.GetValueOrDefault(i) >= 12)) break;
            await Task.Delay(250, ct);
        }

        log?.Report(touched.Count == 0
            ? "No running BeamNG windows to retile; the layout is saved for launch."
            : $"Verified screen layout on {touched.Count} running window(s).");

        if (touched.Count > 0) Tiling.ParkFocus();
        return touched.Count;
    }

    private Process? ResolveRunningProcess(AppConfig cfg, int instance, IReadOnlyList<Process> windows)
    {
        if (_gameProcesses.TryGetValue(instance, out var tracked))
        {
            try
            {
                tracked.Refresh();
                if (!tracked.HasExited && tracked.MainWindowHandle != IntPtr.Zero) return tracked;
            }
            catch { }
            _gameProcesses.Remove(instance);
        }
        return Tiling.WindowForInstance(cfg, instance, windows);
    }

    public void StopSession(IProgress<string>? log = null)
    {
        foreach (var name in new[] { "BeamMP-Launcher", "BeamNG.drive", "BeamNG.drive.x64" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); } catch { }
            }
        }
        _gameProcesses.Clear();
        log?.Report("Stopped all instances and launchers.");
    }

    public void StopAll(IProgress<string>? log = null)
    {
        StopSession(log);
        ServerConfig.Stop();
        log?.Report("Stopped the BeamMP server and the entire session.");
    }
}

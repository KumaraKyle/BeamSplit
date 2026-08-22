using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace BeamSplit.Core;

public enum InstanceState
{
    Idle,
    Building,
    Launching,
    WaitingForLauncher,
    GameRunning,
    Connected,     // BeamMP: game talking to its launcher
    Synced,        // BeamMP: actually in the shared world
    Error
}

public sealed class InstanceStatus
{
    public int Index { get; init; }
    public InstanceState State { get; set; } = InstanceState.Idle;
    public string Detail { get; set; } = "";
    public int GamePid { get; set; }
    public int LauncherPid { get; set; }
    public int Port { get; set; }
    public int Pad { get; set; } = -1;
    public long MemoryMb { get; set; }
    public double CpuPercent { get; set; }
    public string Monitor { get; set; } = "";
    public bool PortListening { get; set; }
    public bool GameConnected { get; set; }
    public bool Synced { get; set; }
    public string LastLine { get; set; } = "";
    public string LauncherLine { get; set; } = "";
    public string ModState { get; set; } = "not checked";
    public bool ModOk { get; set; } = true;

    public string StateText => State switch
    {
        InstanceState.Idle => "idle",
        InstanceState.Building => "building",
        InstanceState.Launching => "launching",
        InstanceState.WaitingForLauncher => "waiting for launcher",
        InstanceState.GameRunning => "running",
        InstanceState.Connected => "connected",
        InstanceState.Synced => "in session",
        _ => "error"
    };
}

public sealed class ServerStatus
{
    public bool Running { get; set; }
    public bool Listening { get; set; }
    public int Port { get; set; }
    public string Map { get; set; } = "";
    public bool AuthKey { get; set; }
    public List<string> Players { get; set; } = [];
    public TimeSpan Uptime { get; set; }
}

/// <summary>
/// Works out what each instance is actually doing, from the same signals we previously
/// had to correlate by hand across four log files.
///
/// The distinction that matters most is Connected vs Synced: a client can be connected
/// to the server and still have no car in the shared world, which looks exactly like
/// "it's broken" from the outside. That specific state cost hours to diagnose manually,
/// so it gets its own state rather than being folded into "running".
/// </summary>
public sealed partial class SessionMonitor
{
    private readonly AppState _state;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, (DateTime At, TimeSpan Cpu)> _cpuSamples = [];
    private DateTime _serverStarted = DateTime.MinValue;

    public List<InstanceStatus> Items { get; private set; } = [];
    public ServerStatus Server { get; private set; } = new();

    public event Action? Updated;

    public SessionMonitor(AppState state)
    {
        _state = state;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        var cfg = _state.Config;
        var single = cfg.SessionEngine == SessionEngine.SingleInstanceExperimental;
        var beamMp = !single && cfg.Mode == "BeamMP";

        // ---- server ----
        var srvProc = Process.GetProcessesByName("BeamMP-Server").FirstOrDefault();
        var toml = ServerConfig.Read(cfg);
        var port = int.TryParse(toml.GetValueOrDefault("Port", "30814"), out var p) ? p : 30814;
        if (srvProc != null && _serverStarted == DateTime.MinValue)
        {
            try { _serverStarted = srvProc.StartTime; } catch { _serverStarted = DateTime.Now; }
        }
        if (srvProc == null) _serverStarted = DateTime.MinValue;

        Server = new ServerStatus
        {
            Running = srvProc != null,
            Listening = IsListening(port),
            Port = port,
            Map = toml.GetValueOrDefault("Map", ""),
            AuthKey = ServerConfig.HasAuthKey(cfg),
            Players = ReadServerPlayers(cfg),
            Uptime = _serverStarted == DateTime.MinValue ? TimeSpan.Zero : DateTime.Now - _serverStarted
        };

        // ---- instances ----
        var monitors = Native.GetMonitors();
        var games = Tiling.GameWindows();
        var launchers = Process.GetProcessesByName("BeamMP-Launcher").ToList();
        var list = new List<InstanceStatus>();

        var slots = cfg.Players.Count > 0
            ? cfg.Players
            : [new PlayerSlot { Index = 0 }];
        foreach (var slot in slots)
        {
            // PlayerSlot.Index remains the live identity while a session is running.
            // The list can legitimately contain only P1 after P0 drops out.
            var i = slot.Index;
            var st = new InstanceStatus
            {
                Index = i,
                Pad = slot.Pad,
                Port = cfg.BasePort + i * 2,
                Monitor = MonitorLabel(slot, monitors)
            };

            var processIndex = single ? Instances.SingleInstanceIndex : i;
            if (!Instances.Exists(cfg, processIndex))
            {
                st.State = InstanceState.Idle;
                st.Detail = "not built";
                list.Add(st);
                continue;
            }

            // the game process belonging to THIS instance, by exe path
            var exe = Instances.GameExe(cfg, processIndex);
            var game = games.FirstOrDefault(g => PathOf(g) is { } path &&
                                                 path.Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (game != null)
            {
                st.GamePid = game.Id;
                st.MemoryMb = game.WorkingSet64 / 1024 / 1024;
                st.CpuPercent = SampleCpu(game);
            }

            st.PortListening = beamMp && IsListening(st.Port);
            st.LauncherPid = launchers.FirstOrDefault(l => CommandLineHas(l, $"--port {st.Port}"))?.Id ?? 0;
            st.GameConnected = beamMp && LauncherSawGame(cfg, i);
            st.LastLine = LastGameLine(cfg, processIndex);
            st.LauncherLine = LastLauncherLine(cfg, i);
            (st.ModState, st.ModOk) = BeamMpClientState(cfg, i, beamMp);

            if (beamMp && st.GamePid != 0)
                st.Synced = Server.Players.Count > 0 && SyncedCount(cfg) >= CountRunning(games, cfg);

            // ---- resolve the state, and be specific about WHY when it's wrong ----
            if (st.GamePid == 0)
            {
                if (st.LauncherPid != 0) { st.State = InstanceState.WaitingForLauncher; st.Detail = "launcher up, game not started"; }
                else { st.State = InstanceState.Idle; st.Detail = "not running"; }
            }
            else if (!beamMp)
            {
                st.State = InstanceState.GameRunning;
                st.Detail = single
                    ? $"seat {i + 1}, shared pid {st.GamePid}, {st.MemoryMb} MB total"
                    : $"pid {st.GamePid}, {st.MemoryMb} MB";
            }
            else if (st.LauncherPid == 0)
            {
                st.State = InstanceState.Error;
                st.Detail = $"game running but its launcher (port {st.Port}) is gone";
            }
            else if (!st.GameConnected)
            {
                st.State = InstanceState.Launching;
                st.Detail = "game started, not yet linked to its launcher";
            }
            else if (!st.Synced)
            {
                st.State = InstanceState.Connected;
                st.Detail = "connected to the launcher - not synced into the world yet";
            }
            else
            {
                st.State = InstanceState.Synced;
                st.Detail = $"pid {st.GamePid}, port {st.Port}, {st.MemoryMb} MB";
            }

            if (beamMp && st.GamePid != 0 && !st.ModOk)
            {
                st.State = InstanceState.Error;
                st.Detail = st.ModState;
            }

            if (!single)
            {
                var missing = InputSetup.Verify(cfg, i);
                if (missing.Count > 0 && st.State != InstanceState.Idle)
                    st.Detail += $"  -  input proxy incomplete ({string.Join(", ", missing)})";
            }

            list.Add(st);
        }

        Items = list;
        Updated?.Invoke();

        // Process.GetProcesses* allocates native handles. Refresh runs every two
        // seconds, so retaining these temporary wrappers steadily exhausts handles and
        // unmanaged memory during a long session.
        srvProc?.Dispose();
        foreach (var process in games) process.Dispose();
        foreach (var process in launchers) process.Dispose();
    }

    private double SampleCpu(Process process)
    {
        try
        {
            process.Refresh();
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            var percent = 0d;
            if (_cpuSamples.TryGetValue(process.Id, out var before))
            {
                var elapsed = (now - before.At).TotalMilliseconds;
                if (elapsed > 0)
                    percent = (cpu - before.Cpu).TotalMilliseconds /
                              (elapsed * Environment.ProcessorCount) * 100d;
            }
            _cpuSamples[process.Id] = (now, cpu);
            return Math.Clamp(percent, 0, 100);
        }
        catch { return 0; }
    }

    private static int CountRunning(List<Process> games, AppConfig cfg) =>
        games.Count(g => PathOf(g)?.StartsWith(cfg.InstancesDir, StringComparison.OrdinalIgnoreCase) == true);

    private static string? PathOf(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private static string MonitorLabel(PlayerSlot? slot, List<MonitorInfo> monitors)
    {
        if (slot is null) return "";
        var m = monitors.FirstOrDefault(x => x.DeviceName == slot.MonitorDevice);
        if (m.DeviceName is null or "") return "(monitor missing)";
        var name = m.DeviceName.Replace(@"\\.\", "");
        return slot.Split == SplitMode.Full ? name : $"{name} r{slot.Region}";
    }

    private static bool IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch { return false; }
    }

    private static bool CommandLineHas(Process p, string needle)
    {
        try
        {
            using var s = new ManagementObjectSearcherShim(p.Id);
            return s.CommandLine?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    /// <summary>"Game Connected!" in this instance's launcher log means the UDP link is up.</summary>
    private static bool LauncherSawGame(AppConfig cfg, int i)
    {
        var log = Path.Combine(Instances.MpDir(cfg, i), "Launcher.log");
        return TailContains(log, "Game Connected!", 40);
    }

    private static List<string> ReadServerPlayers(AppConfig cfg)
    {
        var log = ServerConfig.LogPath(cfg);
        var players = new List<string>();
        if (log is null || !File.Exists(log)) return players;

        try
        {
            foreach (var raw in ReadTail(log, 200))
            {
                var line = LogTail.Clean(raw);
                var join = JoinRe().Match(line);
                if (join.Success && !players.Contains(join.Groups[1].Value)) players.Add(join.Groups[1].Value);
                var left = LeftRe().Match(line);
                if (left.Success) players.Remove(left.Groups[1].Value);
            }
        }
        catch { }
        return players;
    }

    private static int SyncedCount(AppConfig cfg)
    {
        var log = ServerConfig.LogPath(cfg);
        if (log is null || !File.Exists(log)) return 0;
        var synced = new HashSet<string>();
        try
        {
            foreach (var raw in ReadTail(log, 200))
            {
                var line = LogTail.Clean(raw);
                var m = SyncRe().Match(line);
                if (m.Success) synced.Add(m.Groups[1].Value);
                var left = LeftRe().Match(line);
                if (left.Success) synced.Remove(left.Groups[1].Value);
            }
        }
        catch { }
        return synced.Count;
    }

    private static string LastGameLine(AppConfig cfg, int i)
    {
        var log = Path.Combine(Instances.CurrentProfile(cfg, i), "beamng.log");
        var tail = ReadTail(log, 1);
        return tail.Count > 0 ? tail[^1] : "";
    }

    private static string LastLauncherLine(AppConfig cfg, int i)
    {
        var log = Path.Combine(Instances.MpDir(cfg, i), "Launcher.log");
        var tail = ReadTail(log, 12);
        return tail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
    }

    private static (string Text, bool Ok) BeamMpClientState(AppConfig cfg, int i, bool beamMp)
    {
        if (!beamMp) return ("not used in Solo", true);
        var gameLog = Path.Combine(Instances.CurrentProfile(cfg, i), "beamng.log");
        var client = Path.Combine(Instances.CurrentProfile(cfg, i), "mods", "multiplayer", "BeamMP.zip");

        if (string.IsNullOrWhiteSpace(cfg.ModZip) || !File.Exists(cfg.ModZip))
            return ("matching BeamMP client is missing", false);
        if (!File.Exists(client)) return ("BeamMP.zip is missing from this instance", false);

        try
        {
            var wanted = BeamMpCatalog.ModTargetVersion(cfg.ModZip);
            var actual = BeamMpCatalog.ModTargetVersion(client);
            if (wanted != actual)
                return ($"BeamMP client targets 0.{actual}.x; expected 0.{wanted}.x", false);

            if (string.Equals(cfg.AudioMixMode, "LocalVehicle", StringComparison.OrdinalIgnoreCase) &&
                !BeamMpAudioIsolation.IsPatched(client))
                return ("compatible client installed; remote-audio hook is missing", false);
        }
        catch { }

        if (TailContains(gameLog, "Deactivating BeamMP mod", 350) ||
            TailContains(gameLog, "BeamMP is not compatible", 350))
            return ("BeamMP disabled itself as incompatible", false);
        var launcherLog = Path.Combine(Instances.MpDir(cfg, i), "Launcher.log");
        if (TailContains(launcherLog, "Could not resolve host: auth.beammp.com", 120))
            return ("BeamMP authentication DNS failed; auth.beammp.com is unreachable", false);
        if (TailContains(gameLog, "MPCoreNetwork.onLauncherConnected", 350))
            return ("BeamMP loaded and linked", true);
        return ("compatible client installed; waiting for game", true);
    }

    private static bool TailContains(string path, string needle, int lines) =>
        ReadTail(path, lines).Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the last N lines of a file that another process still holds open.</summary>
    private static List<string> ReadTail(string path, int lines)
    {
        var result = new List<string>();
        if (!File.Exists(path)) return result;
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
            result.AddRange(q);
        }
        catch { }
        return result;
    }

    [GeneratedRegex(@"Assigned ID \d+ to (\S+)")]
    private static partial Regex JoinRe();
    [GeneratedRegex(@"(\S+) is now synced")]
    private static partial Regex SyncRe();
    [GeneratedRegex(@"(\S+) Connection Terminated")]
    private static partial Regex LeftRe();
}

/// <summary>Tiny WMI wrapper so we can read a process command line without a hard dependency.</summary>
internal sealed class ManagementObjectSearcherShim : IDisposable
{
    public string? CommandLine { get; }

    public ManagementObjectSearcherShim(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var o in searcher.Get())
            {
                CommandLine = o["CommandLine"]?.ToString();
                break;
            }
        }
        catch { CommandLine = null; }
    }

    public void Dispose() { }
}

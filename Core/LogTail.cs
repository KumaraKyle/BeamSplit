using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BeamSplit.Core;

/// <summary>
/// Tails a log file that another process is actively writing.
///
/// Two things make this fiddly and both are non-negotiable:
///  * BeamNG and the BeamMP server hold their logs OPEN, so the file must be opened
///    with FileShare.ReadWrite or every read throws.
///  * The BeamMP server writes ANSI escapes (ESC[2K, ESC[0G, a "> " prompt) which make
///    the raw text unreadable, so they are stripped.
///
/// Truncation is handled too: BeamNG rotates its log on every start, and a shrinking
/// file means "start again from zero" rather than "seek past the end".
/// </summary>
public sealed partial class LogTail : IDisposable
{
    private readonly string _path;
    private readonly Action<string> _onLine;
    private readonly bool _stripAnsi;
    private readonly CancellationTokenSource _cts = new();
    private long _offset;

    public string Path => _path;
    public string Tag { get; }

    public LogTail(string path, string tag, Action<string> onLine, bool stripAnsi = false, bool fromStart = false)
    {
        _path = path;
        Tag = tag;
        _onLine = onLine;
        _stripAnsi = stripAnsi;
        if (!fromStart && File.Exists(path))
        {
            try { _offset = new FileInfo(path).Length; } catch { _offset = 0; }
        }
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Pump(); } catch { /* transient lock/rotation - try again next tick */ }
            try { await Task.Delay(500, _cts.Token); } catch { return; }
        }
    }

    private void Pump()
    {
        if (!File.Exists(_path)) { _offset = 0; return; }

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset) _offset = 0;      // rotated/truncated
        if (fs.Length == _offset) return;

        fs.Seek(_offset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8, true, 8192, leaveOpen: true);

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var text = _stripAnsi ? Clean(line) : line.TrimEnd();
            if (text.Length > 0) _onLine(text);
        }
        _offset = fs.Position;
    }

    /// <summary>Strips ANSI escapes and the server's prompt noise.</summary>
    public static string Clean(string s)
    {
        s = AnsiRe().Replace(s, "");
        s = s.Replace("", "");
        s = PromptRe().Replace(s, "");
        return s.Trim();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\[\d*[KG]")]
    private static partial Regex AnsiRe();

    [GeneratedRegex(@"^>\s*")]
    private static partial Regex PromptRe();
}

/// <summary>Owns every tail for the current session and funnels them into AppState's log.</summary>
public sealed class LogHub : IDisposable
{
    private readonly AppState _state;
    private readonly List<LogTail> _tails = [];

    public LogHub(AppState state) => _state = state;

    public void Rebuild(AppConfig cfg)
    {
        Dispose();
        _tails.Clear();

        var serverLog = ServerConfig.LogPath(cfg);
        if (serverLog != null)
            _tails.Add(new LogTail(serverLog, "server",
                l => _state.Log(l, LogSource.Server, "server"), stripAnsi: true));

        for (var i = 0; i < Math.Max(1, cfg.Players.Count); i++)
        {
            if (!Instances.Exists(cfg, i)) continue;
            var idx = i;

            var game = System.IO.Path.Combine(Instances.CurrentProfile(cfg, i), "beamng.log");
            _tails.Add(new LogTail(game, $"P{idx} game",
                l => _state.Log(Shorten(l), LogSource.Game, $"P{idx} game")));

            var launcher = System.IO.Path.Combine(Instances.MpDir(cfg, i), "Launcher.log");
            _tails.Add(new LogTail(launcher, $"P{idx} mp",
                l => _state.Log(l, LogSource.Launcher, $"P{idx} mp")));

            var input = System.IO.Path.Combine(Instances.Bin64(cfg, i), "xinput_filter.log");
            _tails.Add(new LogTail(input, $"P{idx} pad",
                l => _state.Log(l, LogSource.Input, $"P{idx} pad")));
        }
    }

    /// <summary>BeamNG lines carry a long timestamp+level prefix; drop it for readability.</summary>
    private static string Shorten(string line)
    {
        var bar = line.IndexOf('|');
        if (bar <= 0 || bar > 14) return line;
        var parts = line.Split('|', 4);
        return parts.Length == 4 ? $"{parts[2]}: {parts[3].Trim()}" : line;
    }

    public void Dispose()
    {
        foreach (var t in _tails) t.Dispose();
    }
}

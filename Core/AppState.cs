using System.IO;

namespace BeamSplit.Core;

public enum LogSource { App, Server, Game, Launcher, Input }

public sealed record LogLine(DateTime When, LogSource Source, string Tag, string Text)
{
    public override string ToString() => $"{When:HH:mm:ss}  {Tag,-12} {Text}";
}

/// <summary>
/// Shared state for the whole app: the config every page edits, and the log stream the
/// console panel renders. Single instance, created at startup.
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public AppConfig Config { get; private set; } = new();

    private readonly List<LogLine> _log = [];
    private readonly Lock _gate = new();

    /// <summary>Raised for every new line. The console panel subscribes; so does the Setup log.</summary>
    public event Action<LogLine>? Logged;

    public void Load() => Config = ConfigStore.Load();
    public void Save() => ConfigStore.Save(Config);

    public void Log(string text, LogSource source = LogSource.App, string? tag = null)
    {
        var line = new LogLine(DateTime.Now, source, tag ?? source.ToString().ToLowerInvariant(), text);
        lock (_gate)
        {
            _log.Add(line);
            // keep memory bounded during long sessions
            if (_log.Count > 5000) _log.RemoveRange(0, 1000);
        }
        Logged?.Invoke(line);

        try { File.AppendAllText(Paths.LogFile, line + Environment.NewLine); } catch { }
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _log.ToList();
    }

    /// <summary>An IProgress that funnels Core progress reports into the log.</summary>
    public IProgress<string> Progress(LogSource source = LogSource.App, string? tag = null)
        => new Progress<string>(s => Log(s, source, tag));

    /// <summary>
    /// Progress reporter for work already running off the UI thread. Unlike
    /// Progress&lt;T&gt;, this does not capture or post back to WPF's dispatcher, so a noisy
    /// launcher cannot queue ahead of the cinematic's render callbacks.
    /// </summary>
    public IProgress<string> WorkerProgress(LogSource source = LogSource.App, string? tag = null)
        => new ImmediateProgress<string>(s => Log(s, source, tag));

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

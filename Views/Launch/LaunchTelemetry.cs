using System.Text.RegularExpressions;
using BeamSplit.Core;

namespace BeamSplit.Views.Launch;

/// <summary>How far along one player's pipeline is, as the film understands it.</summary>
public enum PaneState { Idle, Working, Ready, Failed }

/// <summary>
/// An immutable view of the launch, taken under the lock. The render path only ever
/// sees one of these, so it can never read a list that a launcher thread is mutating.
/// </summary>
public readonly record struct TelemetrySnapshot(
    string Stage,
    double Progress,
    bool Finished,
    bool Failed,
    string[] Lines,
    PaneState[] Panes);

/// <summary>
/// The only cross-thread surface in the film. <see cref="AppState.Logged"/> fires from the
/// per-player pipelines running under Task.WhenAll, so every field here is guarded.
/// </summary>
public sealed partial class LaunchTelemetry
{
    private const int MaxLines = 9;
    private const int MaxLineLength = 112;

    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];
    private readonly PaneState[] _panes;
    private string _stage = "INITIALIZING RIG";
    private double _progress;
    private bool _finished;
    private bool _failed;

    public LaunchTelemetry(int players)
    {
        _panes = new PaneState[Math.Clamp(players, 1, 4)];
    }

    public void Reset(string stage, double progress)
    {
        lock (_gate)
        {
            _lines.Clear();
            Array.Clear(_panes);
            _stage = stage;
            _progress = progress;
            _finished = false;
            _failed = false;
        }
    }

    public void Finish(bool failed, string stage)
    {
        lock (_gate)
        {
            _finished = true;
            _failed = failed;
            _stage = stage;
            if (!failed) _progress = 1;
            for (var i = 0; i < _panes.Length; i++)
                if (_panes[i] != PaneState.Failed)
                    _panes[i] = failed ? PaneState.Failed : PaneState.Ready;
        }
    }

    public void SetProgress(double value, string stage)
    {
        lock (_gate)
        {
            if (value > _progress) _progress = value;
            _stage = stage;
        }
    }

    public void AddLine(string line)
    {
        line = line.Replace('\r', ' ').Replace('\n', ' ');
        lock (_gate)
        {
            _lines.Add(line.Length > MaxLineLength ? line[..MaxLineLength] + "…" : line);
            if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_gate)
            return new TelemetrySnapshot(_stage, _progress, _finished, _failed,
                _lines.ToArray(), (PaneState[])_panes.Clone());
    }

    /// <summary>
    /// Route a log line into the film. Global keywords drive the stage caption; the
    /// per-player prefix the launcher already emits drives that pane's own light, which
    /// is what lets the animation show *which* player is up.
    /// </summary>
    public void Observe(LogLine line)
    {
        var text = line.Text.Trim();
        if (text.Length == 0) return;
        AddLine($"{line.When:HH:mm:ss}  {text}");

        var player = PlayerPrefix().Match(text);
        if (player.Success && int.TryParse(player.Groups[1].Value, out var index))
            SetPane(index, PaneFor(text));

        if (Has(text, "Building") || Has(text, "Ensure")) SetProgress(.16, "BUILDING DRIVER PROFILES");
        if (Has(text, "input") || Has(text, "proxy")) SetProgress(.30, "DEPLOYING INPUT ROUTES");
        if (Has(text, "server up")) SetProgress(.42, "LOCAL SERVER ONLINE");
        if (Has(text, "pipelines in parallel")) SetProgress(.50, "STARTING PLAYER PIPELINES");
        if (Has(text, "launcher ready")) SetProgress(.62, "LAUNCHERS SYNCHRONIZED");
        if (Has(text, "game pid")) SetProgress(.72, "GAME PROCESSES ACQUIRED");
        if (Has(text, "window acquired")) SetProgress(.84, "STABILIZING DISPLAYS");
        if (Has(text, "window(s) verified")) SetProgress(.96, "FINALIZING SESSION");
    }

    private static PaneState PaneFor(string text)
    {
        if (Has(text, "failed") || Has(text, "exited")) return PaneState.Failed;
        if (Has(text, "window acquired") || Has(text, "verified")) return PaneState.Ready;
        return PaneState.Working;
    }

    private void SetPane(int index, PaneState state)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _panes.Length) return;
            // Never walk a pane backwards on a stray later line, but a failure always wins.
            if (state == PaneState.Failed || state > _panes[index])
                _panes[index] = state;
        }
    }

    private static bool Has(string text, string term) =>
        text.Contains(term, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*P(\d+)\b")]
    private static partial Regex PlayerPrefix();
}

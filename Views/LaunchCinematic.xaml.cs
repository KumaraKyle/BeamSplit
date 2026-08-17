using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeamSplit.Core;
using BeamSplit.Views.Launch;
using SkiaSharp;

namespace BeamSplit.Views;

/// <summary>
/// Hosts the launch film. This control owns only the lifecycle — the bitmap, the render
/// clock, the skip keys and the task the caller awaits. Everything that draws lives in
/// <see cref="LaunchFilm"/>.
/// </summary>
public partial class LaunchCinematic : UserControl
{
    private readonly Stopwatch _clock = new();
    private TaskCompletionSource? _completion;
    private LaunchTelemetry? _telemetry;
    private LaunchFilm? _film;
    private WriteableBitmap? _frame;
    private SKSurface? _surface;
    private CancellationTokenSource? _previewCts;
    private bool _rendering;
    private double _frameCost;

    public LaunchCinematic()
    {
        InitializeComponent();
        BtnSkip.Click += (_, _) => Complete();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Space)
                Complete();
        };
    }

    public Task PlayAsync(int players, Task? launchTask = null)
    {
        Complete();
        players = Math.Clamp(players, 1, 4);

        _telemetry = new LaunchTelemetry(players);
        _telemetry.Reset("INITIALIZING RIG", .04);
        _telemetry.AddLine("BEAMSPLIT // SPLITTING ONE MACHINE");
        _telemetry.AddLine($"ROUTING {players:00} DRIVER CHANNEL{(players == 1 ? "" : "S")}...");

        // Resolved once: GetMonitors is a P/Invoke, and the geometry cannot change
        // meaningfully inside the film.
        _film = new LaunchFilm(players, LaunchLayout.Build(players), _telemetry);
        _frameCost = 0;

        _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Visibility = Visibility.Visible;
        Focus();
        AppState.Current.Logged += OnLaunchLog;
        _clock.Restart();
        CompositionTarget.Rendering += RenderFrame;
        _rendering = true;

        if (launchTask == null)
        {
            _previewCts = new CancellationTokenSource();
            _ = RunPreviewAsync(_previewCts.Token);
        }
        else
        {
            _ = WatchLaunchAsync(launchTask);
        }
        return _completion.Task;
    }

    public void Abort() => Complete();

    private async Task WatchLaunchAsync(Task launchTask)
    {
        try
        {
            await launchTask;
            _telemetry?.Finish(false, "ALL SYSTEMS READY");
        }
        catch
        {
            _telemetry?.Finish(true, "LAUNCH FAULT — OPEN CONSOLE");
        }
    }

    private async Task RunPreviewAsync(CancellationToken ct)
    {
        var stages = new (int Delay, double Progress, string Stage, string Line)[]
        {
            (600, .14, "BUILDING DRIVER PROFILES", "PROFILE MATRIX ............ OK"),
            (600, .28, "DEPLOYING INPUT ROUTES", "XINPUT ISOLATION .......... ARMED"),
            (600, .43, "STARTING LOCAL SERVER", "BEAMMP SERVER ............. LISTENING"),
            (600, .58, "STARTING PLAYER PIPELINES", "P0 / P1 LAUNCHERS ......... READY"),
            (600, .73, "WAITING FOR GAME WINDOWS", "GAME PROCESSES ............ ACQUIRED"),
            (600, .88, "STABILIZING DISPLAYS", "WINDOW GRID ............... LOCKED"),
            (600, .96, "FINALIZING SESSION", "SESSION HANDOFF ........... COMPLETE")
        };
        try
        {
            foreach (var item in stages)
            {
                await Task.Delay(item.Delay, ct);
                _telemetry?.SetProgress(item.Progress, item.Stage);
                _telemetry?.AddLine(item.Line);
            }

            // Hold the fake launch open long enough for one whole sustain loop, otherwise
            // the preview never shows the phase a real launch spends most of its time in.
            var hold = TimeSpan.FromSeconds(LaunchFilm.IntroSeconds + LaunchFilm.LoopSeconds)
                       - _clock.Elapsed;
            if (hold > TimeSpan.Zero) await Task.Delay(hold, ct);
            _telemetry?.Finish(false, "ALL SYSTEMS READY");
        }
        catch (OperationCanceledException) { }
    }

    private void OnLaunchLog(LogLine line) => _telemetry?.Observe(line);

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (!_rendering || _film == null || Visibility != Visibility.Visible)
            return;

        // Stretch="Fill" makes the surface size a free parameter, so render below native
        // and derive the height from the real aspect ratio - clamping the two axes
        // independently is what used to squash the frame.
        var aspect = ActualWidth > 1 ? ActualHeight / ActualWidth : 9d / 16d;
        var width = Math.Clamp((int)Math.Ceiling(ActualWidth), 640, 1280);
        var height = Math.Clamp((int)Math.Round(width * aspect), 360, 1200);

        if (_frame == null || _frame.PixelWidth != width || _frame.PixelHeight != height)
        {
            _surface?.Dispose();
            _surface = null;
            _frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            CinematicFrame.Source = _frame;
        }

        var started = _clock.Elapsed.TotalMilliseconds;
        DrawFrame(_frame, width, height, (float)_clock.Elapsed.TotalSeconds);

        // The film runs exactly when the machine is most contended - four game instances are
        // cold starting behind it. Shed detail rather than frames.
        _frameCost = _frameCost * .9 + (_clock.Elapsed.TotalMilliseconds - started) * .1;
        _film.Quality = _frameCost > 13 ? 0 : _frameCost > 9 ? 1 : 2;

        if (_film.Finished)
            Complete();
    }

    private void DrawFrame(WriteableBitmap bitmap, int width, int height, float seconds)
    {
        bitmap.Lock();
        try
        {
            if (_surface == null)
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                _surface = SKSurface.Create(info, bitmap.BackBuffer, bitmap.BackBufferStride);
                if (_surface == null) return;
            }
            _film!.Render(_surface.Canvas, width, height, seconds);
            _surface.Canvas.Flush();
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    private void Complete()
    {
        AppState.Current.Logged -= OnLaunchLog;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        if (_rendering)
        {
            CompositionTarget.Rendering -= RenderFrame;
            _rendering = false;
        }
        _clock.Stop();
        _surface?.Dispose();
        _surface = null;
        _film?.Dispose();
        _film = null;
        _telemetry = null;
        Visibility = Visibility.Collapsed;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult();
    }
}

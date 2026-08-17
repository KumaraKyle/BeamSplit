using SkiaSharp;

namespace BeamSplit.Views.Launch;

public enum Phase { Intro, Sustain, Resolve, Fault, Done }

/// <summary>
/// "The beam splits" — the launch film, as pure kinetic light.
///
/// One beam ignites, becomes the BeamSplit mark, splits into one beam per player, each beam
/// opens into a viewport, and on a successful launch the viewports fly into the rig's real
/// splitscreen geometry. That is the product, animated.
///
/// Two rules hold the whole thing together:
///
///  1. Every draw is a pure function of (phase, phase-local time). Nothing integrates state
///     frame to frame. The film runs precisely while two to four game instances are cold
///     starting, so frames *will* be dropped — integrated motion would visibly diverge, and
///     a resolve 200 seconds in would not look like a resolve 2 seconds in.
///  2. The sustain loop is built only from integer harmonics of its own phase, so it wraps
///     with matching value *and* velocity. There is no crossfade anywhere; the joins are
///     seamless by construction.
/// </summary>
public sealed class LaunchFilm : IDisposable
{
    public const float IntroSeconds = 4.2f;
    public const float LoopSeconds = 6.0f;
    public const float ResolveSeconds = 2.6f;
    public const float FaultSeconds = 2.4f;

    /// <summary>Everything is composed around this line, well clear of the telemetry floor.</summary>
    private const float FormationY = .385f;

    private readonly int _count;
    private readonly LaunchLayout _layout;
    private readonly LaunchTelemetry _telemetry;
    private readonly FilmPaints _paints = new();
    private readonly Pose[] _poses;
    private readonly PaneState[] _seen;
    private readonly float[] _changedAt;

    private Phase _phase = Phase.Intro;
    private float _phaseStart;
    private float _frozenU;
    private float _progressFrom = .04f;
    private float _progressTo = .04f;
    private float _progressChangedAt;
    private int _frame;

    /// <summary>2 = everything, 1 = no chromatic pass, 0 = no grain or dust either.</summary>
    public int Quality { get; set; } = 2;

    public bool Finished => _phase == Phase.Done;

    public LaunchFilm(int players, LaunchLayout layout, LaunchTelemetry telemetry)
    {
        _count = Math.Clamp(players, 1, 4);
        _layout = layout;
        _telemetry = telemetry;
        _poses = new Pose[_count];
        _seen = new PaneState[_count];
        _changedAt = new float[_count];
        for (var i = 0; i < _count; i++) _changedAt[i] = float.NegativeInfinity;
    }

    private struct Pose
    {
        public float Cx, Cy, W, H, Alpha, Energy, Motion;
        /// <summary>1 while this is still a solid logo bar, 0 once it has opened into a screen.</summary>
        public float Solid;
        public Tint Tint;
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    public void Render(SKCanvas canvas, int width, int height, float t)
    {
        _frame++;
        _paints.Resize(width, height);
        var snap = _telemetry.Snapshot();
        Advance(t, snap);
        if (_phase == Phase.Done) return;

        var pt = t - _phaseStart;
        var u = _phase == Phase.Sustain ? Fract(pt / LoopSeconds) : _frozenU;
        TrackProgress(snap, t);
        NotePaneChanges(snap, t);
        ComputePoses(width, height, t, pt, u, snap);

        _paints.DrawBackdrop(canvas);
        DrawHorizon(canvas, width, height, t);
        if (Quality > 0) DrawDust(canvas, width, height, u);

        DrawRays(canvas, width, height);
        for (var i = 0; i < _count; i++) DrawShaft(canvas, height, _poses[i]);
        if (_phase == Phase.Resolve) DrawVacancies(canvas, width, height, pt);
        for (var i = 0; i < _count; i++) DrawPane(canvas, i, u, t);

        DrawMark(canvas, width, height, t, pt);
        _paints.DrawScrim(canvas);
        DrawRibbon(canvas, width, height, u, t, snap);
        DrawTelemetry(canvas, width, height, u, snap);
        DrawGrade(canvas, width, height);
    }

    private void Advance(float t, TelemetrySnapshot snap)
    {
        var pt = t - _phaseStart;
        switch (_phase)
        {
            case Phase.Intro:
                // Never cut the intro short, even if the launch has already landed.
                if (pt >= IntroSeconds)
                    Enter(snap.Finished ? (snap.Failed ? Phase.Fault : Phase.Resolve) : Phase.Sustain, t);
                break;

            case Phase.Sustain:
                // Leaves at any point in the loop — no waiting for a boundary.
                if (snap.Finished)
                {
                    _frozenU = Fract(pt / LoopSeconds);
                    Enter(snap.Failed ? Phase.Fault : Phase.Resolve, t);
                }
                break;

            case Phase.Resolve when pt >= ResolveSeconds:
            case Phase.Fault when pt >= FaultSeconds:
                Enter(Phase.Done, t);
                break;
        }
    }

    private void Enter(Phase phase, float t)
    {
        _phase = phase;
        _phaseStart = t;
    }

    private void NotePaneChanges(TelemetrySnapshot snap, float t)
    {
        for (var i = 0; i < _count && i < snap.Panes.Length; i++)
        {
            if (snap.Panes[i] == _seen[i]) continue;
            _seen[i] = snap.Panes[i];
            _changedAt[i] = t;
        }
    }

    private void TrackProgress(TelemetrySnapshot snap, float t)
    {
        var target = (float)Math.Clamp(snap.Progress, .04, 1);
        if (MathF.Abs(target - _progressTo) < .0001f) return;

        // Retarget from the value currently on screen, not from the previous milestone.
        // Rapid launcher messages therefore remain one continuous movement instead of
        // restarting with a visible jump each time another pipeline reports progress.
        _progressFrom = DisplayProgress(t);
        _progressTo = MathF.Max(_progressFrom, target);
        _progressChangedAt = t;
    }

    private float DisplayProgress(float t) =>
        Mix(_progressFrom, _progressTo, Smooth(0, .62f, t - _progressChangedAt));

    // ── Pose ─────────────────────────────────────────────────────────────────

    private void ComputePoses(int width, int height, float t, float pt, float u, TelemetrySnapshot snap)
    {
        var seamW = width * .18f;
        var seamH = seamW * .18f;

        for (var i = 0; i < _count; i++)
        {
            Rest(i, width, height, out var rcx, out var rcy, out var rw, out var rh);
            ref var pose = ref _poses[i];
            pose.Tint = TintFor(i, snap);
            pose.Energy = EnergyFor(i, t, snap);
            pose.Solid = 0;

            switch (_phase)
            {
                case Phase.Intro:
                {
                    // Opens as a hairline and blooms into the bar, rather than fading a
                    // rectangle up out of nothing.
                    var ignite = Smooth(.35f, 1.20f, t);
                    var split = EaseOutCubic(Smooth(1.55f, 2.35f, t));
                    var grow = EaseOutCubic(Smooth(2.10f, 3.10f, t));
                    pose.Cx = Mix(width * .5f, rcx, split);
                    pose.Cy = Mix(height * FormationY, rcy, grow);
                    pose.W = Mix(seamW / _count * (.35f + .65f * ignite), rw, split);
                    pose.H = Mix(seamH * (.16f + .84f * ignite), rh, grow);
                    pose.Alpha = Smooth(.30f, .80f, t);
                    pose.Motion = Bell(split) * .8f + Bell(grow) * .5f;
                    // Solid while it is still the logo bar; hollows out as it opens.
                    pose.Solid = 1 - Smooth(1.90f, 2.70f, t);
                    pose.Tint = Tint.Accent;
                    break;
                }

                case Phase.Sustain:
                {
                    Breathe(i, u, width, height, out var dx, out var dy, out var scale);
                    pose.Cx = rcx + dx;
                    pose.Cy = rcy + dy;
                    pose.W = rw * scale;
                    pose.H = rh * scale;
                    pose.Alpha = 1;
                    pose.Motion = 0;
                    break;
                }

                case Phase.Resolve:
                {
                    // Damp the breathing from wherever it was, and fly to the real geometry.
                    // Position is continuous at pt = 0 by construction; the small velocity
                    // discontinuity is the "the launch just landed" beat.
                    var damp = 1 - Smooth(0, .35f, pt);
                    Breathe(i, u, width, height, out var dx, out var dy, out var scale);
                    var bx = rcx + dx * damp;
                    var by = rcy + dy * damp;
                    var bw = rw * Mix(1, scale, damp);
                    var bh = rh * Mix(1, scale, damp);

                    Target(i, width, height, out var tx, out var ty, out var tw, out var th);
                    var fly = EaseInOutCubic(Smooth(.20f, 1.30f, pt));
                    pose.Cx = Mix(bx, tx, fly);
                    pose.Cy = Mix(by, ty, fly);
                    pose.W = Mix(bw, tw, fly);
                    pose.H = Mix(bh, th, fly);
                    pose.Alpha = 1 - Smooth(1.60f, 2.40f, pt);
                    pose.Motion = Bell(fly);
                    pose.Energy = MathF.Max(pose.Energy, Punch(pt, 1.10f, .55f));
                    break;
                }

                default:
                {
                    // The beams literally un-split: back into the one seam they came from.
                    var damp = 1 - Smooth(0, .30f, pt);
                    Breathe(i, u, width, height, out var dx, out var dy, out _);
                    var collapse = EaseInOutCubic(Smooth(.15f, 1.15f, pt));
                    pose.Cx = Mix(rcx + dx * damp, width * .5f, collapse);
                    pose.Cy = Mix(rcy + dy * damp, height * FormationY, collapse);
                    pose.W = Mix(rw, seamW / _count, collapse);
                    pose.H = Mix(rh, seamH, collapse);
                    pose.Tint = Tint.Fault;
                    pose.Alpha = 1 - Smooth(1.95f, 2.40f, pt);
                    pose.Motion = Bell(collapse) * .6f;
                    pose.Energy = .35f;
                    break;
                }
            }
        }
    }

    /// <summary>The formation the intro settles into and the loop breathes around.</summary>
    private void Rest(int i, float width, float height, out float cx, out float cy, out float w, out float h)
    {
        w = width * .150f;
        h = w * .5625f;
        cx = width * .5f + (i - (_count - 1) * .5f) * width * .185f;
        cy = height * FormationY + MathF.Sin(i * 1.7f) * height * .014f;
    }

    /// <summary>
    /// Sustain motion. Built only from sin/cos of whole multiples of the loop phase, so
    /// f(0) = f(1) and f'(0) = f'(1) — the wrap has no seam, not even a velocity pop. And
    /// because sin(0) = 0, the loop at u = 0 *is* the rest pose, so the intro joins it
    /// without a crossfade.
    /// </summary>
    private static void Breathe(int i, float u, float width, float height,
        out float dx, out float dy, out float scale)
    {
        var a = MathF.Tau * u;
        var yPhase = i * 1.31f;
        var xPhase = i * .70f;
        var scalePhase = i * 1.10f;

        // Subtract each pane's phase-offset value at u=0. The integer harmonics still
        // wrap with matching velocity, while every pane now evaluates to the exact rest
        // pose at both ends of the loop. Without this, Intro -> Sustain visibly popped.
        dy = (MathF.Sin(a + yPhase) - MathF.Sin(yPhase)) * height * .013f;
        dx = (MathF.Cos(a * 2 + xPhase) - MathF.Cos(xPhase)) * width * .0045f;
        scale = 1 + (MathF.Sin(a * 2 + scalePhase) - MathF.Sin(scalePhase)) * .015f;
    }

    private void Target(int i, float width, float height, out float cx, out float cy, out float w, out float h)
    {
        var box = FitBox(width, height);
        var n = i < _layout.Panes.Count ? _layout.Panes[i] : new NormRect(0, 0, 1, 1);
        cx = box.Left + n.Cx * box.Width;
        cy = box.Top + n.Cy * box.Height;
        w = n.W * box.Width * .965f;
        h = n.H * box.Height * .965f;
    }

    private SKRect FitBox(float width, float height)
    {
        var boxW = width * .70f;
        var boxH = boxW / _layout.Aspect;
        var maxH = height * .46f;
        if (boxH > maxH)
        {
            boxH = maxH;
            boxW = boxH * _layout.Aspect;
        }
        var cx = width * .5f;
        var cy = height * FormationY;
        return new SKRect(cx - boxW * .5f, cy - boxH * .5f, cx + boxW * .5f, cy + boxH * .5f);
    }

    private Tint TintFor(int i, TelemetrySnapshot snap)
    {
        if (_phase == Phase.Fault) return Tint.Fault;
        var state = i < snap.Panes.Length ? snap.Panes[i] : PaneState.Idle;
        return state switch
        {
            PaneState.Ready => Tint.Ready,
            PaneState.Failed => Tint.Fault,
            PaneState.Working => Tint.Accent,
            _ => Tint.Grey
        };
    }

    private float EnergyFor(int i, float t, TelemetrySnapshot snap)
    {
        var state = i < snap.Panes.Length ? snap.Panes[i] : PaneState.Idle;
        var baseline = state switch
        {
            PaneState.Ready => .85f,
            PaneState.Working => .55f,
            PaneState.Failed => .5f,
            _ => .22f
        };
        // A pane that is still waiting flickers; one that has locked is steady.
        if (state is PaneState.Idle or PaneState.Working)
            baseline += MathF.Sin(t * 6.1f + i * 2.3f) * .06f;
        return baseline + Punch(t, _changedAt[i], .55f) * .9f;
    }

    // ── Light ────────────────────────────────────────────────────────────────

    private void DrawHorizon(SKCanvas canvas, int width, int height, float t)
    {
        var lift = Smooth(0, .9f, t) * .5f;
        _paints.DrawGlow(canvas, Tint.Accent, width * .5f, height * FormationY,
            width * .62f, height * .30f, .07f * lift);
    }

    /// <summary>The lateral rays that carry each pane out of the seam during the split.</summary>
    private void DrawRays(SKCanvas canvas, int width, int height)
    {
        for (var i = 0; i < _count; i++)
        {
            ref var pose = ref _poses[i];
            var reach = pose.Cx - width * .5f;
            if (pose.Motion <= .02f || MathF.Abs(reach) < width * .004f) continue;
            _paints.DrawGlow(canvas, pose.Tint,
                width * .5f + reach * .5f, pose.Cy,
                MathF.Abs(reach) * .62f + pose.W * .3f,
                pose.H * .55f,
                pose.Motion * .34f * pose.Alpha);
        }
    }

    /// <summary>The vertical column of light each pane sits in. Soft, wide, cheap.</summary>
    private void DrawShaft(SKCanvas canvas, int height, in Pose pose)
    {
        if (pose.Alpha <= .004f) return;
        var lit = pose.Alpha * (.5f + pose.Energy * .5f);
        _paints.DrawGlow(canvas, pose.Tint, pose.Cx, pose.Cy,
            pose.W * .95f, height * .46f, .09f * lit);
        _paints.DrawGlow(canvas, pose.Tint, pose.Cx, pose.Cy,
            pose.W * .38f, height * .34f, .13f * lit);
        if (Quality > 1)
            _paints.DrawGlow(canvas, Tint.Cream, pose.Cx, pose.Cy,
                pose.W * .12f, height * .24f, .10f * lit);
    }

    private void DrawPane(SKCanvas canvas, int i, float u, float t)
    {
        ref var pose = ref _poses[i];
        if (pose.Alpha <= .004f || pose.W <= 0 || pose.H <= 0) return;

        var rect = new SKRect(pose.Cx - pose.W * .5f, pose.Cy - pose.H * .5f,
                              pose.Cx + pose.W * .5f, pose.Cy + pose.H * .5f);
        var radius = MathF.Min(8f, pose.H * .18f);
        var colour = FilmPaints.Colour(pose.Tint);
        var alpha = pose.Alpha;

        // Interior first, so the edge light reads as sitting on a dark screen. While the
        // frame is still the logo mark, that interior is the solid accent bar instead.
        _paints.Fill.Color = FilmPaints.Ink.WithAlpha((byte)(196 * alpha));
        canvas.DrawRoundRect(rect, radius, radius, _paints.Fill);
        if (pose.Solid > .004f)
        {
            _paints.Fill.Color = colour.WithAlpha((byte)(255 * alpha * pose.Solid));
            canvas.DrawRoundRect(rect, radius, radius, _paints.Fill);
        }

        // Chromatic fringing scaled by how fast the pane is actually moving, so it appears
        // during the split and the resolve and vanishes in the loop — a lens artefact,
        // not a filter.
        if (Quality > 1 && pose.Motion > .04f)
        {
            var shift = pose.Motion * pose.W * .022f;
            _paints.Stroke.StrokeWidth = 2f;
            _paints.Stroke.Color = new SKColor(255, 120, 40, (byte)(70 * alpha * pose.Motion));
            canvas.DrawRoundRect(rect with { Left = rect.Left - shift, Right = rect.Right - shift },
                radius, radius, _paints.Stroke);
            _paints.Stroke.Color = new SKColor(60, 150, 255, (byte)(70 * alpha * pose.Motion));
            canvas.DrawRoundRect(rect with { Left = rect.Left + shift, Right = rect.Right + shift },
                radius, radius, _paints.Stroke);
        }

        _paints.Stroke.StrokeWidth = 2f;
        _paints.Stroke.Color = colour.WithAlpha((byte)(255 * alpha * Math.Clamp(.45f + pose.Energy * .55f, 0, 1)));
        canvas.DrawRoundRect(rect, radius, radius, _paints.Stroke);

        // Light spilling off the corners.
        var spill = pose.W * .30f;
        var corner = .16f * alpha * (.4f + pose.Energy * .6f);
        _paints.DrawGlow(canvas, pose.Tint, rect.Left, rect.Top, spill, spill, corner);
        _paints.DrawGlow(canvas, pose.Tint, rect.Right, rect.Top, spill, spill, corner);
        _paints.DrawGlow(canvas, pose.Tint, rect.Left, rect.Bottom, spill, spill, corner);
        _paints.DrawGlow(canvas, pose.Tint, rect.Right, rect.Bottom, spill, spill, corner);

        // A pane still working scans; one that has locked holds a steady bar.
        var state = i < _seen.Length ? _seen[i] : PaneState.Idle;
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, radius, radius), SKClipOperation.Intersect, true);
        if (state is PaneState.Idle or PaneState.Working)
        {
            var y = rect.Top + Fract(u + i * .21f) * rect.Height;
            _paints.Additive.Color = colour.WithAlpha((byte)(90 * alpha));
            canvas.DrawRect(rect.Left, y, rect.Width, MathF.Max(1.5f, pose.H * .012f), _paints.Additive);
        }
        else
        {
            _paints.Additive.Color = colour.WithAlpha((byte)(60 * alpha));
            canvas.DrawRect(rect.Left, pose.Cy - pose.H * .006f, rect.Width,
                MathF.Max(1.5f, pose.H * .012f), _paints.Additive);
        }
        canvas.Restore();

        var flash = Punch(t, _changedAt[i], .45f);
        if (flash > .01f)
            _paints.DrawGlow(canvas, pose.Tint, pose.Cx, pose.Cy,
                pose.W * .85f, pose.H * .95f, flash * .45f * alpha);
    }

    /// <summary>
    /// Regions of the split no player fills — three players in a quad grid. Shown as a dim
    /// outline so the geometry explains itself instead of looking like a missing pane.
    /// </summary>
    private void DrawVacancies(SKCanvas canvas, int width, int height, float pt)
    {
        if (_layout.Vacant.Count == 0) return;
        var fly = EaseInOutCubic(Smooth(.20f, 1.30f, pt));
        var alpha = fly * (1 - Smooth(1.60f, 2.40f, pt)) * .30f;
        if (alpha <= .01f) return;

        var box = FitBox(width, height);
        _paints.Stroke.StrokeWidth = 1.5f;
        _paints.Stroke.Color = FilmPaints.Grey.WithAlpha((byte)(255 * alpha));
        foreach (var n in _layout.Vacant)
        {
            var rect = new SKRect(
                box.Left + n.X * box.Width, box.Top + n.Y * box.Height,
                box.Left + (n.X + n.W) * box.Width, box.Top + (n.Y + n.H) * box.Height);
            rect.Inflate(-box.Width * .008f, -box.Height * .008f);
            canvas.DrawRoundRect(rect, 6, 6, _paints.Stroke);
        }
    }

    /// <summary>
    /// The BeamSplit mark itself: two offset bars. The intro passes through it on the way to
    /// splitting, and the resolve reassembles it out of the settled grid.
    /// </summary>
    private void DrawMark(SKCanvas canvas, int width, int height, float t, float pt)
    {
        var seamW = width * .18f;
        var seamH = seamW * .18f;
        var cx = width * .5f;
        var cy = height * FormationY;

        if (_phase == Phase.Intro)
        {
            // The panes are still stacked into one bar here; this is the second one.
            var mark = Smooth(1.00f, 1.55f, t) * (1 - Smooth(1.55f, 2.05f, t));
            if (mark <= .004f) return;
            var bar = new SKRect(
                cx - seamW * .5f + seamW * .25f, cy + seamH * .83f,
                cx + seamW * .5f + seamW * .25f, cy + seamH * 1.83f);
            _paints.Fill.Color = FilmPaints.Grey.WithAlpha((byte)(255 * mark));
            canvas.DrawRoundRect(bar, seamH * .25f, seamH * .25f, _paints.Fill);
            return;
        }

        if (_phase != Phase.Resolve) return;
        var logo = Smooth(1.65f, 2.15f, pt) * (1 - Smooth(2.35f, 2.60f, pt));
        if (logo <= .004f) return;

        var w = width * .195f;
        var h = w * .18f;
        var top = cy - h * 1.25f;
        _paints.Fill.Color = FilmPaints.Accent.WithAlpha((byte)(255 * logo));
        canvas.DrawRoundRect(new SKRect(cx - w * .62f, top, cx + w * .38f, top + h), h * .28f, h * .28f, _paints.Fill);
        _paints.Fill.Color = FilmPaints.Grey.WithAlpha((byte)(255 * logo));
        canvas.DrawRoundRect(new SKRect(cx - w * .38f, top + h * 1.5f, cx + w * .62f, top + h * 2.5f),
            h * .28f, h * .28f, _paints.Fill);
        _paints.DrawGlow(canvas, Tint.Accent, cx, cy, w * 1.1f, h * 3.4f, logo * .22f);
        _paints.DrawLabel(canvas, "BEAMSPLIT", cx, top + h * 4.4f, width * .019f,
            FilmPaints.Fg.WithAlpha((byte)(235 * logo)), tracking: .38f);
    }

    private void DrawDust(SKCanvas canvas, int width, int height, float u)
    {
        var motes = Quality > 1 ? 26 : 13;
        for (var i = 0; i < motes; i++)
        {
            // Born and dying at zero alpha, so wrapping a one-way traveller is invisible.
            var travel = Fract(u + i * .0917f);
            var envelope = MathF.Sin(MathF.PI * travel);
            var x = width * (.10f + Fract(i * .3819f) * .80f);
            var y = height * (.72f - travel * .52f);
            var size = width * (.0012f + Fract(i * .7331f) * .0016f);
            _paints.DrawGlow(canvas, i % 5 == 0 ? Tint.Cream : Tint.Accent,
                x, y, size * 5, size * 5, envelope * .11f);
        }
    }

    // ── Readout ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Progress, with no chrome around it: the ribbon of light under the formation *is* the
    /// bar. The travelling highlight keeps a stalled stage alive without faking motion.
    /// </summary>
    private void DrawRibbon(SKCanvas canvas, int width, int height, float u, float t, TelemetrySnapshot snap)
    {
        var y = height * .668f;
        var left = width * .26f;
        var span = width * .48f;
        var progress = DisplayProgress(t);
        var tint = snap.Failed ? Tint.Fault : snap.Finished ? Tint.Ready : Tint.Accent;
        var colour = FilmPaints.Colour(tint);

        _paints.Fill.Color = FilmPaints.Grey.WithAlpha(70);
        canvas.DrawRect(left, y, span, 1.5f, _paints.Fill);

        var lit = span * progress;
        _paints.Additive.Color = colour.WithAlpha(230);
        canvas.DrawRect(left, y - .5f, lit, 2.5f, _paints.Additive);
        _paints.DrawGlow(canvas, tint, left + lit * .5f, y, lit * .55f, height * .022f, .34f);
        _paints.DrawGlow(canvas, tint, left + lit, y, width * .012f, height * .012f, .55f);

        if (!snap.Finished && lit > 1)
        {
            var travel = Fract(u * 1.5f);
            _paints.DrawGlow(canvas, Tint.Cream, left + lit * travel, y,
                span * .06f, height * .014f, MathF.Sin(MathF.PI * travel) * .40f);
        }
    }

    private void DrawTelemetry(SKCanvas canvas, int width, int height, float u, TelemetrySnapshot snap)
    {
        var stageColour = snap.Failed ? FilmPaints.Fault : FilmPaints.Fg;
        _paints.DrawLabel(canvas, snap.Stage, width * .5f, height * .718f,
            width * .0105f, stageColour.WithAlpha(240), tracking: .26f);

        if (snap.Failed)
            _paints.DrawLabel(canvas, "LAUNCH FAULT — OPEN CONSOLE", width * .5f, height * .752f,
                width * .0088f, FilmPaints.Fault.WithAlpha(225), tracking: .22f);

        var size = MathF.Max(12f, width * .0080f);
        var step = size * 1.62f;
        var x = width * .075f;
        var lines = snap.Lines;
        var shown = Math.Min(4, lines.Length);
        for (var k = 0; k < shown; k++)
        {
            var line = lines[lines.Length - 1 - k];
            var drift = MathF.Sin(MathF.Tau * u + k * .8f) * height * .0016f;
            var fade = (byte)(190 - k * 40);
            _paints.DrawLabel(canvas, line, x, height * .952f - k * step + drift, size,
                FilmPaints.Muted.WithAlpha(fade), tracking: 0, bold: false, mono: true,
                align: SKTextAlign.Left);
        }
    }

    private void DrawGrade(SKCanvas canvas, int width, int height)
    {
        if (Quality > 0) _paints.DrawGrain(canvas, _frame, 16);
        _paints.DrawScanlines(canvas);
        _paints.DrawVignette(canvas);
    }

    // ── Maths ────────────────────────────────────────────────────────────────

    public static float Smooth(float start, float end, float value)
    {
        if (end <= start) return value >= end ? 1 : 0;
        var x = Math.Clamp((value - start) / (end - start), 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>A hit at <paramref name="at"/> decaying over <paramref name="fall"/> seconds.</summary>
    public static float Punch(float t, float at, float fall)
    {
        if (float.IsNegativeInfinity(at) || t < at || t > at + fall) return 0;
        var x = (t - at) / fall;
        return (1 - x) * (1 - x);
    }

    /// <summary>Peaks in the middle of a 0..1 ramp — how fast something is moving.</summary>
    public static float Bell(float x) => 4 * x * (1 - x);

    public static float Fract(float x) => x - MathF.Floor(x);

    public static float Mix(float a, float b, float k) => a + (b - a) * k;

    public static float EaseOutCubic(float x) => 1 - MathF.Pow(1 - x, 3);

    public static float EaseInOutCubic(float x) =>
        x < .5f ? 4 * x * x * x : 1 - MathF.Pow(-2 * x + 2, 3) / 2;

    public void Dispose() => _paints.Dispose();
}

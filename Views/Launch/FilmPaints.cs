using SkiaSharp;

namespace BeamSplit.Views.Launch;

/// <summary>The film's whole palette. Drawn from Theme.xaml so the film matches the app.</summary>
public enum Tint { Accent, Warm, Cream, Grey, Ready, Fault }

/// <summary>
/// Every native object the film draws with, allocated once and mutated in place.
///
/// The old film built a dozen SKPaints and several shaders on every single frame, and a new
/// SKFont for every string. Nothing here is allocated inside the render loop: light comes
/// from pre-tinted glow sprites rather than per-frame radial gradients, and grain and
/// scanlines are repeating image shaders rather than hundreds of DrawRect calls.
/// </summary>
public sealed class FilmPaints : IDisposable
{
    public static readonly SKColor Ink = new(0x0B, 0x0D, 0x12);
    public static readonly SKColor Accent = new(0xFF, 0x7A, 0x2F);
    public static readonly SKColor Warm = new(0xFF, 0xB0, 0x5C);
    public static readonly SKColor Cream = new(0xFF, 0xF0, 0xC8);
    public static readonly SKColor Grey = new(0x3B, 0x42, 0x52);
    public static readonly SKColor Ready = new(0x3F, 0xD1, 0x7A);
    public static readonly SKColor Fault = new(0xE5, 0x54, 0x4B);
    public static readonly SKColor Fg = new(0xE6, 0xE9, 0xEF);
    public static readonly SKColor Muted = new(0x8B, 0x93, 0xA7);

    private const int GlowSize = 96;
    private const int NoiseSize = 128;
    private const int NoiseFrames = 4;

    public readonly SKPaint Fill = new() { IsAntialias = true };
    public readonly SKPaint Stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    public readonly SKPaint Glow = new() { BlendMode = SKBlendMode.Plus, IsAntialias = false };
    public readonly SKPaint Additive = new() { BlendMode = SKBlendMode.Plus, IsAntialias = true };
    public readonly SKPaint Flat = new() { IsAntialias = false };
    public readonly SKPaint Text = new() { IsAntialias = true };

    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly SKImage[] _glows;
    private readonly SKImage[] _noise;
    private readonly List<SKBitmap> _sources = [];
    private readonly Dictionary<(bool Bold, bool Mono, int Half), SKFont> _fonts = [];
    private readonly SKPaint[] _noisePaints;
    private readonly SKPaint _scanPaint;
    private SKPaint? _backdrop;
    private SKPaint? _vignette;
    private SKPaint? _scrim;
    private int _width, _height;

    public FilmPaints()
    {
        _glows =
        [
            MakeGlow(Accent), MakeGlow(Warm), MakeGlow(Cream),
            MakeGlow(Grey), MakeGlow(Ready), MakeGlow(Fault)
        ];

        var random = new Random(0x8EA5);
        _noise = new SKImage[NoiseFrames];
        for (var i = 0; i < NoiseFrames; i++) _noise[i] = MakeNoise(random);

        _noisePaints = new SKPaint[NoiseFrames];
        for (var i = 0; i < NoiseFrames; i++)
        {
            _noisePaints[i] = new SKPaint
            {
                IsAntialias = false,
                Shader = SKShader.CreateImage(_noise[i], SKShaderTileMode.Repeat, SKShaderTileMode.Repeat)
            };
        }
        _scanPaint = new SKPaint { IsAntialias = false, Shader = MakeScanlines() };
    }

    public static SKColor Colour(Tint tint) => tint switch
    {
        Tint.Accent => Accent,
        Tint.Warm => Warm,
        Tint.Cream => Cream,
        Tint.Grey => Grey,
        Tint.Ready => Ready,
        _ => Fault
    };

    /// <summary>Rebuild only the caches that depend on frame size.</summary>
    public void Resize(int width, int height)
    {
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;

        _backdrop?.Dispose();
        _backdrop = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, height),
                [Ink, new SKColor(0x12, 0x16, 0x22), Ink],
                [0, .48f, 1], SKShaderTileMode.Clamp)
        };

        // Fade to a transparent version of the SAME colour, never SKColors.Transparent -
        // that constant is transparent *white*, and the gradient interpolates through it,
        // so a darkening pass silently turns into a grey wash.
        _vignette?.Dispose();
        _vignette = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(width * .5f, height * .42f), width * .72f,
                [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(40), SKColors.Black.WithAlpha(190)],
                [0, .6f, 1], SKShaderTileMode.Clamp)
        };

        // One soft floor behind the telemetry, so type never sits on an additive hotspot.
        _scrim?.Dispose();
        _scrim = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, height * ScrimTop), new SKPoint(0, height),
                [Ink.WithAlpha(0), Ink.WithAlpha(232)],
                [0, 1], SKShaderTileMode.Clamp)
        };
    }

    public const float ScrimTop = .62f;

    public void DrawBackdrop(SKCanvas canvas) => canvas.DrawRect(0, 0, _width, _height, _backdrop!);

    public void DrawVignette(SKCanvas canvas) => canvas.DrawRect(0, 0, _width, _height, _vignette!);

    public void DrawScrim(SKCanvas canvas) =>
        canvas.DrawRect(0, _height * ScrimTop, _width, _height * (1 - ScrimTop), _scrim!);

    public void DrawScanlines(SKCanvas canvas) => canvas.DrawRect(0, 0, _width, _height, _scanPaint);

    /// <summary>
    /// One full-screen pass of animated grain. The shader is anchored in canvas space, so
    /// translating the canvas slides the pattern without rebuilding anything.
    /// </summary>
    public void DrawGrain(SKCanvas canvas, int frame, byte alpha)
    {
        var paint = _noisePaints[frame % NoiseFrames];
        paint.Color = SKColors.White.WithAlpha(alpha);
        var dx = frame * 37 % NoiseSize;
        var dy = frame * 53 % NoiseSize;
        canvas.Save();
        canvas.Translate(dx, dy);
        canvas.DrawRect(-dx, -dy, _width, _height, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Soft light of any size or shape. Replaces every per-frame radial gradient: the
    /// falloff lives in a pre-tinted sprite, so this is a single blit.
    /// </summary>
    public void DrawGlow(SKCanvas canvas, Tint tint, float cx, float cy, float halfW, float halfH, float alpha)
    {
        if (alpha <= .004f || halfW <= 0 || halfH <= 0) return;
        Glow.Color = SKColors.White.WithAlpha((byte)(Math.Clamp(alpha, 0, 1) * 255));
        canvas.DrawImage(_glows[(int)tint],
            new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH), Linear, Glow);
    }

    // ── Typography ───────────────────────────────────────────────────────────

    public SKFont Font(float size, bool bold, bool mono)
    {
        var half = (int)MathF.Round(Math.Max(13f, size) * 2);
        var key = (bold, mono, half);
        if (_fonts.TryGetValue(key, out var cached)) return cached;
        var typeface = SKTypeface.FromFamilyName(mono ? "Consolas" : "Segoe UI",
            bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        var font = new SKFont(typeface, half / 2f);
        _fonts[key] = font;
        return font;
    }

    /// <summary>
    /// Every caption goes through here so tracking and weight stay consistent, rather than
    /// some labels padding themselves with literal spaces.
    /// </summary>
    public void DrawLabel(SKCanvas canvas, string text, float x, float baseline, float size,
        SKColor colour, float tracking = .18f, bool bold = true, bool mono = false,
        SKTextAlign align = SKTextAlign.Center)
    {
        if (text.Length == 0 || colour.Alpha == 0) return;
        var font = Font(size, bold, mono);
        Text.Color = colour;

        if (tracking <= 0)
        {
            canvas.DrawText(text, x, baseline, align, font, Text);
            return;
        }

        var extra = font.Size * tracking;
        var total = Measure(font, text, extra);
        var cursor = align switch
        {
            SKTextAlign.Center => x - total * .5f,
            SKTextAlign.Right => x - total,
            _ => x
        };
        foreach (var character in text)
        {
            var glyph = GlyphOf(character);
            canvas.DrawText(glyph, cursor, baseline, SKTextAlign.Left, font, Text);
            cursor += font.MeasureText(glyph) + extra;
        }
    }

    public float MeasureLabel(string text, float size, float tracking, bool bold, bool mono)
    {
        var font = Font(size, bold, mono);
        return tracking <= 0 ? font.MeasureText(text) : Measure(font, text, font.Size * tracking);
    }

    private static float Measure(SKFont font, string text, float extra)
    {
        var total = 0f;
        foreach (var character in text) total += font.MeasureText(GlyphOf(character)) + extra;
        return total - extra;
    }

    // Tracked text draws a character at a time every frame; this keeps that from
    // allocating a string per glyph per frame.
    private static readonly string[] Glyphs = BuildGlyphs();

    private static string[] BuildGlyphs()
    {
        var glyphs = new string[128];
        for (var i = 0; i < glyphs.Length; i++) glyphs[i] = ((char)i).ToString();
        return glyphs;
    }

    private static string GlyphOf(char character) =>
        character < 128 ? Glyphs[character] : character.ToString();

    // ── Sprite construction ──────────────────────────────────────────────────

    private static SKImage MakeGlow(SKColor tint)
    {
        var info = new SKImageInfo(GlowSize, GlowSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        // A hot core with a long tail reads as light; a linear ramp reads as a circle.
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(GlowSize * .5f, GlowSize * .5f), GlowSize * .5f,
            [tint, tint.WithAlpha(150), tint.WithAlpha(38), tint.WithAlpha(0)],
            [0, .28f, .62f, 1], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        surface.Canvas.DrawRect(0, 0, GlowSize, GlowSize, paint);
        return surface.Snapshot();
    }

    private SKImage MakeNoise(Random random)
    {
        var pixels = new SKColor[NoiseSize * NoiseSize];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new SKColor(255, 255, 255, (byte)random.Next(0, 256));
        var bitmap = new SKBitmap(NoiseSize, NoiseSize, SKColorType.Bgra8888, SKAlphaType.Unpremul)
        {
            Pixels = pixels
        };
        _sources.Add(bitmap);
        return SKImage.FromBitmap(bitmap);
    }

    private SKShader MakeScanlines()
    {
        var bitmap = new SKBitmap(1, 3, SKColorType.Bgra8888, SKAlphaType.Unpremul)
        {
            Pixels =
            [
                SKColors.Black.WithAlpha(0),
                SKColors.Black.WithAlpha(0),
                SKColors.Black.WithAlpha(30)
            ]
        };
        _sources.Add(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        return SKShader.CreateImage(image, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
    }

    public void Dispose()
    {
        Fill.Dispose(); Stroke.Dispose(); Glow.Dispose();
        Additive.Dispose(); Flat.Dispose(); Text.Dispose();
        foreach (var paint in _noisePaints)
        {
            paint.Shader?.Dispose();
            paint.Dispose();
        }
        _scanPaint.Shader?.Dispose();
        _scanPaint.Dispose();
        _backdrop?.Dispose(); _vignette?.Dispose(); _scrim?.Dispose();
        foreach (var glow in _glows) glow.Dispose();
        foreach (var noise in _noise) noise.Dispose();
        foreach (var source in _sources) source.Dispose();
        foreach (var font in _fonts.Values) font.Dispose();
        _fonts.Clear();
    }
}

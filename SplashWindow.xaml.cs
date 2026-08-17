using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BeamSplit;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayIntro();
    }

    /// <summary>Sets the line of text under the wordmark. Safe from any thread.</summary>
    public void SetStep(string text) => Dispatcher.Invoke(() => LblStep.Text = text);

    private void PlayIntro()
    {
        // window fade-in
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        // the two bars slide in from opposite sides and settle offset from each other
        SlideBar(Bar1, -70, 0, 0);
        SlideBar(Bar2, 70, 0, 90);

        // indeterminate sweep across the progress track
        var sweep = new DoubleAnimation(-140, 520, TimeSpan.FromMilliseconds(1150))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SweepT.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private static void SlideBar(UIElement bar, double fromX, double toX, int delayMs)
    {
        var tt = new TranslateTransform(fromX, 0);
        bar.RenderTransform = tt;

        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
        bar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = begin });
    }

    /// <summary>Fades out, then closes. Awaited by App so the handoff is smooth.</summary>
    public Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { Close(); tcs.SetResult(); };
        BeginAnimation(OpacityProperty, fade);
        return tcs.Task;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BeamSplit.Views;

public partial class AppTourOverlay : UserControl
{
    public event Action? Next;
    public event Action? Back;
    public event Action? Close;

    public AppTourOverlay()
    {
        InitializeComponent();
        BtnTourNext.Click += (_, _) => Next?.Invoke();
        BtnTourBack.Click += (_, _) => Back?.Invoke();
        BtnTourClose.Click += (_, _) => Close?.Invoke();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close?.Invoke();
            else if (e.Key is System.Windows.Input.Key.Right or System.Windows.Input.Key.Enter) Next?.Invoke();
            else if (e.Key == System.Windows.Input.Key.Left) Back?.Invoke();
        };
    }

    public void ShowStep(int index, int count, string eyebrow, string title, string body, string tip)
    {
        Visibility = Visibility.Visible;
        Focus();
        LblTourCount.Text = $"{index + 1:00} / {count:00}";
        LblTourEyebrow.Text = eyebrow.ToUpperInvariant();
        LblTourTitle.Text = title;
        LblTourBody.Text = body;
        LblTourTip.Text = tip;
        TourProgress.Maximum = count;
        TourProgress.BeginAnimation(ProgressBar.ValueProperty,
            new DoubleAnimation(TourProgress.Value, index + 1, TimeSpan.FromMilliseconds(360))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        BtnTourBack.IsEnabled = index > 0;
        BtnTourNext.Content = index == count - 1 ? "Finish tour ✓" : "Next ›";
        TourCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
        ((TranslateTransform)TourCard.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}

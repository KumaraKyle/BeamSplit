using System.Windows;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class MemoryAdviceDialog : Window
{
    public MemoryAdviceDialog(MemoryAdvice advice)
    {
        InitializeComponent();
        LblHeadline.Text = $"{advice.MapName} with {advice.Players} BeamNG instances is a heavy combination.";
        LblMemory.Text = $"Installed RAM  {advice.TotalMemoryMb / 1024d:0.0} GB\nFree right now  {advice.AvailableMemoryMb / 1024d:0.0} GB";
        LblReason.Text = advice.Reason;
        BtnEnable.Click += (_, _) => { DialogResult = true; Close(); };
        BtnContinue.Click += (_, _) => { DialogResult = false; Close(); };
    }
}

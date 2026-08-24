using System.Windows;
using BeamSplit.Core;

namespace BeamSplit.Views;

public partial class SupportPromptDialog : Window
{
    public SupportPromptChoice Choice { get; private set; } = SupportPromptChoice.Later;

    public SupportPromptDialog()
    {
        InitializeComponent();
        BtnSupport.Click += (_, _) => Finish(SupportPromptChoice.Support);
        BtnLater.Click += (_, _) => Finish(SupportPromptChoice.Later);
        BtnNever.Click += (_, _) => Finish(SupportPromptChoice.Never);
    }

    private void Finish(SupportPromptChoice choice)
    {
        Choice = choice;
        Close();
    }
}


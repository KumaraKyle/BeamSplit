using System.Diagnostics;

namespace BeamSplit.Core;

public enum SupportPromptChoice { Support, Later, Never }

/// <summary>
/// Keeps optional project support respectful and local. BeamSplit does not embed a
/// checkout, receive payment data, or attempt to determine whether somebody paid.
/// </summary>
public static class SupportPrompt
{
    public const string Url = "https://ko-fi.com/kumarakyle";
    public const int SuccessfulLaunchesBeforePrompt = 3;
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromDays(30);

    public static bool ShouldShow(AppConfig cfg, DateTime utcNow) =>
        !cfg.SupportPromptNeverAgain &&
        cfg.SuccessfulSessionLaunches >= SuccessfulLaunchesBeforePrompt &&
        (cfg.SupportPromptNextUtc is null || cfg.SupportPromptNextUtc <= utcNow);

    public static void Record(AppConfig cfg, SupportPromptChoice choice, DateTime utcNow)
    {
        if (choice is SupportPromptChoice.Support or SupportPromptChoice.Never)
        {
            // Clicking Support also ends prompting. We deliberately cannot and do not
            // inspect whether the person completes a payment in their browser.
            cfg.SupportPromptNeverAgain = true;
            cfg.SupportPromptNextUtc = null;
            return;
        }

        cfg.SupportPromptNextUtc = utcNow.Add(SnoozeDuration);
    }

    public static bool OpenPage(IProgress<string>? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
            log?.Report("Opened the optional BeamSplit Ko-fi page in the default browser.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"Could not open the support page: {ex.Message}");
            return false;
        }
    }
}


using System.Windows.Threading;

namespace BeamSplit.Core;

/// <summary>
/// Keeps BeamNG windows unfocused for as long as a session is running.
///
/// This is not cosmetic. Windows delivers raw HID input only to the FOCUSED window, and
/// that path ignores the per-instance XInput filtering entirely - so the moment a game
/// window takes focus, that instance responds to EVERY controller. Parking focus back on
/// the shell leaves only the filtered path live, which is what makes one-pad-per-player
/// work.
///
/// Runs in-process on a DispatcherTimer; the PowerShell build needed a separate polling
/// process for the same job.
/// </summary>
public sealed class FocusGuard
{
    private readonly DispatcherTimer _timer;
    private readonly AppState _state;

    public bool Running { get; private set; }
    public int Reparks { get; private set; }
    public bool GameHasFocus { get; private set; }

    /// <summary>Fires whenever Running/Reparks/GameHasFocus change, so the UI can show it.</summary>
    public event Action? Changed;

    public FocusGuard(AppState state)
    {
        _state = state;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (Running) return;
        Running = true;
        Reparks = 0;
        _timer.Start();
        _state.Log("Focus guard on - game windows will be kept unfocused.");
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        _timer.Stop();
        _state.Log($"Focus guard off ({Reparks} re-parks).");
        Changed?.Invoke();
    }

    public void Toggle() { if (Running) Stop(); else Start(); }

    private void Tick()
    {
        var focused = Tiling.IsGameFocused();
        if (focused != GameHasFocus)
        {
            GameHasFocus = focused;
            Changed?.Invoke();
        }
        if (!focused) return;

        if (Tiling.ParkFocus())
        {
            Reparks++;
            // only mention the first few, otherwise a stuck window floods the log
            if (Reparks <= 3)
                _state.Log($"Re-parked focus off a game window ({Reparks}).");
            Changed?.Invoke();
        }
    }
}

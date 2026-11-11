using System.Runtime.InteropServices;
using System.Windows;

namespace SapphireNotch.Windows;

// Game mode: while a full-screen game (or any exclusive full-screen app) has the screen, the island hides
// itself so it can never steal focus or draw over the game, and App Tamer limits background apps at once.
public partial class MainWindow
{
    private bool gameActive;
    private int gameTicks;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private FpsOverlay? fpsOverlay;

    private void UpdateFps()
    {
        bool want = SettingsStore.Current.FpsOverlay;
        if (want && !FpsMonitor.Running && !fpsAsked)
        {
            fpsAsked = true; // ask once per switch-on
            if (!FpsMonitor.Start())
            {
                SettingsStore.Current.FpsOverlay = false;
                SettingsStore.Save();
                AddBubble("Team", "FPS needs admin permission, so it stayed off. Turn it on again in Settings and accept the Windows prompt.", false);
                want = false;
            }
        }
        if (!want)
        {
            fpsAsked = false;
            if (FpsMonitor.Running)
            {
                FpsMonitor.Stop();
            }
            fpsOverlay?.Hide();
            return;
        }
        nint window = LookNative.GetForegroundWindow();
        LookNative.GetWindowThreadProcessId(window, out uint pid);
        double fps = FpsMonitor.Get((int)pid);
        if (fps <= 0 || (!gameActive && !FullScreenWindow(window)))
        {
            fpsOverlay?.Hide();
            return;
        }
        fpsOverlay ??= new FpsOverlay();
        fpsOverlay.SetFps(fps);
        if (!fpsOverlay.IsVisible)
        {
            fpsOverlay.Show();
        }
    }

    private bool fpsAsked;

    private static bool FullScreenWindow(nint window)
    {
        return window != 0 && GetWindowRect(window, out RectI r) && r.Left <= 0 && r.Top <= 0
            && r.Right >= (int)SystemParameters.PrimaryScreenWidth && r.Bottom >= (int)SystemParameters.PrimaryScreenHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectI rect);

    private void CheckGameMode()
    {
        if (++gameTicks < 12) // about twice a second
        {
            return;
        }
        gameTicks = 0;
        UpdateFps();
        bool want = false;
        if (SettingsStore.Current.GameMode && SHQueryUserNotificationState(out int state) == 0)
        {
            want = state == 2 || state == 3; // busy full-screen app, or a Direct3D full-screen game
        }
        if (want == gameActive)
        {
            return;
        }
        gameActive = want;
        AppTamer.GameActive = want;
        if (want)
        {
            if (expanded)
            {
                Collapse();
            }
            Hide();
        }
        else
        {
            Show();
            UpdateHitRegion();
        }
    }
}

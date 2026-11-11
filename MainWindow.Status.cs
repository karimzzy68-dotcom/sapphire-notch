using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Header status cluster: battery level, keep awake, keep open, lock; and the label that shows on the selected tab only.
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
    private bool keepAwake, pinnedOpen, batteryPulsing;

    private void InitStatus()
    {
        RefreshBattery();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => RefreshBattery();
        timer.Start();
        SyncTabLabels(currentTab);
        InitPrompter();
        InitAgents();
        if (Environment.GetEnvironmentVariable("SAPPHIRE_SELFTEST") != null)
        {
            RunSelfTest();
        }

        // Developer aid: SAPPHIRE_SHOT_TAB=Music opens the island on that tab and keeps it open (for screenshots).
        string? shotTab = Environment.GetEnvironmentVariable("SAPPHIRE_SHOT_TAB");
        if (!string.IsNullOrEmpty(shotTab))
        {
            pinnedOpen = true;
            DispatcherTimer once = new() { Interval = TimeSpan.FromSeconds(1.5) };
            int attempts = 0;
            once.Tick += (_, _) =>
            {
                if (++attempts > 20)
                {
                    once.Stop();
                    return;
                }
                if (!expanded)
                {
                    Expand();
                }
                if (currentTab != shotTab)
                {
                    SelectTab(shotTab, false);
                    string? shotMode = Environment.GetEnvironmentVariable("SAPPHIRE_SHOT_MODE");
                    if (!string.IsNullOrEmpty(shotMode))
                    {
                        if (shotMode == "Copilot")
                        {
                            SetCopilotMode(true);
                            CopilotHeard("Them", "Tell me about your biggest strength and how it helps your team?");
                            CopilotHeard("You", "I turn vague requirements into clear plans.");
                            SetCopilotAnswer("Lead with: \"I turn vague requirements into clear specs, which cuts rework.\"\n• Give one recent example\n• Tie it to a team result");
                        }
                        else
                        {
                            SelectDevMode(shotMode);
                        }
                    }
                }
            };
            once.Start();
        }
    }

    private void RefreshBattery()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus power) || power.BatteryFlag == 128 || power.BatteryLifePercent > 100)
        {
            BatteryText.Text = "AC";
            BatteryIcon.Text = "\uEB5C"; // plug
            return;
        }
        bool charging = power.AcLineStatus == 1;
        BatteryText.Text = power.BatteryLifePercent + "%";
        BatteryIcon.Text = charging ? "\uE0BA" : "\uE0C2";
        if (charging != batteryPulsing)
        {
            batteryPulsing = charging;
            BatteryIcon.Foreground = charging ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) : new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
            // charging: the icon breathes, so you can see at a glance that power is flowing
            BatteryIcon.BeginAnimation(OpacityProperty, charging
                ? new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.SineEase()
                }
                : null);
        }
        BatteryText.Foreground = power.BatteryLifePercent <= 15 && !charging ? (Brush)FindResource("Red") : (Brush)FindResource("Ink");
    }

    private void Awake_Click(object sender, RoutedEventArgs e)
    {
        keepAwake = !keepAwake;
        SetThreadExecutionState(keepAwake ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
        MarkToggle(AwakeButton, keepAwake);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        pinnedOpen = !pinnedOpen;
        MarkToggle(PinButton, pinnedOpen);
    }

    private bool silent;

    // Silent / sound on: sends the media mute key, so it matches the system volume state.
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        silent = !silent;
        keybd_event(0xAD, 0, 0, 0);
        keybd_event(0xAD, 0, 2, 0);
        MuteButtonGlyph.Text = silent ? "\uE45C" : "\uE44A";
        MarkToggle(MuteButton, silent);
    }

    private void Focus_Click(object sender, RoutedEventArgs e) => OpenShell("ms-settings:quietmomentshome");

    private void Camera_Click(object sender, RoutedEventArgs e) => OpenShell("microsoft.windows.camera:");

    private static void OpenShell(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void LockPc_Click(object sender, RoutedEventArgs e) => LockWorkStation();

    private static void MarkToggle(Button button, bool on)
    {
        button.Background = on ? new SolidColorBrush(Color.FromArgb(0x55, 0x6A, 0xA8, 0xFF)) : Brushes.Transparent;
        button.BorderBrush = on ? new SolidColorBrush(Color.FromArgb(0x99, 0x6A, 0xA8, 0xFF)) : Brushes.Transparent;
        button.BorderThickness = new Thickness(on ? 1 : 0);
    }

    /// <summary>Only the selected tab shows its name; the others are icon-only, like a dock.</summary>
    private void SyncTabLabels(string tab)
    {
        (string Name, TextBlock Label)[] labels =
        {
            ("Home", HomeTabLabel), ("Chat", ChatTabLabel), ("Music", MusicTabLabel), ("Dev", DevTabLabel),
            ("Share", ShareTabLabel), ("Tools", ToolsTabLabel), ("Prompter", PrompterTabLabel), ("Tamer", TamerTabLabel), ("System", SystemTabLabel)
        };
        foreach ((string name, TextBlock label) in labels)
        {
            label.Visibility = name == tab ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

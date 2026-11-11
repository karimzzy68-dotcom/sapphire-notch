using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SapphireNotch.Windows;

public partial class SettingsWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;

    private bool loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Dark title bar and rounded corners, like the rest of the app.
            nint handle = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
            // real acrylic behind the whole window
            int acrylic = 3;
            DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int));
            HwndSource? hs = HwndSource.FromHwnd(handle);
            if (hs?.CompositionTarget != null)
            {
                hs.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            Margins all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref all);
        };
        Load();
        loading = false;
    }

    private void Load()
    {
        AppSettings s = SettingsStore.Current;
        TglMusic.IsChecked = s.ShowMusic;
        TglDev.IsChecked = s.ShowDev;
        TglShare.IsChecked = s.ShowShare;
        TglTools.IsChecked = s.ShowTools;
        TglSystem.IsChecked = s.ShowSystem;
        TglHover.IsChecked = s.OpenOnHover;
        TglGame.IsChecked = s.GameMode;
        TglFps.IsChecked = s.FpsOverlay;
        TglStartup.IsChecked = s.StartWithWindows;
        TglVoice.IsChecked = s.VoiceNotesEnabled;
        MarkTrigger(s.VoiceTrigger);
        MarkCloseChoice(s.AutoCloseSeconds);
        SettingsPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh", "settings.json");
    }

    private void MarkCloseChoice(int seconds)
    {
        foreach (Button b in new[] { Close5, Close15, Close30 })
        {
            bool on = b.Tag as string == seconds.ToString();
            b.Background = on ? (Brush)FindResource("GlassSelected") : Brushes.Transparent;
            b.Foreground = on ? (Brush)FindResource("Ink") : (Brush)FindResource("Dim3");
        }
    }

    private void MarkTrigger(string trigger)
    {
        foreach (Button b in new[] { TrigAlt, TrigWin, TrigCtrl })
        {
            bool on = b.Tag as string == trigger;
            b.Background = on ? (Brush)FindResource("GlassSelected") : Brushes.Transparent;
            b.BorderBrush = on ? (Brush)FindResource("GlassRim") : Brushes.Transparent;
            b.BorderThickness = new Thickness(on ? 1.5 : 0.0);
            b.Foreground = on ? (Brush)FindResource("Ink") : (Brush)FindResource("Dim3");
        }
    }

    private void Trigger_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            SettingsStore.Current.VoiceTrigger = tag;
            MarkTrigger(tag);
            SettingsStore.Save();
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null)
        {
            return;
        }
        PageOverview.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTabs.Visibility = NavTabs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = NavShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        AppSettings s = SettingsStore.Current;
        s.ShowMusic = TglMusic.IsChecked == true;
        s.ShowDev = TglDev.IsChecked == true;
        s.ShowShare = TglShare.IsChecked == true;
        s.ShowTools = TglTools.IsChecked == true;
        s.ShowSystem = TglSystem.IsChecked == true;
        SettingsStore.Save();
    }

    private void General_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        AppSettings s = SettingsStore.Current;
        s.OpenOnHover = TglHover.IsChecked == true;
        s.GameMode = TglGame.IsChecked == true;
        s.FpsOverlay = TglFps.IsChecked == true;
        s.StartWithWindows = TglStartup.IsChecked == true;
        s.VoiceNotesEnabled = TglVoice.IsChecked == true;
        SettingsStore.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int seconds))
        {
            SettingsStore.Current.AutoCloseSeconds = seconds;
            MarkCloseChoice(seconds);
            SettingsStore.Save();
        }
    }

    private void ShowNotch_Click(object sender, RoutedEventArgs e) => Application.Current.MainWindow?.Activate();

    private void ShowSystem_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.ShowTab("System");
        }
    }
}



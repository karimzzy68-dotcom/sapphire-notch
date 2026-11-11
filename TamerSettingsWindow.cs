using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SapphireNotch.Windows;

/// <summary>"App Tamer : Options" with the same five tabs as the real app: General, Control, Detection, Stats, Options.</summary>
internal sealed class TamerSettingsWindow : Window
{
    private static readonly (string Key, string Glyph)[] Tabs =
    {
        ("General", ""), ("Control", ""), ("Detection", ""), ("Stats", ""), ("Options", "")
    };

    private readonly StackPanel body = new() { Margin = new Thickness(26, 18, 26, 18) };
    private readonly Dictionary<string, Button> tabButtons = new();
    private readonly TextBlock title = new() { FontSize = 13, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 8) };
    private string current = "General";

    private static TamerOptions Opt => SettingsStore.Current.Tamer;

    public TamerSettingsWindow()
    {
        Width = 580;
        Height = 560;
        MinWidth = 520;
        Title = "App Tamer : Options";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = TamerUi.Hex("#FF202024");
        Foreground = (Brush)TamerUi.Res("Ink");
        FontFamily = (FontFamily)TamerUi.Res("UiFont");
        UseLayoutRounding = true;

        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        StackPanel header = new();
        header.Children.Add(title);
        StackPanel strip = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        foreach ((string key, string glyph) in Tabs)
        {
            StackPanel content = new() { Width = 78, Margin = new Thickness(0, 6, 0, 6) };
            content.Children.Add(new TextBlock { Text = glyph, Style = (Style)TamerUi.Res("Icon"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = key, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            Button button = new() { Content = content, Style = (Style)TamerUi.Res("SurfaceButton"), Margin = new Thickness(2, 0, 2, 0) };
            string tab = key;
            button.Click += (_, _) => Show(tab);
            tabButtons[key] = button;
            strip.Children.Add(button);
        }
        header.Children.Add(strip);
        header.Children.Add(new Border { Height = 1, Background = (Brush)TamerUi.Res("Hairline") });
        root.Children.Add(header);

        ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); // dark title bar
        };
        Show("General");
    }

    private void Show(string tab)
    {
        current = tab;
        title.Text = "App Tamer : " + tab;
        foreach (KeyValuePair<string, Button> entry in tabButtons)
        {
            entry.Value.Background = entry.Key == tab ? (Brush)TamerUi.Res("Fill09") : Brushes.Transparent;
            entry.Value.Foreground = entry.Key == tab ? TamerUi.Hex("#FF38BDF8") : (Brush)TamerUi.Res("Dim");
        }
        body.Children.Clear();
        switch (tab)
        {
            case "General":
                BuildGeneral();
                break;
            case "Control":
                BuildControl();
                break;
            case "Detection":
                BuildDetection();
                break;
            case "Stats":
                BuildStats();
                break;
            default:
                BuildOptions();
                break;
        }
    }

    private static void Changed()
    {
        SettingsStore.Save();
        AppTamer.Reapply();
    }

    private void BuildGeneral()
    {
        body.Children.Add(TamerUi.Line(TamerUi.Switch(SettingsStore.Current.StartWithWindows, on =>
        {
            SettingsStore.Current.StartWithWindows = on;
            Changed();
        }), "Automatically start at login"));
        body.Children.Add(TamerUi.Line(TamerUi.Switch(SettingsStore.Current.TamerEnabled, on =>
        {
            SettingsStore.Current.TamerEnabled = on;
            if (on)
            {
                AppTamer.Start();
            }
            else
            {
                AppTamer.Stop();
            }
            Changed();
        }), "Tame apps automatically (the same switch as in the notch)"));
        body.Children.Add(new TextBlock
        {
            Text = "App Tamer lives in the Tamer tab of the notch. Click an app there to choose what happens to it when you are not using it.",
            Style = (Style)TamerUi.Res("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
    }

    private void BuildControl()
    {
        TamerOptions o = Opt;
        body.Children.Add(TamerUi.Line(TamerUi.Switch(o.NoSlowWhenPlugged, on => { o.NoSlowWhenPlugged = on; Changed(); }),
            "Do not slow or stop apps if power is plugged in and the battery is charged above",
            TamerUi.Choice(new double[] { 50, 60, 70, 80, 90 }, v => $"{v:0}%", o.PlugBattery, v => { o.PlugBattery = (int)v; Changed(); })));
        body.Children.Add(TamerUi.Line(TamerUi.Switch(o.NoSlowWhenIdle, on => { o.NoSlowWhenIdle = on; Changed(); }),
            "Do not slow or stop apps after the PC has been idle for more than",
            TamerUi.Choice(new double[] { 5, 10, 15, 30, 60 }, TamerUi.Minutes, o.IdleMinutes, v => { o.IdleMinutes = (int)v; Changed(); })));
        body.Children.Add(new Border { Height = 1, Background = (Brush)TamerUi.Res("Hairline"), Margin = new Thickness(0, 8, 0, 12) });
        body.Children.Add(TamerUi.Line(null, "Wait after starting up or waking from sleep",
            TamerUi.Choice(new double[] { 0, 5, 10, 30 }, TamerUi.Seconds, o.StartDelay, v => { o.StartDelay = (int)v; Changed(); })));
        body.Children.Add(TamerUi.Line(null, "Wait before slowing or stopping an app",
            TamerUi.Choice(new double[] { 5, 15, 30, 60 }, TamerUi.Seconds, o.SlowDelay, v => { o.SlowDelay = (int)v; Changed(); })));
        body.Children.Add(TamerUi.Line(null, "Wake stopped apps every",
            TamerUi.Choice(new double[] { 1, 5, 10, 30 }, TamerUi.Minutes, o.WakeEveryMinutes, v => { o.WakeEveryMinutes = (int)v; Changed(); })));
        body.Children.Add(TamerUi.Line(TamerUi.Switch(o.HideWhenStopped, on => { o.HideWhenStopped = on; Changed(); }), "Minimize applications when they are stopped"));
        Button reset = TamerUi.Pill("Reset limits for all apps", () =>
        {
            if (MessageBox.Show(this, "Remove every app rule and put all apps back to normal?", "App Tamer", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                SettingsStore.Current.TamerRules.Clear();
                Changed();
            }
        });
        reset.HorizontalAlignment = HorizontalAlignment.Center;
        reset.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(reset);
    }

    private void BuildDetection()
    {
        TamerOptions o = Opt;
        StackPanel top = new() { HorizontalAlignment = HorizontalAlignment.Left };
        top.Children.Add(TamerUi.Line(null, "Notify me when an application exceeds",
            TamerUi.Choice(new double[] { 10, 20, 30, 50, 75, 100 }, v => $"{v:0}% CPU", o.HogPercent, v => { o.HogPercent = (int)v; Changed(); })));
        top.Children.Add(TamerUi.Line(null, "for longer than",
            TamerUi.Choice(new double[] { 10, 30, 60, 120 }, TamerUi.Seconds, o.HogSeconds, v => { o.HogSeconds = (int)v; Changed(); })));
        body.Children.Add(top);
        body.Children.Add(new TextBlock
        {
            Text = "Percent of your whole CPU, the same scale as Task Manager.",
            Style = (Style)TamerUi.Res("Caption"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        body.Children.Add(TamerUi.Heading("Don't notify me about these applications:"));
        foreach (string app in o.HogIgnore.ToList())
        {
            string name = app;
            Grid row = TamerUi.Line(null, name);
            Button remove = TamerUi.Pill("Remove", () =>
            {
                o.HogIgnore.Remove(name);
                Changed();
                Show("Detection");
            });
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            body.Children.Add(row);
        }
        if (o.HogIgnore.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "None yet.", Style = (Style)TamerUi.Res("Caption"), Margin = new Thickness(0, 0, 0, 8) });
        }
        List<string> running = new();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != 0 && !o.HogIgnore.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    {
                        running.Add(process.ProcessName);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        ComboBox pick = new() { Height = 28, FontSize = 11.5, ItemsSource = running.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() };
        Button add = TamerUi.Pill("Add", () =>
        {
            if (pick.SelectedItem is string chosen && chosen.Length > 0)
            {
                o.HogIgnore.Add(chosen);
                Changed();
                Show("Detection");
            }
        });
        add.Margin = new Thickness(8, 0, 0, 0);
        DockPanel adder = new() { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        adder.Children.Add(add);
        adder.Children.Add(pick);
        body.Children.Add(adder);
    }

    private void BuildStats()
    {
        TamerOptions o = Opt;
        body.Children.Add(TamerUi.Line(null, "Update CPU statistics every",
            TamerUi.Choice(new double[] { 1, 2, 5, 10 }, TamerUi.Seconds, o.UpdateEvery, v => { o.UpdateEvery = (int)v; Changed(); })));
        body.Children.Add(TamerUi.Line(null, "Calculate average CPU over",
            TamerUi.Choice(new double[] { 10, 30, 60, 120 }, TamerUi.Seconds, o.AverageOver, v => { o.AverageOver = (int)v; Changed(); })));
        body.Children.Add(new Border { Height = 1, Background = (Brush)TamerUi.Res("Hairline"), Margin = new Thickness(0, 10, 0, 14) });
        body.Children.Add(Total("Total hours running", o.TotalHours.ToString("0.00")));
        body.Children.Add(Total("CPU hours saved", o.SavedHours.ToString("0.00")));
        body.Children.Add(Total("Apps automatically quit", o.AutoQuit.ToString()));
    }

    private static Grid Total(string label, string value)
    {
        Grid grid = new() { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) });
        TextBlock number = new() { Text = value, FontSize = 12.5, FontWeight = FontWeights.Medium };
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);
        return grid;
    }

    private void BuildOptions()
    {
        TamerOptions o = Opt;
        body.Children.Add(TamerUi.Line(TamerUi.Switch(o.ShowGraph, on => { o.ShowGraph = on; Changed(); }), "Show graph of CPU usage"));
        body.Children.Add(new TextBlock
        {
            Text = "Percentages are the share of your whole CPU, like Task Manager. \"Run on the efficiency cores\" lowers the app's priority and turns on Windows efficiency mode (EcoQoS); on PCs with efficiency cores Windows then prefers them.",
            Style = (Style)TamerUi.Res("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Tamer tab: like the real App Tamer: CPU at a glance, the busiest apps, the tamed apps, and per-app rules.
public partial class MainWindow
{
    private const string ColorRunning = "#FF8E939C", ColorSlowed = "#FF38BDF8", ColorStopped = "#FFF87171", ColorWaiting = "#FFF5A524", ColorBoost = "#FF4ADE80";

    private readonly List<ComboBox> tamerCombos = new();
    private TamerSettingsWindow? tamerSettings;
    private string tamerFilter = "";
    private bool tamerShowSystem;
    private string? editingApp;
    private string? hogApp;
    private double hogCpu;
    private DateTime lastTamerRebuild = DateTime.MinValue;
    private List<TamerApp>? lastTamerSnapshot;

    private sealed record RowView(string Name, string Kind, TextBlock A, TextBlock B, Ellipse Dot);

    private readonly List<RowView> rowViews = new();

    private bool TamerDropDownOpen => tamerCombos.Any(c => c.IsDropDownOpen);

    private void InitTamer()
    {
        if (AppTamer.MigrateRules())
        {
            SettingsStore.Save();
        }
        TamerSwitch.IsChecked = SettingsStore.Current.TamerEnabled;
        AppTamer.RecoverPaused(); // wake anything a crashed session left frozen
        AppTamer.Updated += UpdateTamerView;
        AppTamer.HogFound += OnHogFound;
        Application.Current.Exit += (_, _) => AppTamer.Stop(); // always hand the PC back as it was
        if (SettingsStore.Current.TamerEnabled)
        {
            AppTamer.Start();
        }
        UpdateTamerView();
        Dispatcher.BeginInvoke(new Action(RefreshTamerList), DispatcherPriority.ApplicationIdle); // build the rows once up front
    }

    private void TamerSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = TamerSwitch.IsChecked == true;
        SettingsStore.Current.TamerEnabled = on;
        SettingsStore.Save();
        if (on)
        {
            AppTamer.Start();
        }
        else
        {
            AppTamer.Stop();
        }
        RefreshTamerList();
        UpdateTamerView();
        e.Handled = true;
    }

    private void TamerGear_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (tamerSettings is { IsLoaded: true })
        {
            tamerSettings.Activate();
            return;
        }
        tamerSettings = new TamerSettingsWindow();
        tamerSettings.Closed += (_, _) =>
        {
            tamerSettings = null;
            UpdateTamerView();
        };
        tamerSettings.Show();
        Collapse();
    }

    private void TamerSearch_Changed(object sender, TextChangedEventArgs e)
    {
        tamerFilter = TamerSearch.Text;
        RefreshTamerList();
    }

    // ---- Hogs ------------------------------------------------------------------------------

    private void OnHogFound(string app, double cpu)
    {
        hogApp = app;
        hogCpu = cpu;
        ShowTab("Tamer"); // tell the user, like App Tamer's notification
        RefreshTamerList();
    }

    private void IgnoreHog()
    {
        if (hogApp != null && !SettingsStore.Current.Tamer.HogIgnore.Contains(hogApp, StringComparer.OrdinalIgnoreCase))
        {
            SettingsStore.Current.Tamer.HogIgnore.Add(hogApp);
            SettingsStore.Save();
        }
        hogApp = null;
        RefreshTamerList();
    }

    private void TakeControlOfHog()
    {
        string? app = hogApp;
        hogApp = null;
        if (app != null)
        {
            if (!AppTamer.Running)
            {
                SettingsStore.Current.TamerEnabled = true;
                TamerSwitch.IsChecked = true;
                AppTamer.Start();
            }
            OpenTamerEditor(app);
        }
    }

    // ---- The lists -------------------------------------------------------------------------

    private void RefreshTamerList()
    {
        if (TamerList == null)
        {
            return;
        }
        double offset = TamerScroll.VerticalOffset;
        TamerList.Children.Clear();
        rowViews.Clear();
        lastTamerRebuild = DateTime.UtcNow;
        List<TamerApp> apps = AppTamer.Snapshot;
        lastTamerSnapshot = apps;
        TamerOptions options = SettingsStore.Current.Tamer;

        if (hogApp != null)
        {
            TamerList.Children.Add(HogCard());
        }

        string filter = tamerFilter.Trim();
        bool searching = filter.Length > 0;
        if (AppTamer.Running)
        {
            IEnumerable<TamerApp> shown = apps.Where(a => (tamerShowSystem || !a.System) && (!searching || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            shown = shown.OrderByDescending(a => a.Cpu).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
            if (!searching)
            {
                shown = shown.Take(12);
            }
            TamerList.Children.Add(SectionHeader("HIGHEST CPU PROCESSES", "% CPU", "% Avg"));
            foreach (TamerApp app in shown)
            {
                TamerList.Children.Add(ProcessRow(app, "cpu", app.Cpu.ToString("0.0"), app.Avg.ToString("0.0")));
            }
            Button system = new()
            {
                Content = tamerShowSystem ? "Hide essential system processes" : "Include essential system processes…",
                Style = (Style)FindResource("SecondaryButton"),
                Height = 26,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 4)
            };
            system.Click += (_, e) =>
            {
                tamerShowSystem = !tamerShowSystem;
                RefreshTamerList();
                e.Handled = true;
            };
            TamerList.Children.Add(system);

            List<TamerApp> tamed = apps.Where(a => a.Managed && a.State.Length > 0 && (!searching || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).OrderByDescending(a => a.Saving).ToList();
            TamerList.Children.Add(SectionHeader("TAMED PROCESSES", "% Savings", ""));
            if (tamed.Count == 0)
            {
                TamerList.Children.Add(Note("Nothing is being slowed right now."));
            }
            foreach (TamerApp app in tamed)
            {
                TamerList.Children.Add(ProcessRow(app, "saving", app.State == "Boosted" ? "boost" : app.Saving.ToString("0.0"), ""));
            }
        }
        else
        {
            TamerList.Children.Add(Note("Turn the switch on to tame apps and to see which ones use the CPU."));
        }

        HashSet<string> tamedNames = apps.Where(a => a.Managed && a.State.Length > 0).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<TamerRule> waiting = SettingsStore.Current.TamerRules
            .Where(r => r.HasEffect && !tamedNames.Contains(r.App) && (!searching || r.App.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.App, StringComparer.OrdinalIgnoreCase).ToList();
        TamerList.Children.Add(SectionHeader("MANAGED APPS", "", ""));
        if (waiting.Count == 0)
        {
            TamerList.Children.Add(Note("Click any app above to choose what happens to it when you are not using it."));
        }
        foreach (TamerRule rule in waiting)
        {
            TamerApp? known = apps.FirstOrDefault(a => a.Name.Equals(rule.App, StringComparison.OrdinalIgnoreCase));
            TamerApp view = known ?? new TamerApp { Name = rule.App, Managed = true, Limit = rule.SlowOn ? rule.SlowLimit : 0 };
            TamerList.Children.Add(ProcessRow(view, "none", known != null ? known.Cpu.ToString("0.0") : "", ""));
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TamerScroll.ScrollToVerticalOffset(offset)));
        UpdateTamerView();
    }

    private UIElement HogCard()
    {
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        buttons.Children.Add(TamerUi.Pill("Take control", TakeControlOfHog));
        Button ignore = TamerUi.Pill("Ignore", IgnoreHog);
        ignore.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(ignore);
        return new Border
        {
            Background = (Brush)FindResource("Fill09"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{hogApp} is using {hogCpu:0}% CPU", FontSize = 12.5, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "It has been busy for a while. Take control to slow it down or stop it when you are not using it.", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) },
                    buttons
                }
            }
        };
    }

    private UIElement SectionHeader(string title, string first, string second)
    {
        Grid grid = new() { Margin = new Thickness(4, 8, 4, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.Children.Add(new TextBlock { Text = title, FontSize = 10.5, Foreground = (Brush)FindResource("Dim") });
        TextBlock a = new() { Text = first, FontSize = 10.5, Foreground = (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Right };
        TextBlock b = new() { Text = second, FontSize = 10.5, Foreground = (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(a, 1);
        Grid.SetColumn(b, 2);
        grid.Children.Add(a);
        grid.Children.Add(b);
        return grid;
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = (Brush)FindResource("Dim"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(6, 2, 6, 6)
    };

    private static string DotColor(TamerApp app) => app.State switch
    {
        "Stopped" => ColorStopped,
        "Slowed" => ColorSlowed,
        "Boosted" => ColorBoost,
        _ => app.Managed ? ColorWaiting : ColorRunning
    };

    private UIElement ProcessRow(TamerApp app, string kind, string first, string second)
    {
        Grid grid = new() { Height = 28, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        Ellipse dot = new() { Width = 9, Height = 9, Fill = TamerUi.Hex(DotColor(app)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        StackPanel name = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 12.5,
            FontWeight = app.Managed ? FontWeights.Medium : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230
        });
        if (app.Limit > 0)
        {
            name.Children.Add(new TextBlock { Text = $" < {app.Limit:0.#}%", FontSize = 10.5, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 1) });
        }
        TextBlock a = new() { Text = first, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        TextBlock b = new() { Text = second, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(a, 2);
        Grid.SetColumn(b, 3);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(a);
        grid.Children.Add(b);

        Border row = new() { Child = grid, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Padding = new Thickness(4, 0, 4, 0), Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("Fill07");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        string appName = app.Name;
        row.MouseLeftButtonUp += (_, e) =>
        {
            OpenTamerEditor(appName);
            e.Handled = true;
        };
        rowViews.Add(new RowView(app.Name, kind, a, b, dot));
        return row;
    }

    // ---- Live values -----------------------------------------------------------------------

    private void UpdateTamerView()
    {
        if (TamerStatus == null)
        {
            return;
        }
        TamerOptions options = SettingsStore.Current.Tamer;
        TamerStatus.Text = AppTamer.Running
            ? (AppTamer.AffectedProcesses == 0 ? "On: nothing slowed right now" : $"On: {AppTamer.AffectedProcesses} processes tamed")
            : "Off";
        TamerSample now = AppTamer.Running ? AppTamer.Now : default;
        TamerTotalValue.Text = $"{now.Total:0.0}%";
        TamerTamedValue.Text = $"{now.Tamed:0.0}%";
        TamerSavedValue.Text = $"{now.Saved:0.0}%";
        TamerGraphBox.Visibility = options.ShowGraph ? Visibility.Visible : Visibility.Collapsed;
        if (options.ShowGraph)
        {
            DrawGraph();
        }

        if (TamerPanel.Visibility != Visibility.Visible || TamerEditor.Visibility == Visibility.Visible)
        {
            return;
        }
        List<TamerApp> apps = AppTamer.Snapshot;
        bool changed = !ReferenceEquals(apps, lastTamerSnapshot);
        if (changed && !TamerScroll.IsMouseOver && (DateTime.UtcNow - lastTamerRebuild).TotalSeconds > 2.5)
        {
            RefreshTamerList();
            return;
        }
        foreach (RowView view in rowViews)
        {
            TamerApp? app = apps.FirstOrDefault(a => a.Name.Equals(view.Name, StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                continue;
            }
            view.Dot.Fill = TamerUi.Hex(DotColor(app));
            if (view.Kind == "cpu")
            {
                view.A.Text = app.Cpu.ToString("0.0");
                view.B.Text = app.Avg.ToString("0.0");
            }
            else if (view.Kind == "saving")
            {
                view.A.Text = app.State == "Boosted" ? "boost" : app.Saving.ToString("0.0");
            }
        }
    }

    private void DrawGraph()
    {
        TamerGraph.Children.Clear();
        double width = TamerGraphBox.ActualWidth, height = TamerGraphBox.ActualHeight;
        TamerSample[] history = AppTamer.History;
        if (width < 10 || height < 10)
        {
            return;
        }
        for (int i = 1; i < 5; i++)
        {
            TamerGraph.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height * i / 5, Y2 = height * i / 5, Stroke = (Brush)FindResource("Hairline"), StrokeThickness = 1 });
        }
        if (history.Length < 2)
        {
            return;
        }
        double top = Math.Max(10, history.Max(s => s.Total) * 1.25);
        void Plot(Func<TamerSample, double> pick, string color)
        {
            Polyline line = new() { Stroke = TamerUi.Hex(color), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < history.Length; i++)
            {
                double x = width * i / (history.Length - 1);
                double y = height - 2 - Math.Clamp(pick(history[i]) / top, 0, 1) * (height - 4);
                line.Points.Add(new Point(x, y));
            }
            TamerGraph.Children.Add(line);
        }
        Plot(s => s.Total, "#FFF2F3F5");
        Plot(s => s.Tamed, "#FF38BDF8");
        Plot(s => s.Saved, "#FF4ADE80");
    }

    // ---- Per-app rules ---------------------------------------------------------------------

    private void OpenTamerEditor(string app)
    {
        editingApp = app;
        tamerCombos.Clear();
        TamerRule? existing = SettingsStore.Current.TamerRules.FirstOrDefault(r => r.App.Equals(app, StringComparison.OrdinalIgnoreCase));
        TamerRule rule = existing ?? new TamerRule { App = app, Efficiency = false };

        void Commit()
        {
            if (!SettingsStore.Current.TamerRules.Contains(rule))
            {
                SettingsStore.Current.TamerRules.Add(rule);
            }
            SettingsStore.Save();
            if (!AppTamer.Running)
            {
                SettingsStore.Current.TamerEnabled = true;
                TamerSwitch.IsChecked = true;
                AppTamer.Start();
            }
            AppTamer.Reapply();
        }

        StackPanel body = TamerEditorBody;
        body.Children.Clear();

        DockPanel top = new() { Margin = new Thickness(0, 0, 0, 10) };
        Button done = TamerUi.Pill("Done", () => CloseTamerEditor());
        DockPanel.SetDock(done, Dock.Right);
        top.Children.Add(done);
        top.Children.Add(new TextBlock { Text = app, FontSize = 16, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(top);

        Grid columns = new();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel left = new();
        left.Children.Add(TamerUi.Heading("When not in front…"));
        left.Children.Add(TamerUi.Line(TamerUi.Switch(rule.Stop, on => { rule.Stop = on; Commit(); }), "Stop this app completely"));
        left.Children.Add(TamerUi.Line(TamerUi.Switch(rule.Efficiency, on => { rule.Efficiency = on; Commit(); }), "Run this app on the CPU's efficiency cores"));
        left.Children.Add(TamerUi.Line(TamerUi.Switch(rule.SlowOn, on => { rule.SlowOn = on; Commit(); }), "Slow down this app if it uses more than:",
            TamerUi.Choice(new double[] { 0.5, 1, 2, 5, 10, 20, 25, 50 }, v => $"{v:0.#}% CPU", rule.SlowLimit, v => { rule.SlowLimit = v; Commit(); }, tamerCombos)));
        left.Children.Add(TamerUi.Line(TamerUi.Switch(rule.KeepWithSound, on => { rule.KeepWithSound = on; Commit(); }), "Don't stop or slow when sound is playing", indent: 0));
        left.Children.Add(TamerUi.Line(TamerUi.Switch(rule.OnlyHidden, on => { rule.OnlyHidden = on; Commit(); }), "Only stop or slow when the app is minimized"));

        StackPanel right = new();
        Grid.SetColumn(right, 2);
        right.Children.Add(TamerUi.Heading("When in front…"));
        right.Children.Add(TamerUi.Line(TamerUi.Switch(rule.Boost, on => { rule.Boost = on; Commit(); }), "Give this app extra priority"));
        right.Children.Add(TamerUi.Heading("When idle…"));
        right.Children.Add(TamerUi.Line(TamerUi.Switch(rule.HideAfter > 0, on => { rule.HideAfter = on ? Math.Max(5, rule.HideAfter) : 0; Commit(); }), "Minimize after:",
            TamerUi.Choice(new double[] { 1, 5, 10, 30, 60 }, TamerUi.Minutes, rule.HideAfter > 0 ? rule.HideAfter : 5, v => { if (rule.HideAfter > 0) { rule.HideAfter = (int)v; Commit(); } }, tamerCombos)));
        right.Children.Add(TamerUi.Line(TamerUi.Switch(rule.QuitAfter > 0, on => { rule.QuitAfter = on ? Math.Max(10, rule.QuitAfter) : 0; Commit(); }), "Quit after:",
            TamerUi.Choice(new double[] { 5, 10, 30, 60, 120 }, TamerUi.Minutes, rule.QuitAfter > 0 ? rule.QuitAfter : 10, v => { if (rule.QuitAfter > 0) { rule.QuitAfter = (int)v; Commit(); } }, tamerCombos)));
        Button forget = TamerUi.Pill("Stop managing this app", () =>
        {
            SettingsStore.Current.TamerRules.Remove(rule);
            SettingsStore.Save();
            AppTamer.Reapply();
            CloseTamerEditor();
        });
        forget.HorizontalAlignment = HorizontalAlignment.Left;
        forget.Margin = new Thickness(0, 10, 0, 0);
        right.Children.Add(forget);

        columns.Children.Add(left);
        columns.Children.Add(right);
        body.Children.Add(columns);
        body.Children.Add(new TextBlock
        {
            Text = "Percentages are the share of your whole CPU, the same numbers Task Manager shows. Rules apply only while the app is not the window you are using.",
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        TamerEditor.Visibility = Visibility.Visible;
    }

    private void CloseTamerEditor()
    {
        if (editingApp != null)
        {
            TamerRule? rule = SettingsStore.Current.TamerRules.FirstOrDefault(r => r.App.Equals(editingApp, StringComparison.OrdinalIgnoreCase));
            if (rule != null && !rule.HasEffect)
            {
                SettingsStore.Current.TamerRules.Remove(rule);
                SettingsStore.Save();
                AppTamer.Reapply();
            }
        }
        editingApp = null;
        tamerCombos.Clear();
        TamerEditor.Visibility = Visibility.Collapsed;
        RefreshTamerList();
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Personal AI: shows the 5-hour and weekly limits of the Codex and Claude logins on this PC,
// lets each one be connected or disconnected, and can start the next 5-hour window by itself.
public partial class MainWindow
{
    private sealed class AccountView
    {
        public required AiAccountRecord Record { get; init; }
        public AiUsage? Usage { get; set; }
        public bool Expanded { get; set; }
        public bool Busy { get; set; }
        public DateTime LastFired { get; set; } = DateTime.MinValue;
        public DateTime? LastReset { get; set; }
        public string? Note { get; set; }
    }

    private List<AiAccountRecord> aiRecords = new();
    private readonly List<AccountView> aiViews = new();
    private readonly DispatcherTimer aiTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool aiRefreshing;

    private void InitPersonalAI()
    {
        aiRecords = AiAccounts.Load();
        foreach (AiAccountRecord record in aiRecords)
        {
            aiViews.Add(new AccountView { Record = record });
        }
        aiTimer.Tick += async (_, _) => await RefreshAccountsAsync();
        aiTimer.Start();
        PersonalAIPanel.IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                await RefreshAccountsAsync();
            }
        };
        RenderAccounts();
        _ = RefreshAccountsAsync();
    }

    private async void RefreshAccounts_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RefreshAccountsAsync();
    }

    private async Task RefreshAccountsAsync()
    {
        if (aiRefreshing)
        {
            return;
        }
        aiRefreshing = true;
        try
        {
            foreach (AccountView view in aiViews.ToList())
            {
                if (!view.Record.Connected)
                {
                    continue;
                }
                view.Usage = await AiAccounts.FetchAsync(view.Record);
                await MaybeStartSessionAsync(view);
            }
        }
        finally
        {
            aiRefreshing = false;
        }
        RenderAccounts();
    }

    // Auto session start: when the 5-hour window has run out (or never began), send one tiny prompt so the next one starts now.
    private async Task MaybeStartSessionAsync(AccountView view)
    {
        AiUsage? usage = view.Usage;
        if (!view.Record.AutoStart || usage == null || usage.Error != null || view.Busy)
        {
            return;
        }
        DateTime now = DateTime.Now;
        bool windowEnded = view.LastReset is DateTime last && now > last.AddSeconds(30) && (usage.SessionReset == null || usage.SessionReset > last.AddMinutes(1));
        bool noWindow = usage.SessionReset == null && (now - view.LastFired).TotalHours > 5.1;
        view.LastReset = usage.SessionReset ?? view.LastReset;
        if ((!windowEnded && !noWindow) || (now - view.LastFired).TotalMinutes < 10)
        {
            return;
        }
        view.Busy = true;
        view.LastFired = now;
        view.Note = "Starting a new session…";
        RenderAccounts();
        string? error = await AiAccounts.StartSessionAsync(view.Record);
        view.Busy = false;
        view.Note = error ?? "New session started.";
        view.Usage = await AiAccounts.FetchAsync(view.Record);
        view.LastReset = view.Usage.SessionReset;
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ContextMenu menu = new() { PlacementTarget = (UIElement)sender };
        MenuItem claude = new() { Header = "Sign in a Claude account (browser)" };
        claude.Click += (_, _) => BeginBrowserSignIn("claude");
        MenuItem codex = new() { Header = "Sign in a Codex account (browser)" };
        codex.Click += (_, _) => BeginBrowserSignIn("codex");
        MenuItem detect = new() { Header = "Look for logins on this PC" };
        detect.Click += async (_, _) => await DetectAccountsAsync();
        menu.Items.Add(claude);
        menu.Items.Add(codex);
        menu.Items.Add(new Separator());
        menu.Items.Add(detect);
        menu.IsOpen = true;
    }

    // Opens the browser sign-in for a new account in its own folder, then adds the card by itself once the login lands.
    private void BeginBrowserSignIn(string kind)
    {
        string? error = AiAccounts.BeginSignIn(kind, out string home);
        if (error != null)
        {
            AccountsSummary.Text = error;
            return;
        }
        AccountsSummary.Text = "Finish the " + (kind == "claude" ? "Claude" : "Codex") + " sign-in in your browser…";
        string file = Path.Combine(home, kind == "claude" ? ".credentials.json" : "auth.json");
        DateTime giveUp = DateTime.Now.AddMinutes(5);
        DispatcherTimer wait = new() { Interval = TimeSpan.FromSeconds(3) };
        wait.Tick += async (_, _) =>
        {
            if (File.Exists(file))
            {
                wait.Stop();
                await DetectAccountsAsync();
            }
            else if (DateTime.Now > giveUp)
            {
                wait.Stop();
                AccountsSummary.Text = "Sign-in was not finished. Try Add account again.";
            }
        };
        wait.Start();
    }

    private async Task DetectAccountsAsync()
    {
        int before = aiRecords.Count;
        AiAccounts.Detect(aiRecords);
        foreach (AiAccountRecord record in aiRecords.Where(r => aiViews.All(v => v.Record != r)))
        {
            aiViews.Add(new AccountView { Record = record });
        }
        AiAccounts.Save(aiRecords);
        AccountsSummary.Text = aiRecords.Count > before ? "Found " + (aiRecords.Count - before) + " new login(s)." : "No new logins found.";
        await RefreshAccountsAsync();
    }

    private void RenderAccounts()
    {
        if (AccountList == null)
        {
            return;
        }
        int codex = aiViews.Count(v => v.Record.Kind == "codex");
        int claude = aiViews.Count(v => v.Record.Kind == "claude");
        if (AccountsSummary.Text.Length == 0 || AccountsSummary.Text.Contains("accounts") || AccountsSummary.Text.Contains("tracked"))
        {
            AccountsSummary.Text = aiViews.Count == 0 ? "No Codex or Claude login found on this PC." : codex + " Codex · " + claude + " Claude · " + aiViews.Count(v => v.Record.Connected) + " tracked";
        }
        // only rebuild when something visible changed, so the list never jumps while you are reading or scrolling it
        string signature = string.Join("|", aiViews.Select(v => string.Join(",", v.Record.Id, v.Record.Connected, v.Record.AutoStart, v.Expanded, v.Note,
            v.Usage?.Error, v.Usage?.SessionUsed, v.Usage?.WeekUsed, v.Usage?.SessionReset is DateTime r ? (long)(r - DateTime.Now).TotalMinutes : -1, v.Usage?.Email))) + CardWidth();
        if (signature == lastAccountSignature)
        {
            return;
        }
        lastAccountSignature = signature;
        double offset = AccountScroll.VerticalOffset;
        AccountList.Children.Clear();
        foreach (AccountView view in aiViews)
        {
            AccountList.Children.Add(BuildCard(view));
        }
        Dispatcher.BeginInvoke(new Action(() => AccountScroll.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
    }

    private string lastAccountSignature = "";

    private double CardWidth() => AccountScroll.ActualWidth > 100 ? Math.Floor((AccountScroll.ActualWidth - 12) / 2) : 246;

    private void AccountScroll_SizeChanged(object sender, SizeChangedEventArgs e) => RenderAccounts();

    // Smooth, even scrolling: a fixed distance per notch, eased toward the target instead of jumping a line at a time.
    private double accountScrollTarget = -1;
    private bool accountScrollRunning;

    private void AccountScroll_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!accountScrollRunning)
        {
            accountScrollTarget = AccountScroll.VerticalOffset;
        }
        accountScrollTarget = Math.Clamp(accountScrollTarget - e.Delta * 0.55, 0, AccountScroll.ScrollableHeight);
        if (accountScrollRunning)
        {
            return;
        }
        accountScrollRunning = true;
        EventHandler? step = null;
        step = (_, _) =>
        {
            double gap = accountScrollTarget - AccountScroll.VerticalOffset;
            if (Math.Abs(gap) < 0.5)
            {
                AccountScroll.ScrollToVerticalOffset(accountScrollTarget);
                CompositionTarget.Rendering -= step;
                accountScrollRunning = false;
                return;
            }
            AccountScroll.ScrollToVerticalOffset(AccountScroll.VerticalOffset + gap * 0.25);
        };
        CompositionTarget.Rendering += step;
    }

    private static string In(DateTime when)
    {
        TimeSpan left = when - DateTime.Now;
        if (left.TotalMinutes < 1) return "resets in under a minute";
        if (left.TotalHours < 1) return "resets in " + (int)left.TotalMinutes + " minutes";
        if (left.TotalHours < 48) return "resets in " + Math.Round(left.TotalHours) + " hours";
        return "resets in " + Math.Round(left.TotalDays) + " days";
    }

    private UIElement BuildCard(AccountView view)
    {
        AiAccountRecord record = view.Record;
        AiUsage? usage = view.Usage;
        string brand = record.Kind == "claude" ? "Claude" : "Codex";
        string name = usage?.Email is { Length: > 0 } email ? email : brand;
        if (usage?.Email == null && !record.Id.EndsWith("default"))
        {
            name = brand + " · #" + record.Id.Split('-').Last();
        }

        StackPanel body = new();
        Grid top = new();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel title = new() { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = record.Kind == "claude" ? "✦" : "</>", FontSize = 11, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.Medium, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(70, CardWidth() - 150), VerticalAlignment = VerticalAlignment.Center, ToolTip = name });
        title.Children.Add(new TextBlock { Text = "5H", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(title, 0);
        top.Children.Add(title);
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock chevron = new() { Text = view.Expanded ? "▴" : "▾", FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        Grid.SetColumn(chevron, 2);
        top.Children.Add(chevron);

        if (!record.Connected)
        {
            body.Children.Add(top);
            body.Children.Add(new TextBlock { Text = "Disconnected", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 6) });
        }
        else if (usage == null)
        {
            body.Children.Add(top);
            body.Children.Add(new TextBlock { Text = "Loading…", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0) });
        }
        else if (usage.Error != null)
        {
            body.Children.Add(top);
            body.Children.Add(new TextBlock { Text = usage.Error, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }
        else
        {
            double left = Math.Clamp(100 - (usage.SessionUsed ?? 0), 0, 100);
            TextBlock percent = new() { Text = usage.SessionUsed == null ? "No session" : Math.Round(left) + "% left", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(percent, 1);
            top.Children.Add(percent);
            body.Children.Add(top);
            body.Children.Add(Bar(left, new Thickness(0, 6, 0, 0)));
            body.Children.Add(new TextBlock
            {
                Text = usage.SessionReset is DateTime reset ? In(reset) : "starts with your next message",
                Style = (Style)FindResource("Caption"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0)
            });
            if (view.Expanded && usage.WeekUsed != null)
            {
                double weekLeft = Math.Clamp(100 - usage.WeekUsed.Value, 0, 100);
                body.Children.Add(new TextBlock { Text = "Weekly · " + Math.Round(weekLeft) + "% left" + (usage.WeekReset is DateTime weekReset ? " · " + In(weekReset) : ""), Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 6, 0, 0) });
                body.Children.Add(Bar(weekLeft, new Thickness(0, 3, 0, 0)));
            }
        }

        if (view.Note != null)
        {
            body.Children.Add(new TextBlock { Text = view.Note, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }

        if (view.Expanded || !record.Connected)
        {
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            Button connect = SmallButton(record.Connected ? "Disconnect" : "Connect");
            connect.Click += async (_, args) =>
            {
                args.Handled = true;
                record.Connected = !record.Connected;
                if (!record.Connected)
                {
                    record.AutoStart = false;
                    view.Usage = null;
                    view.Note = null;
                }
                AiAccounts.Save(aiRecords);
                RenderAccounts();
                if (record.Connected)
                {
                    await RefreshAccountsAsync();
                }
            };
            actions.Children.Add(connect);
            if (record.Connected)
            {
                Button auto = SmallButton("Auto session: " + (record.AutoStart ? "On" : "Off"));
                auto.Margin = new Thickness(6, 0, 0, 0);
                auto.ToolTip = "When the 5-hour window runs out, send one tiny prompt so the next window starts by itself.";
                auto.Click += async (_, args) =>
                {
                    args.Handled = true;
                    record.AutoStart = !record.AutoStart;
                    AiAccounts.Save(aiRecords);
                    RenderAccounts();
                    if (record.AutoStart)
                    {
                        await RefreshAccountsAsync();
                    }
                };
                actions.Children.Add(auto);
            }
            body.Children.Add(actions);
        }

        Border card = new()
        {
            Style = (Style)FindResource("Inset"), CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 6, 6), Width = CardWidth() - 6, Child = body, Cursor = System.Windows.Input.Cursors.Hand
        };
        card.MouseLeftButtonUp += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && FindParent<Button>(source) != null)
            {
                return;
            }
            view.Expanded = !view.Expanded;
            RenderAccounts();
            args.Handled = true;
        };
        return card;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = child; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }

    private Button SmallButton(string text) => new() { Content = text, Style = (Style)FindResource("SecondaryButton"), Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11 };

    private static UIElement Bar(double percentLeft, Thickness margin)
    {
        Grid track = new() { Height = 4, Margin = margin };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) });
        Border fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(percentLeft < 15 ? Color.FromRgb(0xFF, 0x5A, 0x5F) : Color.FromRgb(0x38, 0xBD, 0xF8)) };
        track.SizeChanged += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * percentLeft / 100.0);
        track.Children.Add(fill);
        return track;
    }
}

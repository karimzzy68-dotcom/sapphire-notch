using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Guided tour: the screen dims, the button being explained is lit, a pointer glides to it and taps it,
// and a small card says what it does. Shown once after install, and again from the gear menu.
public partial class MainWindow
{
    private sealed record TourStep(string Tab, string? Tool, string? DevMode, Func<FrameworkElement?>? Target, string Title, string Text);

    private static readonly Color TourBlue = Color.FromRgb(0x38, 0xBD, 0xF8);

    private bool tourActive;
    private int tourIndex;
    private int tourTicket;
    private Grid? tourLayer;
    private Path? tourScrim;
    private RectangleGeometry? tourHole;
    private Border? tourRing, tourCard, tourRipple;
    private Path? tourPointer;
    private Canvas? tourCanvas;
    private TextBlock? tourCount, tourTitle, tourText;
    private StackPanel? tourDots;
    private Button? tourBack, tourNext;
    private Rect tourLast = Rect.Empty;

    private static string TriggerName() => SettingsStore.Current.VoiceTrigger switch
    {
        "CtrlWin" => "Ctrl + the Windows key",
        "BothCtrl" => "both Ctrl keys",
        _ => "Ctrl + Alt"
    };

    // ---- finding the real buttons on screen ---------------------------------------------------

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && match(hit))
            {
                return hit;
            }
            if (Find(child, match) is T deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    private static bool HasText(DependencyObject root, string text) => Find<TextBlock>(root, t => t.Text.Contains(text)) != null;

    private FrameworkElement? ButtonWithText(string text) => Find<Button>(ExpandedSurface, b => b.Content is string s ? s == text : HasText(b, text));

    private FrameworkElement? ButtonWithTip(string start) => Find<Button>(ExpandedSurface, b => b.ToolTip is string s && s.StartsWith(start));

    private List<TourStep> TourSteps() => new()
    {
        new("Home", null, null, null, "Welcome to Sapphire NOTCh", "A quick tour, nine stops. It lives at the top of your screen."),
        new("Home", null, null, () => TabScroll, "Tabs", "Click an icon to switch page, or swipe. Only the open tab shows its name."),
        new("Home", null, null, () => (MuteButton.Parent as FrameworkElement)?.Parent as FrameworkElement, "Quick switches", "Battery, focus, camera, silent, keep awake, keep open and lock PC. All one click."),
        new("Home", null, null, () => VoiceButton, "Voice typing", "Hold " + TriggerName() + " in any app, speak, let go: your words get typed."),
        new("Chat", null, null, () => ChatInput, "Chat", "Ask anything and press Enter. Pick the persona and model above; add your OpenRouter key once."),
        new("Music", null, null, () => MusicOpenButton, "Music", "Now playing with synced lyrics and controls. Works with Spotify or YouTube Music."),
        new("Dev", null, "AI", () => AIModeButton, "Dev", "Git status and your Claude / Codex usage limits, account by account."),
        new("Share", null, null, () => (FrameworkElement?)Find<Button>(SharePanel, b => HasText(b, "Nearby")) ?? Find<Border>(SharePanel, b => HasText(b, "Drag files")), "Share", "Drag files to the top edge and drop them on Nearby Sharing or LocalSend."),
        new("Tools", null, null, () => TimerToolButton, "Tools, Prompter, Tamer", "Timer, calculator, clipboard, notes. Prompter is a teleprompter you can hide from screen sharing. Tamer slows idle apps."),
        new("Home", null, null, null, "You are set", "Right-click the bar or open the menu to replay this. Enjoy!")
    };

    private void Tour_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StartTour();
    }

    /// <summary>Called after start-up; shows the tour once for a new user.</summary>
    private void StartTourIfFirstRun(double delaySeconds)
    {
        if (SettingsStore.Current.TourSeen)
        {
            return;
        }
        DispatcherTimer wait = new() { Interval = TimeSpan.FromSeconds(delaySeconds) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            StartTour();
        };
        wait.Start();
    }

    private void StartTour()
    {
        EnsureTourLayer();
        tourActive = true;
        tourIndex = 0;
        tourLast = Rect.Empty;
        Expand();
        tourLayer!.Visibility = Visibility.Visible;
        ShowTourStep();
    }

    // ---- building the overlay -------------------------------------------------------------------

    private void EnsureTourLayer()
    {
        if (tourLayer != null)
        {
            return;
        }
        tourHole = new RectangleGeometry(new Rect(0, 0, 0, 0), 10, 10);
        tourScrim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0xC8, 0x02, 0x04, 0x0A)), IsHitTestVisible = true };

        tourRing = new Border
        {
            BorderBrush = new SolidColorBrush(TourBlue), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
            IsHitTestVisible = false, Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = TourBlue, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 }
        };
        tourRipple = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.3, 0.3) };
        tourPointer = new Path
        {
            Data = Geometry.Parse("M0,0 L0,17 L4.4,13 L7.4,19.5 L10.2,18.3 L7.3,12 L13,12 Z"),
            Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x20)), StrokeThickness = 1.2, Opacity = 0, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Opacity = 0.6 }
        };

        tourCount = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TourBlue) };
        tourTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 2, 0, 3) };
        tourText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Dim"), LineHeight = 15 };
        tourDots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        Button skip = new() { Content = "Skip", Style = (Style)FindResource("LinkButton"), Foreground = (Brush)FindResource("Dim"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        skip.Click += (_, e) => { e.Handled = true; EndTour(); };
        tourBack = new Button { Content = "Back", Style = (Style)FindResource("SecondaryButton"), Height = 24, Padding = new Thickness(12, 0, 12, 0), FontSize = 10.5 };
        tourBack.Click += (_, e) => { e.Handled = true; tourIndex = Math.Max(0, tourIndex - 1); ShowTourStep(); };
        tourNext = new Button { Content = "Next", Style = (Style)FindResource("PrimaryButton"), Height = 24, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(5, 0, 0, 0), FontSize = 10.5 };
        tourNext.Click += (_, e) =>
        {
            e.Handled = true;
            if (tourIndex >= TourSteps().Count - 1)
            {
                EndTour();
                return;
            }
            tourIndex++;
            ShowTourStep();
        };

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(skip);
        buttons.Children.Add(new Border { Width = 6 });
        buttons.Children.Add(tourBack);
        buttons.Children.Add(tourNext);
        Grid footer = new() { Margin = new Thickness(0, 9, 0, 0) };
        footer.Children.Add(tourDots);
        footer.Children.Add(buttons);

        StackPanel body = new();
        body.Children.Add(tourCount);
        body.Children.Add(tourTitle);
        body.Children.Add(tourText);
        body.Children.Add(footer);

        tourCard = new Border
        {
            Width = 330, CornerRadius = new CornerRadius(16), Padding = new Thickness(15, 10, 15, 10),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x12, 0x17, 0x24)),
            BorderBrush = (Brush)FindResource("GlassRim"), BorderThickness = new Thickness(1.2), Child = body
        };

        tourCanvas = new Canvas { IsHitTestVisible = true, Background = Brushes.Transparent };
        tourCanvas.Children.Add(tourRing);
        tourCanvas.Children.Add(tourRipple);
        tourCanvas.Children.Add(tourCard);
        tourCanvas.Children.Add(tourPointer);

        tourLayer = new Grid { Visibility = Visibility.Collapsed };
        tourLayer.Children.Add(tourScrim);
        tourLayer.Children.Add(tourCanvas);
        Grid.SetRowSpan(tourLayer, 2);
        Panel.SetZIndex(tourLayer, 100);
        ExpandedSurface.Children.Add(tourLayer);
    }

    // ---- one step -------------------------------------------------------------------------------

    private void ShowTourStep()
    {
        List<TourStep> steps = TourSteps();
        TourStep step = steps[tourIndex];
        int ticket = ++tourTicket;

        SelectTab(step.Tab);
        if (step.DevMode != null)
        {
            SelectDevMode(step.DevMode);
        }
        else if (step.Tab == "Dev")
        {
            SelectDevMode("Git");
        }
        if (step.Tool != null)
        {
            SelectTool(step.Tool);
        }

        tourCount!.Text = (tourIndex + 1) + " OF " + steps.Count;
        tourTitle!.Text = step.Title;
        tourText!.Text = step.Text;
        tourBack!.Visibility = tourIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        tourNext!.Content = tourIndex == steps.Count - 1 ? "Done" : "Next";
        tourDots!.Children.Clear();
        int shown = Math.Min(steps.Count, 12);
        int active = (int)Math.Round((double)tourIndex / Math.Max(1, steps.Count - 1) * (shown - 1));
        for (int i = 0; i < shown; i++)
        {
            tourDots.Children.Add(new Border
            {
                Width = i == active ? 14 : 5, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 0, 3, 0),
                Background = new SolidColorBrush(i == active ? TourBlue : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            });
        }

        // hide the old highlight while the page changes, then light the new button once the page has settled
        tourRing!.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        tourPointer!.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(step.Tab == "Tamer" || step.DevMode == "AI" || step.Tab == "Chat" ? 560 : 380) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (ticket == tourTicket && tourActive)
            {
                Spotlight(step);
            }
        };
        settle.Start();
        PlaceCard(Rect.Empty, animate: false);
    }

    private void Spotlight(TourStep step)
    {
        double width = ExpandedSurface.Width;
        double height = ExpandedSurface.Height;
        tourScrim!.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), tourHole);

        FrameworkElement? target = null;
        try
        {
            target = step.Target?.Invoke();
        }
        catch (InvalidOperationException)
        {
        }
        Rect box = Rect.Empty;
        if (target is { IsVisible: true, ActualWidth: > 2, ActualHeight: > 2 })
        {
            Rect raw = target.TransformToVisual(ExpandedSurface).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            box = new Rect(Math.Max(2, raw.X - 5), Math.Max(2, raw.Y - 4), Math.Min(width - 4, raw.Width + 10), raw.Height + 8);
            box.Intersect(new Rect(2, 2, width - 4, height - 4));
        }

        TimeSpan move = TimeSpan.FromMilliseconds(420);
        CubicEase ease = new() { EasingMode = EasingMode.EaseInOut };
        Rect from = tourLast.IsEmpty ? new Rect(width / 2, height / 2, 0, 0) : tourLast;
        if (box.IsEmpty)
        {
            tourHole!.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(from, new Rect(width / 2, height / 2, 0, 0), move) { EasingFunction = ease });
            tourLast = Rect.Empty;
            PlaceCard(Rect.Empty, animate: true);
            return;
        }

        tourHole!.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(from, box, move) { EasingFunction = ease });
        tourLast = box;

        // the lit ring glides to the button and then pulses
        tourRing!.Width = box.Width;
        tourRing.Height = box.Height;
        tourRing.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(from.X, box.X, move) { EasingFunction = ease });
        tourRing.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from.Y, box.Y, move) { EasingFunction = ease });
        tourRing.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0)),
                new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0.35)),
                new LinearDoubleKeyFrame(0.45, KeyTime.FromPercent(0.7)),
                new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0))
            },
            Duration = TimeSpan.FromMilliseconds(1100), RepeatBehavior = RepeatBehavior.Forever
        });

        // the pointer comes in from the card and taps the button
        Point center = new(box.X + box.Width / 2 - 2, box.Y + box.Height / 2 - 2);
        Point start = new(Math.Clamp(center.X + 70, 0, width - 20), Math.Clamp(center.Y + 60, 0, height - 20));
        tourPointer!.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(start.X, center.X, TimeSpan.FromMilliseconds(650)) { BeginTime = move, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tourPointer.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(start.Y, center.Y, TimeSpan.FromMilliseconds(650)) { BeginTime = move, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tourPointer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { BeginTime = move });

        Canvas.SetLeft(tourRipple!, center.X - 15);
        Canvas.SetTop(tourRipple, center.Y - 15);
        TimeSpan tap = move + TimeSpan.FromMilliseconds(700);
        ScaleTransform scale = (ScaleTransform)tourRipple.RenderTransform;
        DoubleAnimation grow = new(0.3, 1.5, TimeSpan.FromMilliseconds(520)) { BeginTime = tap, RepeatBehavior = new RepeatBehavior(3) };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        tourRipple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = tap, RepeatBehavior = new RepeatBehavior(3) });

        PlaceCard(box, animate: true);
    }

    private void PlaceCard(Rect box, bool animate)
    {
        double width = ExpandedSurface.Width;
        double height = ExpandedSurface.Height;
        tourCard!.Measure(new Size(tourCard.Width, 400));
        double cardHeight = tourCard.DesiredSize.Height;
        double x, y;
        if (box.IsEmpty)
        {
            x = (width - tourCard.Width) / 2;
            y = Math.Max(54, (height - cardHeight) / 2 + 20);
        }
        else
        {
            x = Math.Clamp(box.X + box.Width / 2 - tourCard.Width / 2, 8, width - tourCard.Width - 8);
            if (box.Bottom + 12 + cardHeight <= height - 4)
            {
                y = box.Bottom + 12;
            }
            else if (box.Top - 12 - cardHeight >= 4)
            {
                y = box.Top - 12 - cardHeight;
            }
            else
            {
                y = Math.Max(4, height - cardHeight - 6);
            }
        }
        if (!animate)
        {
            tourCard.BeginAnimation(Canvas.LeftProperty, null);
            tourCard.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(tourCard, x);
            Canvas.SetTop(tourCard, y);
            return;
        }
        double fromX = double.IsNaN(Canvas.GetLeft(tourCard)) ? x : Canvas.GetLeft(tourCard);
        double fromY = double.IsNaN(Canvas.GetTop(tourCard)) ? y : Canvas.GetTop(tourCard);
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
        tourCard.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(fromX, x, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        tourCard.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(fromY, y, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    private void EndTour()
    {
        tourActive = false;
        tourTicket++;
        if (tourLayer != null)
        {
            tourRing?.BeginAnimation(OpacityProperty, null);
            tourPointer?.BeginAnimation(OpacityProperty, null);
            tourLayer.Visibility = Visibility.Collapsed;
        }
        SettingsStore.Current.TourSeen = true;
        SettingsStore.Save();
        SelectTab("Home");
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SapphireNotch.Windows;

// Swipe left / right to change tab: mouse or touch drag, two-finger touchpad swipe, and the tab strip chevrons.
public partial class MainWindow
{
    private const double SwipeThreshold = 45.0;
    private const double SwipeStart = 8.0;

    private Point? swipeOrigin;
    private bool swiping;
    private double swipeDx;
    private long lastTouchpadSwipe;
    private double touchpadAccumulator;

    public static readonly DependencyProperty TabScrollOffsetProperty = DependencyProperty.Register(
        nameof(TabScrollOffset), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).TabScroll.ScrollToHorizontalOffset((double)e.NewValue)));

    public double TabScrollOffset
    {
        get => (double)GetValue(TabScrollOffsetProperty);
        set => SetValue(TabScrollOffsetProperty, value);
    }

    private void InitSwipe()
    {
        ContentStage.PreviewMouseLeftButtonDown += Stage_Down;
        ContentStage.PreviewMouseMove += Stage_Move;
        ContentStage.PreviewMouseLeftButtonUp += Stage_Up;
        ContentStage.LostMouseCapture += delegate
        {
            if (swiping)
            {
                EndSwipe(commit: false, dx: 0.0);
            }
        };
    }

    private static bool IsInputControl(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is FrameworkElement { Name: "ChatScroll" } || element is TextBoxBase || element is PasswordBox || element is ComboBox || element is ScrollBar || element is Slider)
            {
                return true;
            }
            element = (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Stage_Down(object sender, MouseButtonEventArgs e)
    {
        if (!expanded || IsInputControl(e.OriginalSource as DependencyObject))
        {
            swipeOrigin = null;
            return;
        }
        swipeOrigin = e.GetPosition(this);
        swiping = false;
        swipeDx = 0.0;
    }

    private void Stage_Move(object sender, MouseEventArgs e)
    {
        if (swipeOrigin == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        double dx = e.GetPosition(this).X - swipeOrigin.Value.X;
        if (!swiping && Math.Abs(dx) > SwipeStart)
        {
            swiping = true;
            ContentStage.BeginAnimation(UIElement.OpacityProperty, null);
            ((TranslateTransform)ContentStage.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
            ContentStage.CaptureMouse();
        }
        if (swiping)
        {
            swipeDx = dx;
            // the page follows the finger, with resistance, and dims a little
            ((TranslateTransform)ContentStage.RenderTransform).X = Math.Sign(dx) * Math.Min(60.0, Math.Abs(dx) * 0.45);
            ContentStage.Opacity = 1.0 - Math.Min(1.0, Math.Abs(dx) / 220.0) * 0.45;
            e.Handled = true;
        }
    }

    private void Stage_Up(object sender, MouseButtonEventArgs e)
    {
        if (swiping)
        {
            e.Handled = true; // a drag must not also click the card under the pointer
            bool commit = Math.Abs(swipeDx) > SwipeThreshold;
            double dx = swipeDx;
            swiping = false; // before releasing capture, or the capture-lost handler would cancel the swipe
            ContentStage.ReleaseMouseCapture();
            EndSwipe(commit, dx);
        }
        swipeOrigin = null;
    }

    private void EndSwipe(bool commit, double dx)
    {
        swiping = false;
        swipeDx = 0.0;
        TranslateTransform shift = (TranslateTransform)ContentStage.RenderTransform;
        if (commit)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0.0;
            ContentStage.Opacity = 1.0;
            NavigatePage(dx < 0.0 ? 1 : -1);
            return;
        }
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift.X, 0.0, TimeSpan.FromMilliseconds(260.0))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
        });
        ContentStage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ContentStage.Opacity, 1.0, TimeSpan.FromMilliseconds(200.0)));
    }

    /// <summary>Small nudge when there is no tab further in that direction.</summary>
    private void BounceStage(int direction)
    {
        TranslateTransform shift = (TranslateTransform)ContentStage.RenderTransform;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, -direction * 14.0, TimeSpan.FromMilliseconds(110.0))
        {
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>Two-finger horizontal swipe on a precision touchpad arrives as horizontal wheel ticks.</summary>
    private bool TouchpadSwipe(int delta)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(lastTouchpadSwipe, now).TotalMilliseconds < 380.0)
        {
            return true; // swallow momentum from the previous swipe
        }
        touchpadAccumulator += delta;
        if (Math.Abs(touchpadAccumulator) < 120.0)
        {
            return true;
        }
        int direction = touchpadAccumulator > 0 ? 1 : -1;
        touchpadAccumulator = 0.0;
        lastTouchpadSwipe = now;
        NavigatePage(direction);
        return true;
    }

    // ---- Tab strip ------------------------------------------------------------------------

    private void ScrollTabsTo(double offset)
    {
        offset = Math.Clamp(offset, 0.0, TabScroll.ScrollableWidth);
        BeginAnimation(TabScrollOffsetProperty, new DoubleAnimation(TabScroll.HorizontalOffset, offset, TimeSpan.FromMilliseconds(240.0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void TabLeft_Click(object sender, RoutedEventArgs e)
    {
        ScrollTabsTo(TabScroll.HorizontalOffset - 150.0);
        e.Handled = true;
    }

    private void TabRight_Click(object sender, RoutedEventArgs e)
    {
        ScrollTabsTo(TabScroll.HorizontalOffset + 150.0);
        e.Handled = true;
    }

    private void TabScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        bool overflow = TabScroll.ScrollableWidth > 1.0;
        TabLeftButton.Visibility = overflow && TabScroll.HorizontalOffset > 1.0 ? Visibility.Visible : Visibility.Collapsed;
        TabRightButton.Visibility = overflow && TabScroll.HorizontalOffset < TabScroll.ScrollableWidth - 1.0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnsureTabVisible(string tab)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!TabButtons.TryGetValue(tab, out Button? button) || !button.IsLoaded || !TabScroll.IsLoaded)
            {
                return;
            }
            double left = button.TransformToAncestor(TabScroll).Transform(new Point(0, 0)).X;
            double right = left + button.ActualWidth;
            if (left < 0)
            {
                ScrollTabsTo(TabScroll.HorizontalOffset + left - 10);
            }
            else if (right > TabScroll.ViewportWidth)
            {
                ScrollTabsTo(TabScroll.HorizontalOffset + right - TabScroll.ViewportWidth + 10);
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}

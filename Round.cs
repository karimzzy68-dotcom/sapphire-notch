using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SapphireNotch.Windows;

public static class Round
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached("Radius", typeof(CornerRadius), typeof(Round), new PropertyMetadata(new CornerRadius(10)));

    /// <summary>Text colour a selectable button returns to when it is not the selected one.</summary>
    public static readonly DependencyProperty RestBrushProperty =
        DependencyProperty.RegisterAttached("RestBrush", typeof(Brush), typeof(Round), new PropertyMetadata(Brushes.Gray));

    /// <summary>Grows the element slightly while the pointer is over it.</summary>
    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached("HoverScale", typeof(double), typeof(Round), new PropertyMetadata(1.0, OnHoverScaleChanged));

    public static CornerRadius GetRadius(DependencyObject d) => (CornerRadius)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, CornerRadius value) => d.SetValue(RadiusProperty, value);

    public static Brush GetRestBrush(DependencyObject d) => (Brush)d.GetValue(RestBrushProperty);
    public static void SetRestBrush(DependencyObject d, Brush value) => d.SetValue(RestBrushProperty, value);

    public static double GetHoverScale(DependencyObject d) => (double)d.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject d, double value) => d.SetValue(HoverScaleProperty, value);

    private static void OnHoverScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (double)e.NewValue == 1.0)
        {
            return;
        }
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new ScaleTransform(1.0, 1.0);
        element.MouseEnter += (_, _) => Animate(element, GetHoverScale(element), 200);
        element.MouseLeave += (_, _) => Animate(element, 1.0, 200);
    }

    private static void Animate(FrameworkElement element, double to, int milliseconds)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            return;
        }
        DoubleAnimation animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SapphireNotch.Windows;

/// <summary>
/// WPF does not clamp a huge CornerRadius, so a "999" capsule turns into an ellipse.
/// Clamps any radius to half of the element's shorter side, which makes it a true pill.
/// </summary>
public sealed class PillRadiusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        CornerRadius radius = values[0] is CornerRadius r ? r : new CornerRadius(10);
        double height = values[1] is double h && !double.IsNaN(h) ? h : 0;
        double width = values[2] is double w && !double.IsNaN(w) ? w : 0;
        double limit = Math.Max(0, Math.Min(height, width) / 2);
        if (limit <= 0)
        {
            return radius;
        }
        return new CornerRadius(
            Math.Min(radius.TopLeft, limit), Math.Min(radius.TopRight, limit),
            Math.Min(radius.BottomRight, limit), Math.Min(radius.BottomLeft, limit));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

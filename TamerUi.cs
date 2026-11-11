using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SapphireNotch.Windows;

/// <summary>Small builders shared by the Tamer tab and the App Tamer options window.</summary>
internal static class TamerUi
{
    public static object Res(string key) => Application.Current.FindResource(key);

    private static readonly Dictionary<string, Brush> BrushCache = new();

    public static Brush Hex(string color)
    {
        if (!BrushCache.TryGetValue(color, out Brush? brush))
        {
            brush = (Brush)new BrushConverter().ConvertFromString(color)!;
            brush.Freeze();
            BrushCache[color] = brush;
        }
        return brush;
    }

    public static ToggleButton Switch(bool on, Action<bool> set)
    {
        ToggleButton toggle = new()
        {
            Style = (Style)Res("Switch"),
            IsChecked = on,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        toggle.Click += (_, e) =>
        {
            set(toggle.IsChecked == true);
            e.Handled = true;
        };
        return toggle;
    }

    /// <summary>A drop-down with fixed choices; picks the one nearest to <paramref name="current"/>.</summary>
    public static ComboBox Choice(double[] values, Func<double, string> label, double current, Action<double> set, List<ComboBox>? track = null)
    {
        ComboBox box = new() { Height = 26, MinWidth = 92, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        int nearest = 0;
        for (int i = 0; i < values.Length; i++)
        {
            box.Items.Add(label(values[i]));
            if (Math.Abs(values[i] - current) < Math.Abs(values[nearest] - current))
            {
                nearest = i;
            }
        }
        box.SelectedIndex = nearest;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                set(values[box.SelectedIndex]);
            }
        };
        track?.Add(box);
        return box;
    }

    /// <summary>A line with a switch, a label and an optional drop-down.</summary>
    public static Grid Line(ToggleButton? toggle, string text, ComboBox? choice = null, double indent = 0)
    {
        Grid grid = new() { Margin = new Thickness(indent, 0, 0, 7), MinHeight = 26 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (toggle != null)
        {
            grid.Children.Add(toggle);
        }
        TextBlock label = new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (choice != null)
        {
            Grid.SetColumn(choice, 2);
            grid.Children.Add(choice);
        }
        return grid;
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.Medium,
        Foreground = (Brush)Res("Dim"),
        Margin = new Thickness(0, 6, 0, 7)
    };

    public static Button Pill(string text, Action click)
    {
        Button button = new() { Content = text, Style = (Style)Res("SecondaryButton"), Height = 28, Padding = new Thickness(14, 0, 14, 0), FontSize = 11.5 };
        button.Click += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return button;
    }

    public static string Minutes(double v) => v == 1 ? "1 minute" : $"{v:0} minutes";

    public static string Seconds(double v) => v == 1 ? "1 second" : v % 60 == 0 && v >= 60 ? Minutes(v / 60) : $"{v:0} seconds";
}

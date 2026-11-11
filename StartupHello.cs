using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SapphireNotch.Windows;

/// <summary>
/// Start-up animation: the screen dims and "hello" is written across it in one pen stroke, then everything fades away.
/// A WPF port of the Apple-style "hello" handwriting effect. It never takes focus and clicks pass straight through.
/// </summary>
internal sealed class StartupHello : Window
{
    // "h" and "ello" as two strokes, on a 638 x 200 canvas
    private const string StrokeH = "M8.69214 166.553C36.2393 151.239 61.3409 131.548 89.8191 98.0295C109.203 75.1488 119.625 49.0228 120.122 31.0026C120.37 17.6036 113.836 7.43883 101.759 7.43883C88.3598 7.43883 79.9231 17.6036 74.7122 40.9363C69.005 66.5793 64.7866 96.0036 54.1166 190.356";
    private const string StrokeEllo = "M55.1624 181.135C60.6251 133.114 81.4118 98.0479 107.963 98.0479C123.844 98.0479 133.937 110.703 131.071 128.817C129.457 139.487 127.587 150.405 125.408 163.06C122.869 178.941 130.128 191.348 152.122 191.348C184.197 191.348 219.189 173.523 237.097 145.915C243.198 136.509 245.68 128.073 245.928 119.884C246.176 104.996 237.739 93.8296 222.851 93.8296C203.992 93.8296 189.6 115.17 189.6 142.465C189.6 171.745 205.481 192.341 239.208 192.341C285.066 192.341 335.86 137.292 359.199 75.8585C365.788 58.513 368.26 42.4065 368.26 31.1512C368.26 17.8057 364.042 7.55823 352.131 7.55823C340.469 7.55823 332.777 16.6141 325.829 30.9129C317.688 47.4967 311.667 71.4162 309.203 98.4549C303 166.301 316.896 191.348 349.936 191.348C390 191.348 434.542 135.534 457.286 75.6686C463.803 58.513 466.275 42.4065 466.275 31.1512C466.275 17.8057 462.057 7.55823 450.146 7.55823C438.484 7.55823 430.792 16.6141 423.844 30.9129C415.703 47.4967 409.682 71.4162 407.218 98.4549C401.015 166.301 414.911 191.348 444.416 191.348C473.874 191.348 489.877 165.67 499.471 138.402C508.955 111.447 520.618 94.8221 544.935 94.8221C565.035 94.8221 580.916 109.71 580.916 137.75C580.916 168.768 560.792 192.093 535.362 192.341C512.984 192.589 498.285 174.475 499.774 147.179C501.511 116.907 519.873 94.8221 543.943 94.8221C557.839 94.8221 569.51 100.999 578.682 107.725C603.549 125.866 622.709 114.656 630.047 96.7186";

    private const double Thickness = 14.8883;
    private const double Speed = 0.85; // below 1 is quicker than the original

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Plays the animation; false when it was skipped.</summary>
    public static bool Play()
    {
        if (!SettingsStore.Current.StartupAnimation || !SystemParameters.ClientAreaAnimation)
        {
            return false;
        }
        // not over a game or a full-screen video
        if (SHQueryUserNotificationState(out int state) == 0 && (state == 2 || state == 3 || state == 4))
        {
            return false;
        }
        new StartupHello().Show();
        return true;
    }

    private StartupHello()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        Grid root = new() { Opacity = 0 };
        // a soft dark veil, brighter in the middle so the writing glows
        root.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromArgb(0x99, 0x0B, 0x10, 0x1E), Color.FromArgb(0xE6, 0x02, 0x04, 0x0A)) { RadiusX = 0.8, RadiusY = 0.8 }
        });

        Geometry hGeometry = Geometry.Parse(StrokeH);
        Geometry elloGeometry = Geometry.Parse(StrokeEllo);
        Canvas canvas = new() { Width = 638, Height = 200 };
        Path h = Pen(hGeometry);
        Path ello = Pen(elloGeometry);
        canvas.Children.Add(h);
        canvas.Children.Add(ello);
        root.Children.Add(new Viewbox
        {
            Child = canvas,
            Stretch = Stretch.Uniform,
            Width = Math.Min(640, Width * 0.6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x7C, 0xC4, 0xFF), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.75 }
        });
        Content = root;

        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            const int ExStyle = -20;
            // click-through, never activated, hidden from Alt+Tab
            SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | 0x20 | 0x80 | 0x08000000);
        };
        Loaded += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // topmost, above the island, no move/size/activate
            Run(root, h, hGeometry, ello, elloGeometry);
        };
    }

    private static Path Pen(Geometry geometry) => new()
    {
        Data = geometry,
        Stroke = Brushes.White,
        StrokeThickness = Thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Opacity = 0
    };

    private static double LengthOf(Geometry geometry)
    {
        double total = 0;
        foreach (PathFigure figure in geometry.GetFlattenedPathGeometry().Figures)
        {
            Point previous = figure.StartPoint;
            foreach (PathSegment segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly)
                {
                    foreach (Point point in poly.Points)
                    {
                        total += (point - previous).Length;
                        previous = point;
                    }
                }
                else if (segment is LineSegment line)
                {
                    total += (line.Point - previous).Length;
                    previous = line.Point;
                }
            }
        }
        return total;
    }

    private void Run(Grid root, Path h, Geometry hGeometry, Path ello, Geometry elloGeometry)
    {
        Draw(h, hGeometry, 0.0, 0.8 * Speed, new CubicEase { EasingMode = EasingMode.EaseInOut });
        Draw(ello, elloGeometry, 0.7 * Speed, 2.8 * Speed, new CubicEase { EasingMode = EasingMode.EaseInOut });

        // the whole scene: fade in, hold while the writing finishes, fade out, close
        double writingEnds = (0.7 + 2.8) * Speed;
        double holdUntil = writingEnds + 0.55;
        DoubleAnimationUsingKeyFrames scene = new();
        scene.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scene.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
        scene.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(holdUntil))));
        scene.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(holdUntil + 0.55))));
        scene.Completed += (_, _) => Close();
        root.BeginAnimation(OpacityProperty, scene);
    }

    // The stroke is "drawn" by sliding a dash along it; dash sizes are counted in pen widths.
    private static void Draw(Path path, Geometry geometry, double delay, double seconds, IEasingFunction ease)
    {
        double units = LengthOf(geometry) / Thickness + 1;
        path.StrokeDashArray = new DoubleCollection { units, units };
        path.StrokeDashOffset = units;
        TimeSpan begin = TimeSpan.FromSeconds(delay);
        path.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(units, 0, TimeSpan.FromSeconds(seconds)) { BeginTime = begin, EasingFunction = ease });
        path.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds * 0.5)) { BeginTime = begin });
    }
}

using System.Windows;
using System.Windows.Media;

namespace SapphireNotch.Windows;

// The window is a see-through sheet; its "shape" is what takes the mouse: the whole island while open, just the pill when closed.
public partial class MainWindow
{
    private bool shapeReady;

    private void UpdateHitRegion()
    {
        if (source == null)
        {
            return;
        }
        if (!shapeReady)
        {
            // while the window is first created Windows paints it white; show a single pixel until it has drawn itself
            DwmHelper.SetShape(source.Handle, 0, 0, 1, 1);
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double width = ExpandedWidth;
        double height = renderingShell ? ExpandedHeight + TallExtra : ExpandedHeight + (chatTall ? TallExtra : 0.0);
        Rect area = expanded || renderingShell
            ? new Rect(0, 0, width, height)
            : new Rect((width - CompactWidth) / 2 - 8, 0, CompactWidth + 16, CompactHeight + 8);
        DwmHelper.SetShape(source.Handle, (int)Math.Floor(area.Left * dpi.DpiScaleX), (int)Math.Floor(area.Top * dpi.DpiScaleY),
            (int)Math.Ceiling(area.Right * dpi.DpiScaleX), (int)Math.Ceiling(area.Bottom * dpi.DpiScaleY));
    }

    // Lays out every tab once, unseen, so the first time you open the bar nothing has to be built or compiled.
    private void WarmUpLayout()
    {
        UIElement[] panels = { HomePanel, ChatPanel, TamerPanel, MusicPanel, DevPanel, SharePanel, ToolsPanel, PrompterPanel, SystemPanel };
        Visibility[] saved = panels.Select(panel => panel.Visibility).ToArray();
        Visibility surface = ExpandedSurface.Visibility;
        try
        {
            ExpandedSurface.Visibility = Visibility.Visible;
            foreach (UIElement panel in panels)
            {
                panel.Visibility = Visibility.Visible;
            }
            ExpandedSurface.UpdateLayout();
        }
        finally
        {
            for (int i = 0; i < panels.Length; i++)
            {
                panels[i].Visibility = saved[i];
            }
            ExpandedSurface.Visibility = surface;
        }
    }
}
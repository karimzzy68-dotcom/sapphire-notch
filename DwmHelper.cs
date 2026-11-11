using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace SapphireNotch.Windows;

/// <summary>
/// A see-through window that Windows composites on the graphics card (smooth, in step with the screen),
/// instead of a "layered" window that is copied to the screen by the processor on every frame.
/// </summary>
internal static class DwmHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    public static void MakeTransparent(nint window, HwndTarget? target)
    {
        if (target != null)
        {
            target.BackgroundColor = Colors.Transparent;
        }
        Margins all = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(window, ref all);
        int noRound = 1;                     // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(window, 33, ref noRound, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(window, 34, ref noBorder, sizeof(int));
    }

    /// <summary>Hides or shows the window in the compositor without hiding it from WPF, so the first (white) frame is never seen.</summary>
    public static void Cloak(nint window, bool on)
    {
        int value = on ? 1 : 0; // DWMWA_CLOAK
        DwmSetWindowAttribute(window, 13, ref value, sizeof(int));
    }

    /// <summary>Only this rectangle (window pixels) belongs to the window; clicks outside it reach the apps below.</summary>
    public static void SetShape(nint window, int left, int top, int right, int bottom)
    {
        SetWindowRgn(window, CreateRectRgn(left, top, right, bottom), true);
    }
}

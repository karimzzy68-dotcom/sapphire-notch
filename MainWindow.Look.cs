using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SapphireNotch.Windows;

// Look: one press sends a screenshot of the app you were in to the chat model, which guides you (it never acts for you).
public partial class MainWindow
{
    private const int LookHotkey = 21327;
    private nint lastOtherWindow;
    private bool pendingLook;

    // The notch hides itself from screenshots, so a capture only ever contains your own apps.
    private void TrackForeground()
    {
        nint window = LookNative.GetForegroundWindow();
        if (window == 0 || source == null || window == source.Handle || !LookNative.IsWindowVisible(window) || LookNative.IsIconic(window))
        {
            return;
        }
        LookNative.GetWindowThreadProcessId(window, out uint pid);
        if (pid != Environment.ProcessId)
        {
            lastOtherWindow = window;
        }
    }

    private void Look_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        LookNow(fullScreen: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), fromHotkey: false);
    }

    private void LookNow(bool fullScreen, bool fromHotkey)
    {
        try
        {
            TrackForeground();
            BitmapSource? shot = LookNative.Capture(lastOtherWindow, fullScreen);
            if (fromHotkey)
            {
                ShowTab("Chat");
            }
            if (shot == null)
            {
                AddBubble("Team", "I could not capture the screen just now. Click the app you want help with, then press Look again.", false);
                return;
            }
            if (!SettingsStore.Current.LookNoticeSeen)
            {
                SettingsStore.Current.LookNoticeSeen = true;
                SettingsStore.Save();
                AddBubble("Team", "About Look: it sends one screenshot, only when you press it, to the AI model you picked (through OpenRouter). Nothing is sent until you press Enter, and you can remove it first with the x. Do not use it on screens with passwords or private data.", false);
            }
            SetPendingImage(shot, 1600);
            pendingLook = true;
            ChipText.Text = "Screenshot ready: nothing is sent until you press Enter";
            ChatInput.Focus();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or ArgumentException)
        {
            AddBubble("Team", "Look failed: " + ex.Message, false);
        }
    }
}

internal static class LookNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect rect, int size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);

    /// <summary>The window you were using, or the whole monitor it is on (also when no window is known).</summary>
    public static BitmapSource? Capture(nint window, bool fullScreen)
    {
        Rect area;
        bool haveWindow = window != 0 && !IsIconic(window) && IsWindowVisible(window);
        if (!fullScreen && haveWindow && WindowBounds(window, out area) && area.Right - area.Left >= 120 && area.Bottom - area.Top >= 80)
        {
            // use the window as it is
        }
        else
        {
            MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            if (haveWindow && GetMonitorInfo(MonitorFromWindow(window, 2), ref info))
            {
                area = info.Monitor;
            }
            else
            {
                area = new Rect { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
            }
        }
        int width = area.Right - area.Left, height = area.Bottom - area.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint old = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, width, height, screen, area.Left, area.Top, 0x00CC0020u | 0x40000000u))
            {
                return null;
            }
            BitmapInfoHeader header = new() { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            byte[] pixels = new byte[width * height * 4];
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
            BitmapSource source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, width * 4);
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }
    private static bool WindowBounds(nint window, out Rect rect)
    {
        // the visible frame, without the invisible resize border Windows adds
        if (DwmGetWindowAttribute(window, 9, out rect, Marshal.SizeOf<Rect>()) == 0)
        {
            return true;
        }
        return GetWindowRect(window, out rect);
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SapphireNotch.Windows;

/// <summary>
/// Frosted-glass backdrop: grabs the pixels behind the island (never the island itself),
/// shrinks them hard so they blur, and paints them back smoothly scaled up behind the glass.
/// The grab runs on its own thread so it can never stall the island's spring animation.
/// </summary>
internal sealed class BackdropBlur : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect32 rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    private const uint ExcludeFromCapture = 0x11;
    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;

    // How much smaller the capture is than the screen; bigger = blurrier.
    private const int Shrink = 12;

    private readonly Image target;
    private readonly nint hwnd;
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private WriteableBitmap? bitmap;
    private volatile bool running = true;
    private volatile bool active;
    public volatile int HeightPixels;
    private int uploadPending;
    private byte[] front = Array.Empty<byte>();
    private int frontW, frontH;

    public BackdropBlur(Window window, Image target)
    {
        this.target = target;
        dispatcher = window.Dispatcher;
        hwnd = new WindowInteropHelper(window).Handle;
        // The island must never appear in its own backdrop (it would smear into itself).
        if (Environment.GetEnvironmentVariable("SAPPHIRE_NOEXCLUDE") == null)
        {
            SetWindowDisplayAffinity(hwnd, ExcludeFromCapture);
        }
        Thread worker = new(Loop) { IsBackground = true, Name = "Sapphire backdrop", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>True while the island is open or moving: grab often. Otherwise grab rarely.</summary>
    public bool Active
    {
        get => active;
        set => active = value;
    }

    private readonly object swap = new();

    public void Dispose() => running = false;

    /// <summary>Grab the backdrop right now on the UI thread, so opening never shows an old picture.</summary>
    public void Refresh()
    {
        byte[] fresh = Array.Empty<byte>();
        if (!Grab(ref fresh, out int w, out int h))
        {
            return;
        }
        lock (swap)
        {
            front = fresh;
            frontW = w;
            frontH = h;
        }
        Upload();
    }

    private void Loop()
    {
        byte[] back = Array.Empty<byte>();
        while (running)
        {
            // Reading the screen with GDI makes Windows stall the cursor while it does so, so never do it while the island is just a pill.
            if (!active)
            {
                Thread.Sleep(100);
                continue;
            }
            Thread.Sleep(90);
            if (Interlocked.CompareExchange(ref uploadPending, 0, 0) != 0)
            {
                continue; // the UI has not shown the last frame yet
            }
            if (!Grab(ref back, out int w, out int h))
            {
                continue;
            }
            lock (swap)
            {
                (front, back) = (back, front);
                frontW = w;
                frontH = h;
            }
            Interlocked.Exchange(ref uploadPending, 1);
            dispatcher.BeginInvoke(new Action(Upload), System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private bool Grab(ref byte[] buffer, out int w, out int h)
    {
        w = h = 0;
        if (!GetWindowRect(hwnd, out Rect32 r))
        {
            return false;
        }
        int srcW = r.R - r.L, srcH = r.B - r.T;
        if (HeightPixels > 0)
        {
            srcH = Math.Min(srcH, HeightPixels);
        }
        if (srcW <= 0 || srcH <= 0)
        {
            return false;
        }
        w = Math.Max(8, srcW / Shrink);
        h = Math.Max(4, srcH / Shrink);

        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        nint bmp = CreateCompatibleBitmap(screen, w, h);
        nint old = SelectObject(dc, bmp);
        try
        {
            SetStretchBltMode(dc, Halftone);
            StretchBlt(dc, 0, 0, w, h, screen, r.L, r.T, srcW, srcH, SrcCopy);
            BitmapInfoHeader info = new()
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = -h, // top-down
                Planes = 1,
                BitCount = 32
            };
            if (buffer.Length != w * h * 4)
            {
                buffer = new byte[w * h * 4];
            }
            return GetDIBits(dc, bmp, 0, (uint)h, buffer, ref info, 0) != 0;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    // UI thread: a plain memory copy, a fraction of a millisecond.
    private void Upload()
    {
        try
        {
            if (!running || frontW == 0)
            {
                return;
            }
            if (bitmap == null || bitmap.PixelWidth != frontW || bitmap.PixelHeight != frontH)
            {
                bitmap = new WriteableBitmap(frontW, frontH, 96, 96, PixelFormats.Bgr32, null);
                target.Source = bitmap;
            }
            lock (swap)
            {
                bitmap.WritePixels(new Int32Rect(0, 0, frontW, frontH), front, frontW * 4, 0);
            }
        }
        finally
        {
            Interlocked.Exchange(ref uploadPending, 0);
        }
    }
}


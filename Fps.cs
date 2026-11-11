using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace SapphireNotch.Windows;

/// <summary>
/// Real frame rate. Windows only shares frame events with administrators, so a tiny elevated copy of this app
/// (started on demand, with one UAC prompt) counts them and reports "pid fps" lines over a pipe.
/// </summary>
internal static class FpsMonitor
{
    private const string PipeName = "SapphireNotchFps";
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Dictionary<int, double> Latest = new();
    private static NamedPipeServerStream? server;
    private static Process? helper;

    public static bool Running { get; private set; }

    public static double Get(int pid)
    {
        lock (Latest)
        {
            return Latest.TryGetValue(pid, out double fps) ? fps : 0;
        }
    }

    /// <summary>Returns false if the user declined the Windows prompt.</summary>
    public static bool Start()
    {
        if (Running)
        {
            return true;
        }
        server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fps-helper") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            server.Dispose();
            server = null;
            return false;
        }
        Running = true;
        NamedPipeServerStream pipe = server;
        _ = Task.Run(async () =>
        {
            try
            {
                await pipe.WaitForConnectionAsync();
                using StreamReader reader = new(pipe);
                while (await reader.ReadLineAsync() is string line)
                {
                    string[] part = line.Split(' ');
                    if (part.Length == 2 && int.TryParse(part[0], out int pid) && double.TryParse(part[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fps))
                    {
                        lock (Latest)
                        {
                            Latest[pid] = fps;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            Running = false;
        });
        return true;
    }

    public static void Stop()
    {
        Running = false;
        try
        {
            server?.Dispose(); // the helper sees the closed pipe and ends
        }
        catch (IOException)
        {
        }
        server = null;
        lock (Latest)
        {
            Latest.Clear();
        }
    }

    /// <summary>The elevated side: count frame-present events per process and send them every second.</summary>
    public static void RunHelper()
    {
        using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.Out);
        pipe.Connect(10000);
        using StreamWriter writer = new(pipe) { AutoFlush = true };
        Dictionary<int, int> counts = new();
        using TraceEventSession session = new("SapphireNotchFpsSession") { StopOnDispose = true };
        session.EnableProvider(Dxgi, TraceEventLevel.Informational);
        session.Source.Dynamic.All += e =>
        {
            if (e.ProviderGuid == Dxgi && (int)e.ID == 42) // Present::Start
            {
                lock (counts)
                {
                    counts[e.ProcessID] = counts.GetValueOrDefault(e.ProcessID) + 1;
                }
            }
        };
        Thread reader = new(() => session.Source.Process()) { IsBackground = true };
        reader.Start();
        try
        {
            while (true)
            {
                Thread.Sleep(1000);
                KeyValuePair<int, int>[] snapshot;
                lock (counts)
                {
                    snapshot = counts.ToArray();
                    counts.Clear();
                }
                foreach (KeyValuePair<int, int> item in snapshot)
                {
                    writer.WriteLine($"{item.Key} {item.Value}");
                }
            }
        }
        catch (IOException)
        {
            // the app closed the pipe: stop tracing
        }
    }
}

/// <summary>A small click-through label in the top-left corner showing the frame rate.</summary>
internal sealed class FpsOverlay : Window
{
    private readonly System.Windows.Controls.TextBlock text = new() { FontSize = 15, Foreground = System.Windows.Media.Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Poppins") };

    public FpsOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 12;
        Top = 12;
        Content = new System.Windows.Controls.Border { Padding = new Thickness(10, 4, 10, 4), Child = text };
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(hwnd, -20);
            SetWindowLongPtr(hwnd, -20, style | 0x20 | 0x80 | 0x08000000); // click-through, tool window, never takes focus
        };
    }

    public void SetFps(double fps) => text.Text = $"{fps:0} FPS";

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

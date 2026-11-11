using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Live CPU / GPU / memory / network readouts for the System tab.
public partial class MainWindow
{
    private const int SparkPoints = 48;
    private const double SparkWidth = 124.0;
    private const double SparkHeight = 24.0;

    private readonly SystemMonitor monitor = new();
    private readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<double> cpuHistory = new();
    private readonly Queue<double> gpuHistory = new();
    private readonly Queue<double> memHistory = new();
    private readonly Queue<double> netHistory = new();
    private bool statsReady;

    private void InitStats()
    {
        if (statsReady)
        {
            return;
        }
        statsReady = true;
        statsTimer.Tick += (_, _) => SampleStats();
        Closed += (_, _) =>
        {
            statsTimer.Stop();
            monitor.Dispose();
        };
    }

    // Sample only while the island is open: nothing runs in the background when it is collapsed.
    private void StartStats()
    {
        SampleStats();
        statsTimer.Start();
    }

    private void StopStats()
    {
        statsTimer.Stop();
    }

    private void SampleStats()
    {
        monitor.Sample();
        Push(cpuHistory, monitor.CpuPercent);
        Push(gpuHistory, monitor.GpuPercent ?? 0);
        Push(memHistory, monitor.MemoryPercent);
        Push(netHistory, monitor.DownBytesPerSecond + monitor.UpBytesPerSecond);
        UpdateStatsView();
    }

    private static void Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > SparkPoints)
        {
            history.Dequeue();
        }
    }

    private void UpdateStatsView()
    {
        if (SystemPanel == null || SystemPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        CpuValue.Text = $"{monitor.CpuPercent:0}%";
        CpuDetail.Text = $"{Environment.ProcessorCount} logical cores";
        DrawSpark(CpuLine, CpuArea, cpuHistory, 100);

        if (monitor.GpuPercent is double gpu)
        {
            GpuValue.Text = $"{gpu:0}%";
            GpuDetail.Text = "Busiest GPU engine";
        }
        else
        {
            GpuValue.Text = "N/A";
            GpuDetail.Text = "No GPU counters found";
        }
        DrawSpark(GpuLine, GpuArea, gpuHistory, 100);

        MemValue.Text = $"{monitor.MemoryPercent:0}%";
        MemDetail.Text = $"{monitor.MemoryUsedGb:0.0} of {monitor.MemoryTotalGb:0.0} GB";
        DrawSpark(MemLine, MemArea, memHistory, 100);

        NetValue.Text = "↓ " + FormatRate(monitor.DownBytesPerSecond);
        NetDetail.Text = "↑ " + FormatRate(monitor.UpBytesPerSecond);
        DrawSpark(NetLine, NetArea, netHistory, Math.Max(131072, netHistory.Count == 0 ? 0 : netHistory.Max()));
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:0} B/s";
        }
        if (bytesPerSecond < 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024:0.#} KB/s";
        }
        return $"{bytesPerSecond / (1024 * 1024):0.#} MB/s";
    }

    private static void DrawSpark(Polyline line, Polygon area, Queue<double> history, double max)
    {
        if (history.Count < 2)
        {
            return;
        }
        double step = SparkWidth / (SparkPoints - 1);
        double offset = (SparkPoints - history.Count) * step; // grow in from the right edge
        PointCollection points = new();
        int index = 0;
        foreach (double value in history)
        {
            double y = SparkHeight - 2 - Math.Clamp(value / max, 0, 1) * (SparkHeight - 4);
            points.Add(new Point(offset + index * step, y));
            index++;
        }
        line.Points = points;

        PointCollection fill = new(points)
        {
            new Point(points[^1].X, SparkHeight),
            new Point(points[0].X, SparkHeight)
        };
        area.Points = fill;
    }
}

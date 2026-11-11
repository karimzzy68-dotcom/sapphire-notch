using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SapphireNotch.Windows;

/// <summary>
/// Samples CPU, memory, GPU and network once per call. Native APIs only (no extra packages):
/// GetSystemTimes for CPU, GlobalMemoryStatusEx for memory, the PDH "GPU Engine" counters
/// for GPU (the same source Task Manager uses) and NetworkInterface byte counters for traffic.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    public double CpuPercent { get; private set; }
    public double MemoryPercent { get; private set; }
    public double MemoryUsedGb { get; private set; }
    public double MemoryTotalGb { get; private set; }
    public double? GpuPercent { get; private set; }
    public double DownBytesPerSecond { get; private set; }
    public double UpBytesPerSecond { get; private set; }
    public double LinkBitsPerSecond { get; private set; }

    private ulong lastIdle, lastKernel, lastUser;
    private bool haveCpuBaseline;

    private long lastNetReceived, lastNetSent;
    private long lastNetTicks;

    private nint gpuQuery;
    private nint gpuCounter;
    private bool gpuUnavailable;

    public void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleNetwork();
        SampleGpu();
    }

    // ---- CPU ----------------------------------------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    private void SampleCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            return;
        }
        if (haveCpuBaseline)
        {
            ulong idleDelta = idle - lastIdle;
            ulong total = (kernel - lastKernel) + (user - lastUser); // kernel time includes idle
            if (total > 0)
            {
                CpuPercent = Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
            }
        }
        lastIdle = idle;
        lastKernel = kernel;
        lastUser = user;
        haveCpuBaseline = true;
    }

    // ---- Memory -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private void SampleMemory()
    {
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return;
        }
        const double gb = 1024.0 * 1024.0 * 1024.0;
        MemoryTotalGb = status.TotalPhys / gb;
        MemoryUsedGb = (status.TotalPhys - status.AvailPhys) / gb;
        MemoryPercent = status.MemoryLoad;
    }

    // ---- Network ------------------------------------------------------------------------

    private void SampleNetwork()
    {
        long received = 0, sent = 0;
        double fastest = 0;
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
                fastest = Math.Max(fastest, nic.Speed);
            }
        }
        catch (NetworkInformationException)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (lastNetTicks != 0 && received >= lastNetReceived && sent >= lastNetSent)
        {
            double seconds = Stopwatch.GetElapsedTime(lastNetTicks, now).TotalSeconds;
            if (seconds > 0.05)
            {
                DownBytesPerSecond = (received - lastNetReceived) / seconds;
                UpBytesPerSecond = (sent - lastNetSent) / seconds;
            }
        }
        else
        {
            DownBytesPerSecond = 0;
            UpBytesPerSecond = 0;
        }
        lastNetReceived = received;
        lastNetSent = sent;
        lastNetTicks = now;
        LinkBitsPerSecond = fastest;
    }

    // ---- GPU (PDH "GPU Engine" counters) ------------------------------------------------

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    private void SampleGpu()
    {
        if (gpuUnavailable)
        {
            return;
        }
        try
        {
            if (gpuQuery == 0)
            {
                if (PdhOpenQuery(null, 0, out gpuQuery) != 0
                    || PdhAddEnglishCounterW(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out gpuCounter) != 0)
                {
                    gpuUnavailable = true;
                    return;
                }
                PdhCollectQueryData(gpuQuery); // first collect only primes the rate counters
                return;
            }
            if (PdhCollectQueryData(gpuQuery) != 0)
            {
                return;
            }

            uint size = 0, count = 0;
            if (PdhGetFormattedCounterArrayW(gpuCounter, PdhFmtDouble, ref size, ref count, 0) != PdhMoreData || size == 0)
            {
                return;
            }
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(gpuCounter, PdhFmtDouble, ref size, ref count, buffer) != 0)
                {
                    return;
                }
                // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then status(4)+pad(4)+double(8)
                const int itemSize = 24;
                Dictionary<string, double> byEngine = new();
                for (int i = 0; i < count; i++)
                {
                    nint item = buffer + i * itemSize;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    int engine = name?.IndexOf("engtype_", StringComparison.Ordinal) ?? -1;
                    if (name == null || engine < 0)
                    {
                        continue;
                    }
                    string key = name[engine..];
                    byEngine[key] = byEngine.GetValueOrDefault(key) + value;
                }
                // Like Task Manager, report the busiest engine type
                GpuPercent = byEngine.Count == 0 ? 0 : Math.Clamp(byEngine.Values.Max(), 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            gpuUnavailable = true;
            GpuPercent = null;
        }
    }

    public void Dispose()
    {
        if (gpuQuery != 0)
        {
            PdhCloseQuery(gpuQuery);
            gpuQuery = 0;
        }
    }
}

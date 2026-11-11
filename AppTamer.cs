using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace SapphireNotch.Windows;

/// <summary>What App Tamer does to one program when it is not the app you are using.</summary>
public sealed class TamerRule
{
    public string App { get; set; } = "";
    public string Mode { get; set; } = "";             // legacy (v1/v2 lists); migrated on start
    public bool Stop { get; set; }                     // stop this app completely
    public bool Efficiency { get; set; } = true;       // run on the efficiency cores (EcoQoS + lower priority)
    public bool SlowOn { get; set; }                   // slow down if it uses more than SlowLimit
    public double SlowLimit { get; set; } = 2;         // percent of the whole CPU (Task Manager scale)
    public bool KeepWithSound { get; set; } = true;    // don't stop or slow while it plays sound
    public bool OnlyHidden { get; set; }               // only stop or slow while its windows are minimized
    public bool Boost { get; set; }                    // raise its priority while you are using it
    public int HideAfter { get; set; }                 // minutes unused before its windows are minimized (0 = off)
    public int QuitAfter { get; set; }                 // minutes unused before it is closed (0 = off)

    public bool HasEffect => Stop || Efficiency || SlowOn || Boost || HideAfter > 0 || QuitAfter > 0;
}

/// <summary>App Tamer preferences (the General, Control, Detection, Stats and Options tabs).</summary>
public sealed class TamerOptions
{
    public bool NoSlowWhenPlugged { get; set; }
    public int PlugBattery { get; set; } = 80;
    public bool NoSlowWhenIdle { get; set; } = true;
    public int IdleMinutes { get; set; } = 10;
    public int StartDelay { get; set; } = 5;
    public int SlowDelay { get; set; } = 15;
    public int WakeEveryMinutes { get; set; } = 5;
    public bool HideWhenStopped { get; set; } = true;

    public int HogPercent { get; set; } = 30;   // percent of the whole CPU
    public int HogSeconds { get; set; } = 30;
    public List<string> HogIgnore { get; set; } = new();

    public int UpdateEvery { get; set; } = 5;
    public int AverageOver { get; set; } = 30;
    public double TotalHours { get; set; }
    public double SavedHours { get; set; }
    public int AutoQuit { get; set; }

    public bool ShowGraph { get; set; } = true;
}

/// <summary>What the Tamer tab shows for one running program.</summary>
internal sealed class TamerApp
{
    public string Name { get; init; } = "";
    public double Cpu { get; init; }           // percent of the whole CPU (as in Task Manager), right now
    public double Avg { get; init; }           // average over the Stats window
    public bool HasWindow { get; init; }
    public bool InFront { get; init; }
    public bool Managed { get; init; }
    public bool System { get; init; }
    public double Limit { get; init; }         // "slow down above" in % of the whole CPU, 0 when not set
    public double Saving { get; init; }        // percent of its normal CPU use that is being saved
    public string State { get; init; } = "";   // "", "Slowed", "Stopped", "Boosted"
}

internal readonly record struct TamerSample(double Total, double Tamed, double Saved);

/// <summary>
/// Works like St. Clair's App Tamer: when an app is not the one you are using it can be stopped, moved to the
/// efficiency cores or slowed above a limit, hidden or quit after a while, and it gets full speed the moment you
/// come back. It also watches for CPU hogs. Everything is undone when the tamer is switched off or Sapphire closes.
/// </summary>
internal static class AppTamer
{
    // Never touched, whatever the list says.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "audiodg", "sihost", "taskhostw", "ctfmon", "searchhost",
        "startmenuexperiencehost", "shellexperiencehost", "runtimebroker", "msmpeng", "securityhealthservice",
        "sapphire notch", "claude", "conhost", "powershell", "pwsh", "cmd", "windowsterminal", "applicationframehost",
        "textinputhost", "lockapp", "wudfhost", "dllhost", "spoolsv", "msedgewebview2"
    };

    // When one of these is in front, the app you were using still counts as "in front".
    private static readonly HashSet<string> IgnoreAsFront = new(StringComparer.OrdinalIgnoreCase)
    {
        "sapphire notch", "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost", "textinputhost", "lockapp"
    };

    private static readonly TimeSpan HogCooldown = TimeSpan.FromMinutes(15);
    private const int HistoryLength = 60;

    private const uint AccessMask = 0x0001 | 0x0100 | 0x0200 | 0x0800 | 0x1000; // job, quota, set info, suspend, query
    private const uint AboveNormal = 0x8000, BelowNormal = 0x4000, Normal = 0x20;

    private static readonly string PausedFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh", "tamer-paused.txt");

    private sealed class AppData
    {
        public string Name = "";
        public HashSet<int> Pids = new();
        public double CpuDelta;                 // CPU seconds used since the last scan
        public double Cpu;
        public double Avg;
        public Queue<double> Hist = new();
        public bool HasWindow;
        public bool System;
        public DateTime? BackgroundSince;
        public double Baseline;                 // what it uses when left alone
        public double HogSeconds;
        public DateTime? StoppedSince;
        public DateTime? WakeUntil;
        public bool Hid, Quit;
        public bool Seeded;
    }

    private sealed class Applied
    {
        public string Name = "";
        public string State = "";               // "", "Boost", "Stopped", "Slow|eco|cap"
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, AppData> Apps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, Applied> PidState = new();
    private static readonly Dictionary<string, nint> Jobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, TimeSpan> LastCpu = new();
    private static readonly Dictionary<string, DateTime> LastHogAlert = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<TamerSample> Samples = new();
    private static Timer? timer;
    private static DateTime lastTick, lastScan, startAt;
    private static ulong lastIdle, lastBusy;
    private static int tick;
    private static string frontName = "";

    public static List<TamerApp> Snapshot { get; private set; } = new();
    public static TamerSample[] History { get; private set; } = Array.Empty<TamerSample>();
    public static TamerSample Now { get; private set; }
    public static int AffectedProcesses { get; private set; }

    public static event Action? Updated;                    // UI thread
    public static event Action<string, double>? HogFound;   // UI thread: app name, CPU in % of one core

    public static bool IsProtected(string app) => Protected.Contains(app);

    public static bool Running
    {
        get
        {
            lock (Gate)
            {
                return timer != null;
            }
        }
    }

    /// <summary>Turns lists from earlier versions (one "Mode" per app) into the new per-app options.</summary>
    public static bool MigrateRules()
    {
        bool changed = false;
        foreach (TamerRule rule in SettingsStore.Current.TamerRules)
        {
            if (rule.Mode.Length == 0)
            {
                continue;
            }
            rule.Stop = false;
            rule.Efficiency = false;
            rule.SlowOn = false;
            rule.Boost = false;
            switch (rule.Mode)
            {
                case "Boost":
                    rule.Boost = true;
                    break;
                case "Pause in background":
                    rule.Stop = true;
                    break;
                case "Cap 50%" or "Always slow":
                    rule.SlowOn = true;
                    rule.SlowLimit = 50;
                    rule.Efficiency = true;
                    break;
                case "Cap 25%":
                    rule.SlowOn = true;
                    rule.SlowLimit = 25;
                    rule.Efficiency = true;
                    break;
                case "Off":
                    break;
                default: // "Low", "Slow in background"
                    rule.Efficiency = true;
                    break;
            }
            rule.Mode = "";
            changed = true;
        }
        return changed;
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (timer != null)
            {
                return;
            }
            lastTick = DateTime.UtcNow;
            lastScan = DateTime.UtcNow.AddMinutes(-1);
            startAt = DateTime.UtcNow;
            tick = 0;
            timer = new Timer(_ => Tick(), null, 300, 1000);
        }
    }

    /// <summary>Stop and put every program back the way it was.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            timer?.Dispose();
            timer = null;
            foreach (KeyValuePair<int, Applied> entry in PidState.ToList())
            {
                Transition(entry.Key, entry.Value.Name, entry.Value.State, "");
            }
            PidState.Clear();
            foreach (nint job in Jobs.Values)
            {
                SetCap(job, 100);
            }
            SavePaused();
            AffectedProcesses = 0;
            Snapshot = new List<TamerApp>();
        }
        SaveSettings();
        Raise();
    }

    /// <summary>Apply a changed list right away.</summary>
    public static void Reapply()
    {
        if (Running)
        {
            Task.Run(Tick);
        }
    }

    /// <summary>If Sapphire was killed while apps were stopped, wake them up (called once at start-up).</summary>
    public static void RecoverPaused()
    {
        try
        {
            if (!File.Exists(PausedFile))
            {
                return;
            }
            foreach (string line in File.ReadAllLines(PausedFile))
            {
                string[] parts = line.Split('|');
                if (parts.Length == 3 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[2], out long started))
                {
                    try
                    {
                        using Process process = Process.GetProcessById(pid);
                        if (process.ProcessName == parts[1] && process.StartTime.Ticks == started)
                        {
                            nint handle = OpenProcess(AccessMask, false, pid);
                            if (handle != 0)
                            {
                                NtResumeProcess(handle);
                                CloseHandle(handle);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // already gone
                    }
                }
            }
            File.Delete(PausedFile);
        }
        catch (IOException)
        {
        }
    }

    // ---- The loop --------------------------------------------------------------------------

    private static void Tick()
    {
        string? hogName = null;
        double hogCpu = 0;
        lock (Gate)
        {
            if (timer == null)
            {
                return;
            }
            TamerOptions opt = SettingsStore.Current.Tamer;
            DateTime now = DateTime.UtcNow;
            double sinceTick = (now - lastTick).TotalSeconds;
            if (sinceTick > 15)
            {
                startAt = now; // woke from sleep: give apps a moment before touching them
            }
            double dt = Math.Min(5, sinceTick);
            lastTick = now;
            tick++;
            opt.TotalHours += dt / 3600.0;
            UpdateFront();

            Dictionary<string, TamerRule> wanted = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (TamerRule rule in SettingsStore.Current.TamerRules.ToList())
                {
                    if (rule.App.Length > 0 && !IsProtected(rule.App) && !wanted.ContainsKey(rule.App))
                    {
                        wanted[rule.App] = rule;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return; // the list was being edited; try again next second
            }

            bool scan = (now - lastScan).TotalSeconds >= Math.Max(1, opt.UpdateEvery) - 0.2;
            double scanSeconds = Math.Max(0.5, (now - lastScan).TotalSeconds);
            if (scan)
            {
                lastScan = now;
            }

            // gather processes
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            HashSet<int> alive = new();
            int own = Environment.ProcessId;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    int pid = process.Id;
                    string name;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (pid == own || (pid <= 4 && !scan))
                    {
                        continue;
                    }
                    bool managed = wanted.ContainsKey(name);
                    if (!managed && !scan)
                    {
                        continue;
                    }
                    if (!Apps.TryGetValue(name, out AppData? data))
                    {
                        data = new AppData { Name = name, System = IsProtected(name) };
                        Apps[name] = data;
                    }
                    if (visited.Add(name))
                    {
                        data.Pids.Clear();
                        data.CpuDelta = 0;
                        if (scan)
                        {
                            data.HasWindow = false;
                        }
                    }
                    data.Pids.Add(pid);
                    alive.Add(pid);

                    if (scan)
                    {
                        try
                        {
                            TimeSpan cpu = process.TotalProcessorTime;
                            if (LastCpu.TryGetValue(pid, out TimeSpan before))
                            {
                                data.CpuDelta += Math.Max(0, (cpu - before).TotalSeconds);
                            }
                            LastCpu[pid] = cpu;
                            if (process.MainWindowHandle != 0)
                            {
                                data.HasWindow = true;
                            }
                        }
                        catch (Exception)
                        {
                            // access denied or the process just ended
                        }
                    }
                }
            }
            foreach (string gone in Apps.Keys.Where(n => !visited.Contains(n) && (scan || wanted.ContainsKey(n))).ToList())
            {
                Apps.Remove(gone);
            }

            // programs whose rule was removed, or that have ended
            foreach (KeyValuePair<int, Applied> entry in PidState.ToList())
            {
                if (!wanted.ContainsKey(entry.Value.Name))
                {
                    Transition(entry.Key, entry.Value.Name, entry.Value.State, "");
                    PidState.Remove(entry.Key);
                }
                else if (!alive.Contains(entry.Key))
                {
                    PidState.Remove(entry.Key);
                }
            }

            // conditions under which nothing is slowed or stopped
            // in a full-screen game (Game mode) background apps are limited at once, whatever the delays say
            bool game = GameActive;
            bool allowed = game || ((now - startAt).TotalSeconds >= opt.StartDelay && !BlockedByPower(opt) && !BlockedByIdle(opt));
            HashSet<int> sound = wanted.Values.Any(r => r.KeepWithSound && (r.Stop || r.SlowOn || r.Efficiency || r.QuitAfter > 0)) ? PlayingPids() : new HashSet<int>();
            HashSet<int> shown = wanted.Values.Any(r => r.OnlyHidden) ? VisiblePids() : new HashSet<int>();

            foreach (AppData app in Apps.Values.Where(a => visited.Contains(a.Name)))
            {
                if (!wanted.TryGetValue(app.Name, out TamerRule? rule))
                {
                    continue;
                }
                bool front = string.Equals(app.Name, frontName, StringComparison.OrdinalIgnoreCase);
                if (front)
                {
                    app.BackgroundSince = null;
                    app.Hid = false;
                    app.Quit = false;
                    app.StoppedSince = null;
                    app.WakeUntil = null;
                }
                else
                {
                    app.BackgroundSince ??= now;
                }
                double away = app.BackgroundSince is DateTime since ? (now - since).TotalSeconds : 0;
                bool soundBlocks = rule.KeepWithSound && app.Pids.Any(sound.Contains);
                bool hiddenBlocks = rule.OnlyHidden && app.Pids.Any(shown.Contains);

                string want = "";
                if (front)
                {
                    want = rule.Boost ? "Boost" : "";
                }
                else if (allowed && (game || away >= opt.SlowDelay) &&!soundBlocks && !hiddenBlocks)
                {
                    if (rule.Stop)
                    {
                        want = "Stopped";
                    }
                    else if (rule.Efficiency || rule.SlowOn)
                    {
                        want = Slow(rule.Efficiency, rule.SlowOn ? rule.SlowLimit : 0);
                    }
                }

                if (want == "Stopped")
                {
                    app.StoppedSince ??= now;
                    if (app.WakeUntil is DateTime wake)
                    {
                        if (now < wake)
                        {
                            want = Slow(true, 0); // awake for a moment so it can refresh
                        }
                        else
                        {
                            app.WakeUntil = null;
                            app.StoppedSince = now;
                        }
                    }
                    else if ((now - app.StoppedSince.Value).TotalMinutes >= Math.Max(1, opt.WakeEveryMinutes))
                    {
                        app.WakeUntil = now.AddSeconds(3);
                        want = Slow(true, 0);
                    }
                }
                else
                {
                    app.StoppedSince = null;
                    app.WakeUntil = null;
                }

                // when idle: hide, then quit
                if (!front && allowed && !soundBlocks)
                {
                    if (rule.QuitAfter > 0 && away >= rule.QuitAfter * 60.0 && !app.Quit)
                    {
                        app.Quit = true;
                        opt.AutoQuit++;
                        want = ""; // a stopped app cannot close itself
                        CloseApp(app.Name);
                    }
                    else if (rule.HideAfter > 0 && away >= rule.HideAfter * 60.0 && !app.Hid)
                    {
                        app.Hid = true;
                        MinimizeWindows(app.Pids);
                    }
                }

                foreach (int pid in app.Pids)
                {
                    string current = PidState.TryGetValue(pid, out Applied? state) ? state.State : "";
                    if (current == want)
                    {
                        continue;
                    }
                    if (Transition(pid, app.Name, current, want))
                    {
                        if (want.Length == 0)
                        {
                            PidState.Remove(pid);
                        }
                        else
                        {
                            PidState[pid] = new Applied { Name = app.Name, State = want };
                        }
                        if (want == "Stopped" && opt.HideWhenStopped)
                        {
                            MinimizeWindows(new HashSet<int> { pid });
                        }
                    }
                }
            }

            // lift the CPU limit of apps that are no longer slowed
            foreach (string name in Jobs.Keys.ToList())
            {
                bool limited = PidState.Values.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && CapOf(p.State) > 0);
                if (!limited)
                {
                    SetCap(Jobs[name], 100);
                }
            }

            if (scan)
            {
                foreach (int gone in LastCpu.Keys.Where(p => !alive.Contains(p)).ToList())
                {
                    LastCpu.Remove(gone);
                }
                double total = SystemCpu();
                double tamed = 0, saved = 0;
                foreach (AppData app in Apps.Values)
                {
                    app.Cpu = app.CpuDelta / scanSeconds * 100.0 / Environment.ProcessorCount; // same scale as Task Manager: share of the whole CPU
                    app.Hist.Enqueue(app.Cpu);
                    int keep = Math.Max(1, (int)Math.Round(Math.Max(1, opt.AverageOver) / (double)Math.Max(1, opt.UpdateEvery)));
                    while (app.Hist.Count > keep)
                    {
                        app.Hist.Dequeue();
                    }
                    app.Avg = app.Hist.Average();

                    bool isTamed = app.Pids.Any(p => PidState.TryGetValue(p, out Applied? s) && (s.State == "Stopped" || s.State.StartsWith("Slow", StringComparison.Ordinal)));
                    if (isTamed)
                    {
                        tamed += app.Cpu;
                        saved += Math.Max(0, app.Baseline - app.Cpu);
                    }
                    else
                    {
                        app.Baseline = app.Seeded ? app.Baseline * 0.6 + app.Cpu * 0.4 : app.Cpu;
                        app.Seeded = true;
                    }
                }
                opt.SavedHours += saved / 100.0 * Environment.ProcessorCount * scanSeconds / 3600.0; // in core-hours
                Samples.Add(new TamerSample(total, tamed, saved));
                while (Samples.Count > HistoryLength)
                {
                    Samples.RemoveAt(0);
                }
                History = Samples.ToArray();
                Now = Samples[^1];

                // CPU hogs: busy, not managed, not the app you are using
                foreach (AppData app in Apps.Values)
                {
                    bool hot = app.Cpu >= opt.HogPercent && !app.System && !wanted.ContainsKey(app.Name)
                        && !string.Equals(app.Name, frontName, StringComparison.OrdinalIgnoreCase)
                        && !opt.HogIgnore.Contains(app.Name, StringComparer.OrdinalIgnoreCase);
                    app.HogSeconds = hot ? app.HogSeconds + scanSeconds : 0;
                    if (app.HogSeconds >= opt.HogSeconds
                        && (!LastHogAlert.TryGetValue(app.Name, out DateTime last) || DateTime.UtcNow - last > HogCooldown))
                    {
                        LastHogAlert[app.Name] = DateTime.UtcNow;
                        hogName = app.Name;
                        hogCpu = app.Cpu;
                    }
                }
            }

            SavePaused();
            AffectedProcesses = PidState.Count;
            Snapshot = Apps.Values
                .Where(a => a.HasWindow || a.Cpu >= 0.5 || wanted.ContainsKey(a.Name))
                .Select(a => new TamerApp
                {
                    Name = a.Name,
                    Cpu = a.Cpu,
                    Avg = a.Avg,
                    HasWindow = a.HasWindow,
                    InFront = string.Equals(a.Name, frontName, StringComparison.OrdinalIgnoreCase),
                    Managed = wanted.ContainsKey(a.Name),
                    System = a.System,
                    Limit = wanted.TryGetValue(a.Name, out TamerRule? r) && r.SlowOn ? r.SlowLimit : 0,
                    State = StateLabel(a),
                    Saving = a.Baseline > 0.5 ? Math.Clamp((a.Baseline - a.Cpu) / a.Baseline * 100.0, 0, 100) : 0
                })
                .ToList();

            if (tick % 300 == 0)
            {
                SaveSettings();
            }
        }
        Raise();
        if (hogName != null)
        {
            string name = hogName;
            double cpuNow = hogCpu;
            Application.Current?.Dispatcher.BeginInvoke(() => HogFound?.Invoke(name, cpuNow));
        }
    }

    private static string Slow(bool eco, double limit) => $"Slow|{(eco ? 1 : 0)}|{limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static double CapOf(string state)
    {
        string[] parts = state.Split('|');
        return parts.Length == 3 && parts[0] == "Slow" && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cap) ? cap : 0;
    }

    private static string StateLabel(AppData app)
    {
        string state = app.Pids.Select(p => PidState.TryGetValue(p, out Applied? s) ? s.State : "").FirstOrDefault(s => s.Length > 0) ?? "";
        return state switch
        {
            "Boost" => "Boosted",
            "Stopped" => "Stopped",
            { Length: > 0 } => "Slowed",
            _ => ""
        };
    }

    /// <summary>Set by Game mode while a full-screen game has the screen.</summary>
    public static volatile bool GameActive;

    private static void UpdateFront()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            return;
        }
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            string name = process.ProcessName;
            if (!IgnoreAsFront.Contains(name))
            {
                frontName = name;
            }
        }
        catch (Exception)
        {
            // the window closed
        }
    }

    private static void SaveSettings()
    {
        Application.Current?.Dispatcher.BeginInvoke(SettingsStore.Save);
    }

    // ---- Conditions ------------------------------------------------------------------------

    private static bool BlockedByPower(TamerOptions opt)
    {
        if (!opt.NoSlowWhenPlugged || !GetSystemPowerStatus(out PowerStatus power))
        {
            return false;
        }
        bool hasBattery = (power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100;
        return hasBattery && power.AcLineStatus == 1 && power.BatteryLifePercent > opt.PlugBattery;
    }

    private static bool BlockedByIdle(TamerOptions opt)
    {
        if (!opt.NoSlowWhenIdle)
        {
            return false;
        }
        LastInput info = new() { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info))
        {
            return false;
        }
        double idleMinutes = unchecked((uint)Environment.TickCount - info.Time) / 60000.0;
        return idleMinutes >= opt.IdleMinutes;
    }

    private static double SystemCpu()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            return 0;
        }
        ulong i = idle.Value, b = kernel.Value + user.Value; // kernel time includes idle time
        double result = 0;
        if (lastBusy != 0 && b > lastBusy)
        {
            result = Math.Clamp((1.0 - (double)(i - lastIdle) / (b - lastBusy)) * 100.0, 0, 100);
        }
        lastIdle = i;
        lastBusy = b;
        return result;
    }

    // ---- Windows and sound -----------------------------------------------------------------

    private static HashSet<int> VisiblePids()
    {
        HashSet<int> pids = new();
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && !IsIconic(window) && GetWindowTextLength(window) > 0)
            {
                GetWindowThreadProcessId(window, out uint pid);
                pids.Add((int)pid);
            }
            return true;
        }, 0);
        return pids;
    }

    private static void MinimizeWindows(HashSet<int> pids)
    {
        HashSet<int> copy = pids.ToHashSet();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (copy.Contains((int)pid) && IsWindowVisible(window) && GetWindowTextLength(window) > 0)
            {
                ShowWindow(window, 7); // minimize without taking focus
            }
            return true;
        }, 0);
    }

    private static void CloseApp(string name)
    {
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    process.CloseMainWindow(); // a polite close; the app can still ask to save
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>Process ids that are making sound right now (Windows audio sessions with a signal).</summary>
    private static HashSet<int> PlayingPids()
    {
        HashSet<int> pids = new();
        try
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device) != 0)
            {
                return pids;
            }
            Guid iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23, 0, out object managerObject);
            IAudioSessionManager2 manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out IAudioSessionControl2 session);
                session.GetState(out int state);
                if (state == 1 && session.GetProcessId(out uint pid) == 0 && ((IAudioMeterInformation)session).GetPeakValue(out float peak) == 0 && peak > 0.0005f)
                {
                    pids.Add((int)pid);
                }
                Marshal.ReleaseComObject(session);
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        catch (Exception)
        {
            // no audio device, or the audio service is restarting: treat as silence
        }
        return pids;
    }

    // ---- Changing a process ----------------------------------------------------------------

    private static bool Transition(int pid, string app, string from, string to)
    {
        nint handle = OpenProcess(AccessMask, false, pid);
        if (handle == 0)
        {
            return false; // another user's process, or one that runs as administrator
        }
        try
        {
            if (from == "Stopped")
            {
                NtResumeProcess(handle);
            }
            if (to.Length == 0)
            {
                SetPriorityClass(handle, Normal);
                Eco(handle, false);
            }
            else if (to == "Boost")
            {
                SetPriorityClass(handle, AboveNormal);
                Eco(handle, false);
            }
            else if (to == "Stopped")
            {
                SetPriorityClass(handle, BelowNormal);
                NtSuspendProcess(handle);
            }
            else
            {
                bool eco = to.Split('|')[1] == "1";
                SetPriorityClass(handle, BelowNormal);
                Eco(handle, eco);
                // no hard CPU cap on purpose: a job-object cap covers a whole app, outlives this program and starves browsers.
                // Slow above X% therefore means low priority plus efficiency mode, which only yields to other work.
            }
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Remember stopped programs on disk so a crash of Sapphire can never leave them frozen.
    private static void SavePaused()
    {
        try
        {
            List<string> lines = new();
            foreach (KeyValuePair<int, Applied> entry in PidState.Where(e => e.Value.State == "Stopped"))
            {
                try
                {
                    using Process process = Process.GetProcessById(entry.Key);
                    lines.Add($"{entry.Key}|{process.ProcessName}|{process.StartTime.Ticks}");
                }
                catch (Exception)
                {
                }
            }
            if (lines.Count == 0)
            {
                if (File.Exists(PausedFile))
                {
                    File.Delete(PausedFile);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PausedFile)!);
                File.WriteAllLines(PausedFile, lines);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void Raise()
    {
        Application.Current?.Dispatcher.BeginInvoke(() => Updated?.Invoke());
    }

    // ---- Windows calls ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags; // 0x1 enable | 0x4 hard cap
        public uint CpuRate;      // hundredths of a percent of the whole CPU
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    private delegate bool EnumWindowsProc(nint window, nint param);

    private static void SetCap(nint job, double percent)
    {
        CpuRateControl info = new() { ControlFlags = 0x1 | 0x4, CpuRate = (uint)Math.Clamp(Math.Round(percent * 100.0), 1, 10000) };
        SetInformationJobObject(job, 15, ref info, (uint)Marshal.SizeOf<CpuRateControl>());
    }

    private static void Eco(nint process, bool on)
    {
        PowerThrottling state = new() { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
        SetProcessInformation(process, 4, ref state, (uint)Marshal.SizeOf<PowerThrottling>());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(nint process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int infoClass, ref PowerThrottling info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref CpuRateControl info, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint process);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    // ---- Core Audio (to know which apps are playing sound) ---------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint groupingParam, int streamFlags, out nint sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(nint groupingParam, int streamFlags, out nint audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint events);
        [PreserveSig] int UnregisterAudioSessionNotification(nint events);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}

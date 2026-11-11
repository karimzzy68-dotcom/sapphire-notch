using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Agents: shows Claude Code sessions (via a small local hook) and which one is waiting for you.
// Notify-and-ask-first: nothing is ever approved for you; "Open Terminal" takes you to the session to answer there.
public partial class MainWindow
{
    private sealed record AgentSession(string Session, string Cwd, string Project, string Branch, string Status, string Message, DateTime Time, string Agent, long Hwnd = 0);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);

    private readonly HashSet<string> agentsAlerted = new();
    private FileSystemWatcher? agentWatcher;
    private readonly DispatcherTimer agentTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private static string AgentsFolder
    {
        get
        {
            string dir = Path.Combine(DataFolder, "agents");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string ClaudeSettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string HookScript => Path.Combine(AppContext.BaseDirectory, "Tools", "agent-hook.ps1");

    private void InitAgents()
    {
        try
        {
            agentWatcher = new FileSystemWatcher(AgentsFolder, "*.json") { EnableRaisingEvents = true };
            agentWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshAgents));
            agentWatcher.Created += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshAgents));
            agentWatcher.Deleted += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshAgents));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
        }
        agentTimer.Tick += (_, _) => RefreshAgents();
        agentTimer.Start();
        RefreshAgents();
    }

    private List<AgentSession> ReadAgents()
    {
        List<AgentSession> list = new();
        foreach (string file in Directory.EnumerateFiles(AgentsFolder, "*.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement r = doc.RootElement;
                string Get(string name) => r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                DateTime time = DateTime.TryParse(Get("time"), out DateTime t) ? t : File.GetLastWriteTime(file);
                long hwnd = r.TryGetProperty("hwnd", out JsonElement h) && h.ValueKind == JsonValueKind.Number ? h.GetInt64() : 0;
                if (DateTime.Now - time > TimeSpan.FromHours(3))
                {
                    continue; // stale session
                }
                list.Add(new AgentSession(Get("session"), Get("cwd"), Get("project"), Get("branch"), Get("status"), Get("message"), time, Get("agent"), hwnd));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return list.OrderByDescending(a => a.Time).ToList();
    }

    private bool HooksInstalled()
    {
        try
        {
            return File.Exists(ClaudeSettingsFile) && File.ReadAllText(ClaudeSettingsFile).Contains("agent-hook.ps1");
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void RefreshAgents()
    {
        if (AgentList == null)
        {
            return;
        }
        List<AgentSession> sessions = ReadAgents();
        bool installed = HooksInstalled();
        AgentSetupButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        int needs = sessions.Count(s => s.Status == "needs");
        AgentStatusText.Text = !installed
            ? "Set up hooks so Claude Code can tell the notch when it needs you."
            : sessions.Count == 0 ? "No Claude Code sessions yet. Start one and it shows here." : $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}, {needs} need{(needs == 1 ? "s" : "")} you";

        AgentList.Children.Clear();
        AddAgentSection("NEEDS YOU", sessions.Where(s => s.Status == "needs").ToList(), true);
        AddAgentSection("WORKING", sessions.Where(s => s.Status == "working").ToList(), false);
        AddAgentSection("IDLE", sessions.Where(s => s.Status == "idle").ToList(), false);

        // Notify once per request: bring the island up on the Agents list.
        foreach (AgentSession s in sessions.Where(s => s.Status == "needs"))
        {
            if (agentsAlerted.Add(s.Session + s.Time.Ticks))
            {
                if (!silent)
                {
                    Chime.Play();
                }
                if (!expanded)
                {
                    ShowTab("Dev");
                    SelectDevMode("Agents");
                }
            }
        }
    }

    private void AddAgentSection(string title, List<AgentSession> items, bool urgent)
    {
        if (items.Count == 0)
        {
            return;
        }
        AgentList.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Eyebrow"), Margin = new Thickness(4, 4, 0, 4) });
        foreach (AgentSession s in items)
        {
            Border row = new()
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Background = urgent ? new SolidColorBrush(Color.FromArgb(0x26, 0xF5, 0xA5, 0x24)) : (Brush)FindResource("CardBrush"),
                BorderBrush = urgent ? new SolidColorBrush(Color.FromArgb(0x55, 0xF5, 0xA5, 0x24)) : (Brush)FindResource("GlassRim"),
                BorderThickness = new Thickness(1)
            };
            DockPanel dock = new();
            Button open = new()
            {
                Style = (Style)FindResource(urgent ? "PrimaryButton" : "SecondaryButton"),
                Content = "Open Claude",
                Height = 26,
                Padding = new Thickness(14, 0, 14, 0),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            string cwd = s.Cwd;
            long hwnd = s.Hwnd;
            string sessionId = s.Session;
            open.Click += (_, _) => OpenAgent(hwnd, sessionId, cwd);
            DockPanel.SetDock(open, Dock.Right);
            dock.Children.Add(open);
            StackPanel text = new();
            StackPanel head = new() { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = s.Project.Length > 0 ? s.Project : "session", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            if (s.Branch.Length > 0)
            {
                head.Children.Add(new TextBlock { Text = "  " + s.Branch, FontSize = 10.5, Foreground = (Brush)FindResource("Dim3"), VerticalAlignment = VerticalAlignment.Center });
            }
            head.Children.Add(new TextBlock { Text = "  since " + s.Time.ToString("HH:mm"), FontSize = 10.5, Foreground = urgent ? (Brush)FindResource("Amber") : (Brush)FindResource("Dim3"), VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(head);
            text.Children.Add(new TextBlock { Text = s.Message, Style = (Style)FindResource("Sub"), Margin = new Thickness(0, 2, 0, 0) });
            dock.Children.Add(text);
            row.Child = dock;
            AgentList.Children.Add(row);
        }
    }

    // Jumps to the window where this Claude Code session is already running; only if that window is gone,
    // reopens the same conversation (claude --resume) in a new terminal.
    private static void OpenAgent(long hwnd, string sessionId, string cwd)
    {
        if (hwnd != 0 && IsWindow((nint)hwnd))
        {
            // The notch never takes focus, so Windows blocks a plain SetForegroundWindow; a tap of Alt lifts that block.
            ShowWindow((nint)hwnd, IsIconic((nint)hwnd) ? 9 : 5);
            keybd_event(0x12, 0, 0, 0);
            keybd_event(0x12, 0, 2, 0);
            BringWindowToTop((nint)hwnd);
            SetForegroundWindow((nint)hwnd);
            return;
        }
        string dir = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool safeId = sessionId.Length > 0 && sessionId.All(c => char.IsLetterOrDigit(c) || c == '-');
        string resume = safeId ? $"claude --resume {sessionId}" : "claude --continue";
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\" powershell -NoExit -Command \"{resume}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command \"{resume}\"") { WorkingDirectory = dir, UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    // Adds our hook to Claude Code's user settings, only after you confirm, keeping a backup of the old file.
    private void AgentSetup_Click(object sender, RoutedEventArgs e)
    {
        string script = HookScript;
        if (!File.Exists(script))
        {
            MessageBox.Show("The hook script was not found next to the app.", "Sapphire NOTCh");
            return;
        }
        MessageBoxResult answer = MessageBox.Show(
            "This adds 4 small hooks to Claude Code's settings (" + ClaudeSettingsFile + ").\n\n" +
            "They only write a status file on this PC: project, branch and a one-line message. Nothing is sent anywhere, and Claude's permission prompts stay exactly as they are.\n\n" +
            "Your current settings are backed up first. Continue?",
            "Set up Claude Code hooks", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettingsFile)!);
            JsonNode root = File.Exists(ClaudeSettingsFile) ? JsonNode.Parse(File.ReadAllText(ClaudeSettingsFile)) ?? new JsonObject() : new JsonObject();
            if (File.Exists(ClaudeSettingsFile))
            {
                File.Copy(ClaudeSettingsFile, ClaudeSettingsFile + ".sapphire-backup", true);
            }
            JsonObject hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            foreach (string name in new[] { "Notification", "Stop", "UserPromptSubmit", "SessionEnd" })
            {
                JsonArray list = hooks[name] as JsonArray ?? new JsonArray();
                hooks[name] = list;
                if (!list.Any(x => x?.ToJsonString().Contains("agent-hook.ps1") == true))
                {
                    list.Add(new JsonObject
                    {
                        ["hooks"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" {name}"
                        })
                    });
                }
            }
            File.WriteAllText(ClaudeSettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            RefreshAgents();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not update Claude Code settings: " + ex.Message, "Sapphire NOTCh");
        }
    }
}

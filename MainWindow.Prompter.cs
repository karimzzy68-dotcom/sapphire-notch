using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Prompter: scrolling script reader, plus a switch that hides this window from screen sharing and recordings.
public partial class MainWindow
{
    private sealed record PrompterScript(string Name, string Text);

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    private const uint WdaNone = 0x0, WdaExcludeFromCapture = 0x11;

    private readonly List<PrompterScript> prompterScripts = new();
    private int prompterIndex;
    private string prompterSpeed = "Normal";
    private bool prompterLoading, prompterHidden = true;
    private readonly DispatcherTimer prompterTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer prompterSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private DateTime prompterLast;

    private static string PrompterFile => Path.Combine(DataFolder, "prompter.json");

    private static double PrompterPixelsPerSecond(string speed) => speed switch
    {
        "Slow" => 14,
        "Fast" => 46,
        "Sprint" => 80,
        _ => 26
    };

    private void InitPrompter()
    {
        try
        {
            if (File.Exists(PrompterFile))
            {
                prompterScripts.AddRange(JsonSerializer.Deserialize<List<PrompterScript>>(File.ReadAllText(PrompterFile)) ?? new());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        if (prompterScripts.Count == 0)
        {
            prompterScripts.Add(new PrompterScript("Meeting opener",
                "Good morning, everyone, and thanks for joining.\n\nToday I want to cover three things: where we are, what changed, and what comes next.\n\nFirst, where we are. Then I will take questions at the end.\n\nLet's get started."));
            prompterScripts.Add(new PrompterScript("Podcast intro", "Welcome back to the show. Today's guest needs no introduction, so let's jump straight in."));
        }
        prompterSaveTimer.Tick += (_, _) =>
        {
            prompterSaveTimer.Stop();
            SavePrompter();
        };
        prompterTimer.Tick += PrompterTick;
        SelectPrompterScript(0);
        UpdateSelection(SpeedButtons(), prompterSpeed);
        ApplyCaptureHiding(true);
    }

    private Dictionary<string, Button> SpeedButtons() => new()
    {
        ["Slow"] = PrompterSlow,
        ["Normal"] = PrompterNormal,
        ["Fast"] = PrompterFast,
        ["Sprint"] = PrompterSprint
    };

    private void SavePrompter()
    {
        try
        {
            File.WriteAllText(PrompterFile, JsonSerializer.Serialize(prompterScripts));
        }
        catch (IOException)
        {
        }
    }

    private void SelectPrompterScript(int index)
    {
        prompterIndex = Math.Clamp(index, 0, prompterScripts.Count - 1);
        prompterLoading = true;
        PrompterText.Text = prompterScripts[prompterIndex].Text;
        prompterLoading = false;
        PrompterScroll.ScrollToTop();
        PrompterStop();
        RefreshPrompterList();
    }

    private void RefreshPrompterList()
    {
        PrompterList.Children.Clear();
        for (int i = 0; i < prompterScripts.Count; i++)
        {
            int index = i;
            bool selected = i == prompterIndex;
            Button row = new()
            {
                Style = (Style)FindResource("SurfaceButton"),
                Content = prompterScripts[i].Name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 28,
                Margin = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11.5,
                Background = selected ? (Brush)FindResource("GlassSelected") : Brushes.Transparent
            };
            row.SetValue(Round.RadiusProperty, new CornerRadius(10));
            row.Click += (_, _) => SelectPrompterScript(index);
            PrompterList.Children.Add(row);
        }
    }

    private void PrompterAdd_Click(object sender, RoutedEventArgs e)
    {
        prompterScripts.Add(new PrompterScript("Script " + (prompterScripts.Count + 1), ""));
        SavePrompter();
        SelectPrompterScript(prompterScripts.Count - 1);
        PrompterText.Focus();
    }

    private void PrompterText_Changed(object sender, TextChangedEventArgs e)
    {
        if (prompterLoading || prompterIndex >= prompterScripts.Count)
        {
            return;
        }
        string text = PrompterText.Text;
        string firstLine = text.Split('\n', 2)[0].Trim();
        string name = firstLine.Length == 0 ? "Untitled" : (firstLine.Length > 22 ? firstLine[..22] + "…" : firstLine);
        prompterScripts[prompterIndex] = new PrompterScript(prompterScripts[prompterIndex].Name.StartsWith("Script ") && text.Length == 0 ? prompterScripts[prompterIndex].Name : name, text);
        prompterSaveTimer.Stop();
        prompterSaveTimer.Start();
    }

    private void PrompterSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string speed })
        {
            prompterSpeed = speed;
            UpdateSelection(SpeedButtons(), speed);
        }
    }

    private void PrompterStart_Click(object sender, RoutedEventArgs e)
    {
        if (prompterTimer.IsEnabled)
        {
            PrompterStop();
            return;
        }
        PrompterText.IsReadOnly = true;
        PrompterStartButton.Content = "Stop";
        prompterLast = DateTime.UtcNow;
        prompterTimer.Start();
    }

    private void PrompterStop()
    {
        prompterTimer.Stop();
        PrompterText.IsReadOnly = false;
        PrompterStartButton.Content = "Start";
    }

    private void PrompterTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        double seconds = (now - prompterLast).TotalSeconds;
        prompterLast = now;
        double next = PrompterScroll.VerticalOffset + PrompterPixelsPerSecond(prompterSpeed) * seconds;
        PrompterScroll.ScrollToVerticalOffset(next);
        if (next >= PrompterScroll.ScrollableHeight)
        {
            PrompterStop();
        }
    }

    // ---- hide from screen sharing -----------------------------------------------------------------

    private void ApplyCaptureHiding(bool hide)
    {
        prompterHidden = hide;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0 && Environment.GetEnvironmentVariable("SAPPHIRE_NOEXCLUDE") == null)
        {
            SetWindowDisplayAffinity(hwnd, hide ? WdaExcludeFromCapture : WdaNone);
        }
        PrompterBannerText.Text = hide
            ? "Hidden from Zoom, Meet, Teams, OBS and screenshots. Only you can see this."
            : "Your audience can see this window. Hide it from Zoom, Meet, Teams and OBS.";
        PrompterHideButton.Content = hide ? "Show" : "Hide";
        PrompterBanner.Background = new SolidColorBrush(hide ? Color.FromArgb(0x26, 0x22, 0xC5, 0x5E) : Color.FromArgb(0x26, 0xF5, 0xA5, 0x24));
        PrompterBanner.BorderBrush = new SolidColorBrush(hide ? Color.FromArgb(0x55, 0x22, 0xC5, 0x5E) : Color.FromArgb(0x55, 0xF5, 0xA5, 0x24));
    }

    private void PrompterHide_Click(object sender, RoutedEventArgs e) => ApplyCaptureHiding(!prompterHidden);

    // ---- screenshot ---------------------------------------------------------------------------------

    // Opens the built-in Windows snipping overlay; the picture goes to the clipboard and a notification.
    private void Shot_Click(object sender, RoutedEventArgs e) => OpenShell("ms-screenclip:");
}

using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SapphireNotch.Windows;

/// <summary>Persisted user preferences (JSON in %LocalAppData%\Sapphire NOTCh\settings.json).</summary>
public sealed class AppSettings
{
    public bool OpenOnHover { get; set; } = true;
    public int AutoCloseSeconds { get; set; } = 5;
    public bool StartWithWindows { get; set; }
    public bool ShowMusic { get; set; } = true;
    public bool ShowDev { get; set; } = true;
    public bool ShowShare { get; set; } = true;
    public bool ShowTools { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool ShowChat { get; set; } = true;
    public string AiModel { get; set; } = "nvidia/nemotron-3-ultra-550b-a55b:free";
    public string AiPersona { get; set; } = "Auto";
    public string AiProvider { get; set; } = "OpenRouter"; // or "OmniRoute"
    public string AiBaseUrl { get; set; } = "http://localhost:20128/v1"; // OmniRoute's local address
    public string AiOmniModel { get; set; } = "";
    public bool TamerEnabled { get; set; }
    public List<TamerRule> TamerRules { get; set; } = new()
    {
        new TamerRule { App = "chrome", Efficiency = true },
        new TamerRule { App = "msedge", Efficiency = true },
        new TamerRule { App = "WhatsApp", Efficiency = true, SlowOn = true, SlowLimit = 1 },
        new TamerRule { App = "Codex", Efficiency = false, Boost = true },
        new TamerRule { App = "ChatGPT", Efficiency = false, Boost = true }
    };
    public TamerOptions Tamer { get; set; } = new();
    public bool LookNoticeSeen { get; set; }
    public bool GameMode { get; set; } = true;
    public bool FpsOverlay { get; set; }
    public bool CheckInEnabled { get; set; }
    public int CheckInMinutes { get; set; } = 15;
    public bool MusicSpotify { get; set; } = true;
    public bool VoiceNotesEnabled { get; set; } = true;
    public string VoiceTrigger { get; set; } = "CtrlRightAlt";
    public bool DictationEnabled { get; set; }
    public string DictationStyle { get; set; } = "Raw";
    public bool DictationUseDictionary { get; set; } = true;
    public bool DictationExpandSnippets { get; set; } = true;
    public bool StartupAnimation { get; set; } = true;
    public bool TourSeen { get; set; }
}

internal static class SettingsStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Sapphire NOTCh";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh", "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Raised after a save so the island can re-apply tabs, timers and hover behaviour.</summary>
    public static event Action? Changed;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        ApplyStartWithWindows();
        Changed?.Invoke();
    }

    private static void ApplyStartWithWindows()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
            {
                return;
            }
            if (Current.StartWithWindows)
            {
                string? exe = Environment.ProcessPath;
                if (exe != null)
                {
                    key.SetValue(RunValue, $"\"{exe}\"");
                }
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}






using System.Windows;
using System.Windows.Controls;

namespace SapphireNotch.Windows;

// Wires the island to the persisted preferences and to the Settings window.
public partial class MainWindow
{
    private SettingsWindow? settingsWindow;
    private bool settingsReady;

    private void InitSettings()
    {
        if (settingsReady)
        {
            return;
        }
        settingsReady = true;
        SettingsStore.Changed += ApplySettings;
        InitVoice();
        InitChat();
        InitTamer();
        Dispatcher.BeginInvoke(new Action(WarmUpLayout), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Closed += (_, _) => SettingsStore.Changed -= ApplySettings;
        ApplySettings();
        if (Environment.GetCommandLineArgs().Contains("--hub"))
        {
            Dispatcher.BeginInvoke(new Action(() => HubWindow.ShowSingle()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void ApplySettings()
    {
        AppSettings s = SettingsStore.Current;
        collapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, s.AutoCloseSeconds));
        if (voice != null)
        {
            voice.Trigger = s.VoiceTrigger;
            voice.Enabled = s.VoiceNotesEnabled;
        }

        ChatTab.Visibility = s.ShowChat ? Visibility.Visible : Visibility.Collapsed;
        ApplyCheckIn();
        MusicTab.Visibility = s.ShowMusic ? Visibility.Visible : Visibility.Collapsed;
        DevTab.Visibility = s.ShowDev ? Visibility.Visible : Visibility.Collapsed;
        ShareTab.Visibility = s.ShowShare ? Visibility.Visible : Visibility.Collapsed;
        ToolsTab.Visibility = s.ShowTools ? Visibility.Visible : Visibility.Collapsed;
        SystemTab.Visibility = s.ShowSystem ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens the island on a given view (used by the Settings window).</summary>
    public void ShowTab(string tab)
    {
        Expand();
        SelectTab(tab, animate: false);
    }

    // ---- Voice notes: hold the trigger, speak, release ----------------------------------------

    private VoiceTrigger? voice;
    private bool voiceTyping;

    private void InitVoice()
    {
        voice = new VoiceTrigger { Trigger = SettingsStore.Current.VoiceTrigger, Enabled = SettingsStore.Current.VoiceNotesEnabled };
        voice.Started += StartVoiceNote;
        voice.Stopped += StopVoiceNote;
        Closed += (_, _) => voice.Dispose();
        whisper.Warm();
    }

    private void StartVoiceNote()
    {
        if (dictating)
        {
            return;
        }
        if (voiceTyping)
        {
            return;
        }
        if (!WhisperDictation.ModelInstalled)
        {
            SelectTab("Chat");
            AddBubble("Team", "The Whisper speech model is missing (models\\ggml-base.en.bin in the Sapphire NOTCh data folder).", false);
            return;
        }
        try
        {
            // a harmless key press while Alt is down, so releasing Alt does not open the target app's menu bar and swallow the paste
            keybd_event(0xE8, 0, 0u, UIntPtr.Zero);
            keybd_event(0xE8, 0, 2u, UIntPtr.Zero);
            whisper.Start();
            voiceTyping = true;
            ShowListening(true);
        }
        catch (Exception ex)
        {
            SpeechDictation.Log("whisper start failed: " + ex.Message);
            SelectTab("Chat");
            AddBubble("Team", "The microphone could not start: " + ex.Message, false);
        }
    }

    private async void StopVoiceNote()
    {
        if (!voiceTyping)
        {
            return;
        }
        voiceTyping = false;
        ShowListening(false);
        DateTime started = DateTime.Now;
        try
        {
            string text = await whisper.StopAsync();
            SpeechDictation.Log("whisper heard " + text.Length + " chars in " + (DateTime.Now - started).TotalSeconds.ToString("0.0") + "s");
            if (text.Length > 0)
            {
                DictationStore.AddEntry(text, null);
                TypeIntoFocusedApp(text);
            }
        }
        catch (Exception ex)
        {
            SpeechDictation.Log("whisper failed: " + ex);
            SelectTab("Chat");
            AddBubble("Team", "Transcription failed: " + ex.Message, false);
        }
    }

    private readonly WhisperDictation whisper = new();

    // Paste the text into whichever app has focus (the notch never takes focus), then put the old clipboard text back.
    private void TypeIntoFocusedApp(string text)
    {
        string? previous = null;
        try
        {
            previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            Clipboard.SetText(text + " ");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }
        ReleaseTriggerKeys();
        keybd_event(0x11, 0, 0u, UIntPtr.Zero);
        keybd_event(0x56, 0, 0u, UIntPtr.Zero);
        keybd_event(0x56, 0, 2u, UIntPtr.Zero);
        keybd_event(0x11, 0, 2u, UIntPtr.Zero);
        System.Windows.Threading.DispatcherTimer restore = new() { Interval = TimeSpan.FromMilliseconds(400) };
        restore.Tick += delegate
        {
            restore.Stop();
            try
            {
                if (previous != null)
                {
                    Clipboard.SetText(previous);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        };
        restore.Start();
    }

    // The trigger keys are still physically held; lift them virtually so Win + H arrives clean.
    private static void ReleaseTriggerKeys()
    {
        foreach (byte key in new byte[] { 0x11, 0x12, 0x5B, 0x5C })
        {
            keybd_event(key, 0, 2u, UIntPtr.Zero);
        }
    }

    private static void PressWinH()
    {
        keybd_event(91, 0, 0u, UIntPtr.Zero);
        keybd_event(72, 0, 0u, UIntPtr.Zero);
        keybd_event(72, 0, 2u, UIntPtr.Zero);
        keybd_event(91, 0, 2u, UIntPtr.Zero);
    }

    private ContextMenu? openMenu;

    // The gear: a small glass menu to launch the Sapphire app, settings and voice notes.
    private void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ContextMenu menu = (ContextMenu)FindResource("AppMenu");
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Closed += (_, _) => openMenu = null;
        openMenu = menu;
        menu.IsOpen = true;
    }

    private void VoiceNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StartVoiceNote(); // opens a new note and starts Windows voice typing; press Win + H to stop
    }

    private void OpenHub_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HubWindow.ShowSingle();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (settingsWindow is { IsLoaded: true })
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow();
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        Collapse();
    }
}








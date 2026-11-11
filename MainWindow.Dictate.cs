using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SapphireNotch.Windows;

// Voice dictation: the mic button, Ctrl + Alt + Space and the hold-to-talk voice note all use this.
public partial class MainWindow
{
    private SpeechDictation? speech;
    private bool dictating;
    private Action<string>? dictationSink;     // where finished phrases go
    private Action<string>? dictationPreview;  // where the live words go (may be null)
    private Action? dictationRestore;          // resets the live preview
    private readonly System.Text.StringBuilder dictated = new();
    private DateTime dictationStart;

    private SpeechDictation Speech
    {
        get
        {
            if (speech == null)
            {
                speech = new SpeechDictation(Dispatcher);
                speech.Phrase += OnDictationPhrase;
                speech.Partial += text => dictationPreview?.Invoke(text);
                speech.Stopped += OnDictationStopped;
            }
            return speech;
        }
    }

    /// <summary>Mic button and hotkey: start listening into the chat box or the open note, or stop.</summary>
    private void ToggleDictation()
    {
        SpeechDictation.Log("toggle (dictating=" + dictating + ")");
        if (dictating)
        {
            _ = EndWhisperIntoBoxAsync();
            return;
        }

        if (NotesContent.Visibility == Visibility.Visible && ToolsPanel.Visibility == Visibility.Visible)
        {
            TextBox notes = NotesBox;
            BeginDictation(text => InsertAtCaret(notes, text), live => NotesHint.Text = live.Length == 0 ? "Quick notes stay on this PC…" : "Hearing: " + live, () => NotesHint.Text = "Quick notes stay on this PC…");
            notes.Focus();
        }
        else
        {
            SelectTab("Chat");
            TextBox input = ChatInput;
            BeginDictation(text => InsertAtCaret(input, text), live => ChatHint.Text = live.Length == 0 ? "Listening…" : "Hearing: " + live, () => ChatHint.Text = "Ask anything, or add an image…");
            input.Focus();
        }
    }

    private static void InsertAtCaret(TextBox box, string text)
    {
        string space = box.Text.Length > 0 && !char.IsWhiteSpace(box.Text[^1]) ? " " : "";
        box.SelectedText = space + text + " ";
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
        box.SelectionLength = 0;
    }

    private async void BeginDictation(Action<string> sink, Action<string>? preview, Action? restore)
    {
        dictationSink = sink;
        dictationPreview = preview;
        dictationRestore = restore;
        dictated.Clear();
        dictationStart = DateTime.Now;
        dictating = true;
        ShowListening(true);
        preview?.Invoke("");
        try
        {
            if (!WhisperDictation.ModelInstalled && !VoiceStudioClient.Available && !(Task.Run(VoiceStudioClient.ProbeAsync).Wait(1200) && VoiceStudioClient.Available))
            {
                // first use on a new PC: fetch the speech model once, then dictation works
                dictating = false;
                ShowListening(false);
                dictationRestore?.Invoke();
                SelectTab("Chat");
                AddBubble("Team", "Setting up voice typing: downloading the speech model once (141 MB). I will tell you when it is ready.", false);
                string? failure = await MeetingCopilot.DownloadModelAsync();
                AddBubble("Team", failure == null ? "Voice typing is ready. Press the microphone again." : "The speech model download failed: " + failure, false);
                return;
            }
            whisper.Start();
        }
        catch (Exception ex)
        {
            SpeechDictation.Log("whisper start failed: " + ex.Message);
            dictating = false;
            ShowListening(false);
            dictationRestore?.Invoke();
            SelectTab("Chat");
            AddBubble("Team", "The microphone could not start: " + ex.Message, false);
        }
    }

    /// <summary>Second click on the mic: stop recording, transcribe on this PC, drop the words into the box.</summary>
    private async Task EndWhisperIntoBoxAsync()
    {
        dictating = false;
        ShowListening(false);
        dictationPreview?.Invoke("Transcribing…");
        DateTime stopped = DateTime.Now;
        try
        {
            string text = await whisper.StopAsync();
            SpeechDictation.Log("whisper heard " + text.Length + " chars in " + (DateTime.Now - stopped).TotalSeconds.ToString("0.0") + "s");
            if (text.Length > 0)
            {
                dictationSink?.Invoke(text);
                DictationStore.AddEntry(text, (DateTime.Now - dictationStart).TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            SpeechDictation.Log("whisper failed: " + ex);
            SelectTab("Chat");
            AddBubble("Team", "Transcription failed: " + ex.Message, false);
        }
        dictationRestore?.Invoke();
        dictationSink = null;
        dictationPreview = null;
        dictationRestore = null;
    }

    private void OnDictationPhrase(string text)
    {
        dictated.Append(dictated.Length > 0 ? " " : "").Append(text);
        dictationSink?.Invoke(text);
        dictationPreview?.Invoke("");
    }

    private void OnDictationStopped(string? message, bool needsPrivacySetting)
    {
        bool wasDictating = dictating;
        dictating = false;
        ShowListening(false);
        dictationRestore?.Invoke();

        string heard = dictated.ToString().Trim();
        if (wasDictating && heard.Length > 0)
        {
            DictationStore.AddEntry(heard, (DateTime.Now - dictationStart).TotalSeconds);
        }
        dictationSink = null;
        dictationPreview = null;
        dictationRestore = null;

        if (message != null)
        {
            SelectTab("Chat");
            AddBubble("Team", message, false);
            if (needsPrivacySetting)
            {
                AddBubble("Team", "Opening the page: Privacy & security > Speech > turn on Online speech recognition.", false);
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private void ShowListening(bool on)
    {
        VoiceIcon.Text = on ? "\uE0E2" : "\uE326"; // stop square / microphone
        VoiceIcon.Foreground = on ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F)) : (Brush)FindResource("Ink");
        VoiceButton.ToolTip = on ? "Listening. Click to stop." : "Dictate: click, speak, click again (or Ctrl + Alt + Space)";
    }
}

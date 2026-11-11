using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SapphireNotch.Windows;

// Meeting copilot (Prompter tab): live transcript of the call, and AI suggestions when you ask (or on questions, if Auto is on).
// Audio is transcribed on this PC. Only transcript text, and a screenshot if you press "With my screen", go to the AI model you picked.
public partial class MainWindow
{
    private const string CopilotSystem =
        "You are a discreet meeting copilot for the person called \"You\". You get the live transcript of a call (\"Them\" is the other side). " +
        "Give what You could say or do next: lead with the answer in one or two plain sentences they can say out loud, then at most three short bullet points of support. " +
        "Be accurate and never invent facts about You; if something is unknown, say what to ask. No preamble.";

    private bool copilotMode;
    private MeetingCopilot? copilot;
    private readonly StringBuilder copilotLog = new();
    private readonly List<string> copilotLines = new();
    private DateTime copilotLastAuto = DateTime.MinValue;
    private bool copilotBusy;
    private double copilotLastSeconds;

    private void CopilotOpen_Click(object sender, RoutedEventArgs e) => SetCopilotMode(true);

    private void CopilotBack_Click(object sender, RoutedEventArgs e) => SetCopilotMode(false);

    private void SetCopilotMode(bool on)
    {
        copilotMode = on;
        PrompterPanel.Visibility = !on && currentTab == "Prompter" ? Visibility.Visible : Visibility.Collapsed;
        CopilotPanel.Visibility = on && currentTab == "Prompter" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopilotListen_Click(object sender, RoutedEventArgs e)
    {
        if (copilot is { Running: true })
        {
            StopCopilot();
            return;
        }
        if (!MeetingCopilot.ModelInstalled)
        {
            _ = DownloadSpeechModel();
            return;
        }
        copilot ??= CreateCopilot();
        copilot.Start(CopilotMicSwitch.IsChecked == true);
        if (copilot.Running)
        {
            CopilotListenButton.Content = "Stop";
            CopilotStatus.Text = "Listening to this PC's sound and your mic";
            SetCopilotAnswer("Listening. The transcript appears as people speak.");
        }
    }

    private MeetingCopilot CreateCopilot()
    {
        MeetingCopilot c = new();
        c.Heard += (who, text) => Dispatcher.BeginInvoke(new Action(() => CopilotHeard(who, text)));
        c.Failed += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            CopilotStatus.Text = message;
            if (copilot is { Running: false })
            {
                CopilotListenButton.Content = "Listen";
            }
        }));
        return c;
    }

    private void StopCopilot()
    {
        copilot?.Stop();
        CopilotListenButton.Content = "Listen";
        CopilotStatus.Text = "Stopped";
    }

    private void CopilotHeard(string who, string text)
    {
        string line = who + ": " + text;
        copilotLines.Add(line);
        copilotLog.AppendLine(line);
        CopilotTranscript.Text = string.Join("\n", copilotLines.TakeLast(40));
        CopilotTranscriptScroll.ScrollToEnd();

        // Auto: answer a question from the other side (at most one every 20 s, and never twice at once)
        if (CopilotAutoSwitch.IsChecked == true && who == "Them" && text.TrimEnd().EndsWith('?') && !copilotBusy
            && DateTime.Now - copilotLastAuto > TimeSpan.FromSeconds(20))
        {
            copilotLastAuto = DateTime.Now;
            _ = AskCopilot("They just asked a question. What should You say?", withScreen: false);
        }
    }

    private async Task DownloadSpeechModel()
    {
        CopilotListenButton.IsEnabled = false;
        CopilotStatus.Text = "Downloading the speech model (141 MB, once)…";
        string? error = await MeetingCopilot.DownloadModelAsync();
        CopilotStatus.Text = error == null ? "Speech model ready. Press Listen." : "Download failed: " + error;
        CopilotListenButton.IsEnabled = true;
    }

    // ---- asking the AI --------------------------------------------------------------------------

    private void SetCopilotAnswer(string text)
    {
        CopilotAnswer.Text = text;
        CopilotAnswerScroll.ScrollToTop();
    }

    private string Transcript(int maxChars = 4000)
    {
        string all = copilotLog.ToString();
        return all.Length > maxChars ? all[^maxChars..] : all;
    }

    private async Task AskCopilot(string ask, bool withScreen)
    {
        if (copilotBusy)
        {
            return;
        }
        if (copilotLog.Length == 0 && !withScreen)
        {
            SetCopilotAnswer("Nothing heard yet. Press Listen and let the call run for a moment.");
            return;
        }
        copilotBusy = true;
        string? image = null;
        // Fast model for live help: the big reasoning model thinks for many seconds before its first word.
        string model = ActiveModel;
        if (withScreen)
        {
            TrackForeground();
            BitmapSource? shot = LookNative.Capture(lastOtherWindow, false);
            if (shot != null)
            {
                image = ToDataUrl(shot, 1400);
                model = AiService.VisionFreeModel;
            }
        }
        SetCopilotAnswer(withScreen ? "Looking at your screen and the call…" : "Thinking…");
        try
        {
            string prompt = (copilotLog.Length > 0 ? "Transcript so far:\n" + Transcript(2000) + "\n\n" : "") + ask;
            DateTime began = DateTime.Now;
            string answer = await AiService.ChatStreamAsync(model, CopilotSystem, new[] { new ChatTurn("user", prompt, image) },
                text => Dispatcher.BeginInvoke(new Action(() => CopilotAnswer.Text = text)));
            if (answer.StartsWith("Rate limited", StringComparison.Ordinal) && model.EndsWith(":free", StringComparison.Ordinal))
            {
                // free models take turns being busy: try the other one straight away
                string other = model == AiService.AutoFreeModel ? AiService.VisionFreeModel : AiService.AutoFreeModel;
                answer = await AiService.ChatStreamAsync(other, CopilotSystem, new[] { new ChatTurn("user", prompt, image) },
                    text => Dispatcher.BeginInvoke(new Action(() => CopilotAnswer.Text = text)));
            }
            CopilotAnswer.Text = answer;
            copilotLastSeconds = (DateTime.Now - began).TotalSeconds;
        }
        finally
        {
            copilotBusy = false;
        }
    }

    private static string ToDataUrl(BitmapSource source, int maxSide)
    {
        double scale = Math.Min(1.0, maxSide / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sized = scale < 1.0 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        JpegBitmapEncoder encoder = new() { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(sized));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private void CopilotSuggest_Click(object sender, RoutedEventArgs e) =>
        _ = AskCopilot("What should You say or do next? Focus on the most recent thing said.", false);

    private void CopilotScreen_Click(object sender, RoutedEventArgs e) =>
        _ = AskCopilot("The picture is Your screen right now. Using it and the call, what should You say or do next?", true);

    private void CopilotSummary_Click(object sender, RoutedEventArgs e) =>
        _ = AskCopilot("Summarize the call so far: key points, decisions, and action items with owners.", false);

    private void CopilotNotes_Click(object sender, RoutedEventArgs e)
    {
        if (copilotLog.Length == 0)
        {
            SetCopilotAnswer("Nothing to save yet.");
            return;
        }
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sapphire NOTCh", "Meetings");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "Meeting " + DateTime.Now.ToString("yyyy-MM-dd HH-mm") + ".md");
            File.WriteAllText(file, "# Meeting " + DateTime.Now.ToString("f") + "\n\n## Transcript\n\n" + copilotLog + "\n## Last suggestion\n\n" + CopilotAnswer.Text + "\n");
            SetCopilotAnswer("Saved to " + file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetCopilotAnswer("Could not save: " + ex.Message);
        }
    }
}

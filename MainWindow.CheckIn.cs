using System.IO;
using System.Windows;
using System.Windows.Input;

namespace SapphireNotch.Windows;

/// <summary>A short two-note chime, made in code, so alerts have a sound without shipping an audio file.</summary>
internal static class Chime
{
    private static readonly byte[] Wav = Build();

    public static void Play()
    {
        try
        {
            using MemoryStream stream = new(Wav);
            using System.Media.SoundPlayer player = new(stream);
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Build()
    {
        const int rate = 22050;
        int count = (int)(rate * 0.75);
        short[] samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)rate;
            double note = t < 0.16 ? 880 : 1320;
            double start = t < 0.16 ? 0 : 0.16;
            double envelope = Math.Exp(-(t - start) * 7) * Math.Min(1, (t - start) * 400);
            samples[i] = (short)(Math.Sin(2 * Math.PI * note * t) * envelope * 9000);
        }
        using MemoryStream ms = new();
        using BinaryWriter w = new(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + count * 2); w.Write("WAVEfmt "u8.ToArray());
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(count * 2);
        foreach (short s in samples)
        {
            w.Write(s);
        }
        return ms.ToArray();
    }
}

// The 15-minute check-in as its own bar: the island opens, shows one question, takes your answer and replies in the same place.
public partial class MainWindow
{
    private bool checkInAnswered;

    private void ShowCheckIn()
    {
        if (!silent)
        {
            Chime.Play();
        }
        checkInAnswered = false;
        CheckInTitle.Text = $"{SettingsStore.Current.CheckInMinutes}-MINUTE CHECK-IN";
        CheckInText.Text = "What did you finish, and what is next?";
        CheckInInput.Text = "";
        CheckInSend.Content = "Send";
        CheckInPanel.Visibility = Visibility.Visible;
        Expand();
        CheckInInput.Focus();
    }

    private void CheckInClose_Click(object sender, RoutedEventArgs e) => CheckInPanel.Visibility = Visibility.Collapsed;

    private void CheckInInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CheckInSend_Click(sender, e);
        }
    }

    private async void CheckInSend_Click(object sender, RoutedEventArgs e)
    {
        if (checkInAnswered)
        {
            CheckInPanel.Visibility = Visibility.Collapsed;
            return;
        }
        string text = CheckInInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        checkInAnswered = true;
        CheckInSend.Content = "Done";
        CheckInInput.Text = "";
        if (sessionGoal.Length == 0)
        {
            sessionGoal = text;
        }
        checkInLog += $"- {DateTime.Now:t}: {text}\n";
        SaveCheckInLog();
        CheckInText.Text = "Thinking…";
        string system = AiService.SystemPrompt("Coach") + $" Session goal: {sessionGoal}. Check-ins so far:\n{checkInLog} Reply in at most three short lines: one acknowledgement, then the single next step.";
        string reply = await AiService.ChatStreamAsync(ActiveModel, system, new[] { new ChatTurn("user", text) },
            t => Dispatcher.BeginInvoke(new Action(() => CheckInText.Text = t)), 200);
        CheckInText.Text = reply;
    }

    // Your check-ins are kept as a plain list for the day, so the 15-minute answers add up to a record.
    private void SaveCheckInLog()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sapphire NOTCh", "Check-ins");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".md"), "# Check-ins " + DateTime.Now.ToString("D") + "\n\nGoal: " + sessionGoal + "\n\n" + checkInLog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

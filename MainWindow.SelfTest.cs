using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SapphireNotch.Windows;

// Developer aid: SAPPHIRE_SELFTEST=1 drives the main controls in code, writes selftest.log and quits.
public partial class MainWindow
{
    private async void RunSelfTest()
    {
        List<string> lines = new();
        void Check(string name, bool ok, string detail = "") => lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        try
        {
            await Task.Delay(3000);
            Expand();
            await Task.Delay(1500);

            (string Tab, UIElement Panel)[] tabs =
            {
                ("Home", HomePanel), ("Chat", ChatPanel), ("Music", MusicPanel), ("Dev", DevPanel), ("Share", SharePanel),
                ("Tools", ToolsPanel), ("Prompter", PrompterPanel), ("Tamer", TamerPanel), ("System", SystemPanel)
            };
            foreach ((string tab, UIElement panel) in tabs)
            {
                SelectTab(tab, false);
                await Task.Delay(300);
                bool shown = panel.Visibility == Visibility.Visible;
                bool labelOk = TabButtons[tab].Visibility == Visibility.Visible;
                Check($"tab {tab} opens its panel", shown && labelOk);
            }

            Check("battery shows a value", BatteryText.Text.Length > 0 && BatteryText.Text != "--", BatteryText.Text);

            bool awakeBefore = keepAwake;
            Click(AwakeButton);
            bool awakeOn = keepAwake;
            Click(AwakeButton);
            Check("keep awake toggles on and off", !awakeBefore && awakeOn && !keepAwake);

            bool pinBefore = pinnedOpen;
            Click(PinButton);
            bool pinOn = pinnedOpen;
            Click(PinButton);
            Check("keep open toggles", pinBefore != pinOn && pinnedOpen == pinBefore);

            string glyphBefore = MuteButtonGlyph.Text;
            Click(MuteButton);
            string glyphSilent = MuteButtonGlyph.Text;
            Click(MuteButton);
            Check("silent toggles icon and restores", glyphBefore != glyphSilent && MuteButtonGlyph.Text == glyphBefore);

            SelectTab("Prompter", false);
            await Task.Delay(300);
            Check("prompter has scripts", prompterScripts.Count > 0 && PrompterText.Text.Length > 0, prompterScripts.Count + " scripts");
            int before = prompterScripts.Count;
            PrompterAdd_Click(this, new RoutedEventArgs());
            Check("prompter adds a script", prompterScripts.Count == before + 1);
            PrompterText.Text = "Line one of a long test script.\n" + string.Concat(Enumerable.Repeat("This is more text to make the script long enough to scroll on screen.\n\n", 12));
            await Task.Delay(300);
            Click(PrompterStartButton);
            await Task.Delay(1500);
            bool started = prompterTimer.IsEnabled && (string)PrompterStartButton.Content == "Stop";
            double offset = PrompterScroll.VerticalOffset;
            Click(PrompterStartButton);
            Check("prompter starts, scrolls, stops", started && offset > 0 && !prompterTimer.IsEnabled, $"scrolled {offset:0}px");
            Click(PrompterFast);
            Check("prompter speed selection", prompterSpeed == "Fast");
            string bannerBefore = PrompterBannerText.Text;
            Click(PrompterHideButton);
            string bannerAfter = PrompterBannerText.Text;
            Click(PrompterHideButton);
            Check("hide-from-screen-share toggles", bannerBefore != bannerAfter && PrompterBannerText.Text == bannerBefore);
            prompterScripts.RemoveAt(prompterScripts.Count - 1);
            SavePrompter();

            SelectTab("Music", false);
            await Task.Delay(1500);
            Check("music panel has play button glyph", !string.IsNullOrEmpty(MusicPlayGlyph.Text), nowHasTrack ? $"track: {nowTitle}" : "nothing playing");
            Check("lyrics loaded when a track plays", !nowHasTrack || lyrics.Count > 0, lyrics.Count + " lines");

            string fake = System.IO.Path.Combine(AgentsFolder, "selftest-fake.json");
            System.IO.File.WriteAllText(fake, "{\"session\":\"selftest-fake\",\"cwd\":\"C:\\\\\",\"project\":\"demo\",\"branch\":\"main\",\"status\":\"needs\",\"message\":\"Approve file edit\",\"time\":\"" + DateTime.Now.ToString("o") + "\",\"agent\":\"Claude Code\"}");
            RefreshAgents();
            bool listed = ReadAgents().Any(a => a.Session == "selftest-fake" && a.Status == "needs") && AgentList.Children.Count >= 2;
            System.IO.File.Delete(fake);
            RefreshAgents();
            Check("agents list shows a needs-you session", listed);
            ShowCheckIn();
            await Task.Delay(300);
            Check("check-in bar opens", CheckInPanel.Visibility == Visibility.Visible);
            CheckInInput.Text = "Finished the installer, next is testing it on a clean PC.";
            CheckInSend_Click(this, new RoutedEventArgs());
            await Task.Delay(15000);
            Check("check-in bar answers", CheckInText.Text.Length > 15 && CheckInText.Text != "Thinking…" && !CheckInText.Text.StartsWith("Rate limited"), CheckInText.Text.Replace("\n", " "));
            CheckInClose_Click(this, new RoutedEventArgs());
            SelectTab("Prompter", false);
            SetCopilotMode(true);
            await Task.Delay(300);
            Check("copilot panel opens", CopilotPanel.Visibility == Visibility.Visible && PrompterPanel.Visibility != Visibility.Visible);
            Check("auto-answer is off by default", CopilotAutoSwitch.IsChecked != true, CopilotAutoSwitch.IsChecked?.ToString() ?? "null");
            string? wav = Environment.GetEnvironmentVariable("SAPPHIRE_TEST_WAV");
            if (wav != null && System.IO.File.Exists(wav))
            {
                byte[] raw = System.IO.File.ReadAllBytes(wav);
                float[] clip = new float[(raw.Length - 44) / 2];
                for (int i = 0; i < clip.Length; i++)
                {
                    clip[i] = BitConverter.ToInt16(raw, 44 + i * 2) / 32768f;
                }
                using MeetingCopilot engine = new();
                string heard = await engine.TranscribeClip(clip);
                lines.Add("      heard: " + heard);
                Check("speech clip is transcribed", heard.Contains("strength", StringComparison.OrdinalIgnoreCase), heard);
                // real capture path: play the clip through the speakers and let the loopback listener hear it
                string liveHeard = "";
                using (MeetingCopilot live = new())
                {
                    live.Heard += (who, text) => liveHeard += who + ": " + text + " ";
                    live.Start(false);
                    using (System.Media.SoundPlayer player = new(wav))
                    {
                        player.PlaySync();
                    }
                    await Task.Delay(9000);
                    live.Stop();
                }
                lines.Add("      live capture heard: " + liveHeard);
                Check("live listener hears sound playing on this PC", liveHeard.Contains("strength", StringComparison.OrdinalIgnoreCase), liveHeard.Length + " chars");
                CopilotHeard("Them", heard);
                await AskCopilot("What should You say next?", false);
                string answer = CopilotAnswer.Text;
                lines.Add("      answer: " + answer.Replace("\n", " "));
                foreach (string m in new[] { AiService.AutoFreeModel, AiService.VisionFreeModel })
                {
                    DateTime t0 = DateTime.Now;
                    double first = -1;
                    string r = await AiService.ChatStreamAsync(m, CopilotSystem, new[] { new ChatTurn("user", "Transcript so far:\n" + heard + "\n\nWhat should You say next?") },
                        _ => { if (first < 0) { first = (DateTime.Now - t0).TotalSeconds; } });
                    lines.Add($"      speed {m}: first words {first:0.0}s, done {(DateTime.Now - t0).TotalSeconds:0.0}s, {r.Length} chars");
                }
                lines.Add($"      copilot answer took {copilotLastSeconds:0.0}s");
                Check("AI suggests an answer from the transcript", answer.Length > 20 && answer != "Thinking…" && !answer.StartsWith("Add your") && !answer.StartsWith("Could not"), answer.Length + " chars");
            }
            if (wav != null && System.IO.File.Exists(wav))
            {
                bool up = await VoiceStudioClient.ProbeAsync();
                using System.IO.FileStream clipStream = System.IO.File.OpenRead(wav);
                string? viaVs = up ? await VoiceStudioClient.TranscribeAsync(clipStream) : null;
                lines.Add("      voicestudio: " + (up ? "reachable, said: " + viaVs : "not running"));
            }
            SetCopilotMode(false);
            Check("tour has 10 steps", TourSteps().Count == 10, TourSteps().Count + " steps");
            int missing = 0;
            foreach (TourStep step in TourSteps())
            {
                if (step.Target == null)
                {
                    continue;
                }
                SelectTab(step.Tab, false);
                await Task.Delay(250);
                if (step.Target() == null)
                {
                    missing++;
                    lines.Add("      missing tour target on step: " + step.Title);
                }
            }
            Check("tour targets all found", missing == 0, missing + " missing");
        }
        catch (Exception ex)
        {
            lines.Add("ERROR  " + ex);
        }
        File.WriteAllLines(Path.Combine(DataFolder, "selftest.log"), lines);
        Application.Current.Shutdown();
    }
}

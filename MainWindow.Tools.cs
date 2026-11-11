using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

// Music, timer presets, calculator, clipboard history and notes.
public partial class MainWindow
{
    private sealed record ClipItem(string Text, DateTime At);

    private const int ClipboardLimit = 50;

    private readonly List<ClipItem> clipboardItems = new();
    private bool ignoreNextClipboard;
    private bool notesLoading;
    private readonly DispatcherTimer notesSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private static string DataFolder
    {
        get
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private void InitTools()
    {
        CalendarDate.Text = DateTime.Now.ToString("MMMM d", CultureInfo.CurrentCulture);
        SetMusicService(SettingsStore.Current.MusicSpotify, remember: false);
        UpdateTimerDisplay();
        notesLoading = true;
        try
        {
            string notes = Path.Combine(DataFolder, "notes.txt");
            if (File.Exists(notes))
            {
                NotesBox.Text = File.ReadAllText(notes);
            }
        }
        catch (IOException)
        {
        }
        notesLoading = false;
        NotesHint.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        notesSaveTimer.Tick += (_, _) =>
        {
            notesSaveTimer.Stop();
            try
            {
                File.WriteAllText(Path.Combine(DataFolder, "notes.txt"), NotesBox.Text);
            }
            catch (IOException)
            {
            }
        };
        RefreshClipboardList();
    }

    // ---- Music ---------------------------------------------------------------------------

    private bool musicSpotify = true;

    private bool SpotifySelected => musicSpotify;

    private void SetMusicService(bool spotify, bool remember = true)
    {
        musicSpotify = spotify;
        string name = spotify ? "Spotify" : "YouTube Music";
        Color brand = spotify ? Color.FromRgb(0x1D, 0xB9, 0x54) : Color.FromRgb(0xFF, 0x4D, 0x4F);
        MusicTitle.Text = spotify ? "Play something in Spotify" : "Play something in YouTube";
        MusicOpenButton.Content = $"Open {name}";
        MusicDesc.Text = $"Sapphire controls playback with your media keys, so it works with {name} in your browser or desktop app.";
        MusicTile.Background = new SolidColorBrush(brand);
        MusicOpenButton.Background = new SolidColorBrush(brand);
        MusicOpenButton.Foreground = spotify ? new SolidColorBrush(Color.FromRgb(0x04, 0x13, 0x0A)) : Brushes.White;
        UpdateSelection(new Dictionary<string, Button>
        {
            ["Spotify"] = MusicSpotifyButton,
            ["YouTube"] = MusicYouTubeButton
        }, spotify ? "Spotify" : "YouTube");
        if (nowHasTrack)
        {
            MusicTitle.Text = nowTitle;
            MusicDesc.Text = nowArtist;
            if (coverBrush != null)
            {
                MusicTile.Background = coverBrush;
            }
        }
        if (remember && SettingsStore.Current.MusicSpotify != spotify)
        {
            SettingsStore.Current.MusicSpotify = spotify;
            SettingsStore.Save();
        }
    }

    private void MusicService_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            SetMusicService(tag == "Spotify");
        }
    }

    // Home: both services side by side; picking one switches the Music tab to it.
    private void HomeMusic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            SetMusicService(tag == "Spotify");
            SelectTab("Music");
        }
    }

    private void MusicSearchToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = MusicSearchRow.Visibility != Visibility.Visible;
        MusicSearchRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MusicDesc.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            MusicSearchInput.Focus();
        }
    }

    private void MusicLibrary_Click(object sender, RoutedEventArgs e)
    {
        OpenMusicWindow(SpotifySelected ? "https://open.spotify.com/collection/playlists" : "https://music.youtube.com/library");
    }

    // ---- Timer ---------------------------------------------------------------------------

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int minutes))
        {
            remainingSeconds = minutes * 60;
            UpdateTimerDisplay();
            countdownTimer.Start();
        }
    }

    // ---- Calculator ----------------------------------------------------------------------

    private string calcEntry = "0";
    private double? calcAccumulator;
    private string? calcOperator;
    private bool calcFresh = true;

    private void Calc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
        {
            return;
        }
        switch (key)
        {
            case "C":
                calcEntry = "0";
                calcAccumulator = null;
                calcOperator = null;
                calcFresh = true;
                break;
            case "back":
                calcEntry = calcEntry.Length > 1 && !calcFresh ? calcEntry[..^1] : "0";
                if (calcEntry == "-")
                {
                    calcEntry = "0";
                }
                break;
            case "neg":
                if (calcEntry != "0")
                {
                    calcEntry = calcEntry.StartsWith('-') ? calcEntry[1..] : "-" + calcEntry;
                }
                break;
            case "pct":
                calcEntry = Format(Parse(calcEntry) / 100.0);
                calcFresh = true;
                break;
            case "+" or "-" or "*" or "/":
                ApplyPending();
                calcOperator = key;
                calcAccumulator = Parse(calcEntry);
                calcFresh = true;
                break;
            case "=":
                ApplyPending();
                calcOperator = null;
                calcAccumulator = null;
                calcFresh = true;
                break;
            case ".":
                if (calcFresh)
                {
                    calcEntry = "0.";
                    calcFresh = false;
                }
                else if (!calcEntry.Contains('.'))
                {
                    calcEntry += ".";
                }
                break;
            default: // digit
                if (calcFresh || calcEntry == "0")
                {
                    calcEntry = key;
                    calcFresh = false;
                }
                else if (calcEntry.Length < 14)
                {
                    calcEntry += key;
                }
                break;
        }
        CalcDisplay.Text = calcEntry;
        CalcStatus.Text = calcOperator != null && calcAccumulator != null
            ? $"{Format(calcAccumulator.Value)} {OperatorSymbol(calcOperator)}"
            : (calcEntry == "Cannot divide by zero" ? "Error" : "Ready");
    }

    private void ApplyPending()
    {
        if (calcOperator == null || calcAccumulator == null || calcFresh)
        {
            return;
        }
        double right = Parse(calcEntry);
        double left = calcAccumulator.Value;
        double result = calcOperator switch
        {
            "+" => left + right,
            "-" => left - right,
            "*" => left * right,
            _ => right == 0 ? double.NaN : left / right
        };
        calcEntry = double.IsNaN(result) ? "Cannot divide by zero" : Format(result);
        calcAccumulator = double.IsNaN(result) ? null : result;
    }

    private static double Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;

    private static string Format(double value) => Math.Round(value, 10).ToString("0.##########", CultureInfo.InvariantCulture);

    private static string OperatorSymbol(string op) => op switch { "*" => "×", "/" => "÷", "-" => "−", _ => op };

    // ---- Clipboard history ---------------------------------------------------------------
    // Kept in memory only (never written to disk): clipboards often hold passwords.

    private void ClipboardChanged()
    {
        if (ignoreNextClipboard)
        {
            ignoreNextClipboard = false;
            return;
        }
        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }
            string text = Clipboard.GetText().Trim();
            if (text.Length == 0 || (clipboardItems.Count > 0 && clipboardItems[0].Text == text))
            {
                return;
            }
            clipboardItems.RemoveAll(i => i.Text == text);
            clipboardItems.Insert(0, new ClipItem(text, DateTime.Now));
            if (clipboardItems.Count > ClipboardLimit)
            {
                clipboardItems.RemoveAt(clipboardItems.Count - 1);
            }
            RefreshClipboardList();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another app holds the clipboard for a moment; the next change will be picked up
        }
    }

    private void ClipboardSearch_Changed(object sender, TextChangedEventArgs e)
    {
        ClipboardSearchHint.Visibility = ClipboardSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshClipboardList();
    }

    private void ClipboardClear_Click(object sender, RoutedEventArgs e)
    {
        clipboardItems.Clear();
        RefreshClipboardList();
    }

    private void RefreshClipboardList()
    {
        if (ClipboardList == null)
        {
            return;
        }
        string filter = ClipboardSearch.Text.Trim();
        ClipboardCount.Text = $"{clipboardItems.Count} of {ClipboardLimit} kept";
        ClipboardList.Children.Clear();
        foreach (ClipItem item in clipboardItems.Where(i => filter.Length == 0 || i.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
        {
            Button card = new()
            {
                Style = (Style)FindResource("GlassCardButton"),
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 8, 12, 8),
                Height = 62,
                Tag = item.Text,
                ToolTip = "Click to copy again"
            };
            card.SetValue(Round.RadiusProperty, new CornerRadius(16));
            StackPanel content = new() { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = "Text  ·  " + item.At.ToString("t", CultureInfo.CurrentCulture),
                FontSize = 11,
                Foreground = (Brush)FindResource("Dim"),
                FontWeight = FontWeights.Medium
            });
            content.Children.Add(new TextBlock
            {
                Text = item.Text.ReplaceLineEndings(" "),
                FontSize = 12.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            card.Content = content;
            card.Click += (_, _) =>
            {
                ignoreNextClipboard = true;
                try
                {
                    Clipboard.SetText(item.Text);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    ignoreNextClipboard = false;
                }
            };
            ClipboardList.Children.Add(card);
        }
    }

    // ---- Notes ---------------------------------------------------------------------------

    private void Notes_Changed(object sender, TextChangedEventArgs e)
    {
        NotesHint.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!notesLoading)
        {
            notesSaveTimer.Stop();
            notesSaveTimer.Start();
        }
    }
}




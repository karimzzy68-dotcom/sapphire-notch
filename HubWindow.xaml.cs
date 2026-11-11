using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

/// <summary>Row shown in the dictation history list.</summary>
public sealed record HistoryRow(DictationEntry Entry, string Text, string Meta);

/// <summary>Row shown in the Notetaker list.</summary>
public sealed record NoteRow(Guid Id, string Title, string Meta);

/// <summary>Desktop hub: dictation history and stats, notes, dictionary, snippets, styles and settings.</summary>
public partial class HubWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int ChartDays = 14;

    private static HubWindow? instance;

    private readonly Dictionary<RadioButton, FrameworkElement> pages;
    private readonly DispatcherTimer noteSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool loading = true;
    private bool syncingNotes;
    private bool loadingNote;
    private Guid? currentNoteId;
    private Guid? editingSnippetId;

    /// <summary>Opens the hub, or brings the open one to the front.</summary>
    public static HubWindow ShowSingle()
    {
        if (instance is { IsLoaded: true })
        {
            if (instance.WindowState == WindowState.Minimized)
            {
                instance.WindowState = WindowState.Normal;
            }
            instance.Activate();
            return instance;
        }
        instance = new HubWindow();
        instance.Show();
        return instance;
    }

    public HubWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyGlass();

        pages = new Dictionary<RadioButton, FrameworkElement>
        {
            [NavDictation] = PageDictation,
            [NavNotes] = PageNotes,
            [NavStats] = PageStats,
            [NavDictionary] = PageDictionary,
            [NavSnippets] = PageSnippets,
            [NavStyles] = PageStyles,
            [NavSettings] = PageSettings,
        };

        WireHint(HistorySearch, HistorySearchHint);
        WireHint(WordInput, WordInputHint);
        WireHint(WordSearch, WordSearchHint);
        WireHint(TriggerInput, TriggerHint);
        WireHint(ExpansionInput, ExpansionHint);

        noteSaveTimer.Tick += (_, _) => FlushNote();

        LoadSettings();
        RefreshHistory();
        RefreshNotes();
        RefreshWords();
        RefreshSnippets();
        UpdatePreview();
        loading = false;

        if (NoteList.Items.Count > 0)
        {
            NoteList.SelectedIndex = 0;
        }
        else
        {
            ShowNote(null);
        }

        DictationStore.Changed += OnStoreChanged;
        SettingsStore.Changed += OnSettingsChanged;
        Closing += (_, _) => FlushNote();
        Closed += (_, _) =>
        {
            DictationStore.Changed -= OnStoreChanged;
            SettingsStore.Changed -= OnSettingsChanged;
            noteSaveTimer.Stop();
            if (instance == this)
            {
                instance = null;
            }
        };
    }

    private void ApplyGlass()
    {
        // Dark title bar, rounded corners and real acrylic, like SettingsWindow.
        nint handle = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
        int acrylic = 3;
        DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref acrylic, sizeof(int));
        HwndSource? hs = HwndSource.FromHwnd(handle);
        if (hs?.CompositionTarget != null)
        {
            hs.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        Margins all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref all);
    }

    /// <summary>Selects a page by its sidebar label ("Dictation", "Notetaker", "Statistics", ...).</summary>
    public void SelectPage(string name)
    {
        foreach (RadioButton nav in pages.Keys)
        {
            if (string.Equals(nav.Content as string, name, StringComparison.OrdinalIgnoreCase))
            {
                nav.IsChecked = true;
                return;
            }
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (pages == null)
        {
            return;
        }
        foreach ((RadioButton nav, FrameworkElement page) in pages)
        {
            page.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (NavNotes.IsChecked != true)
        {
            FlushNote();
        }
    }

    private static void WireHint(TextBox box, TextBlock hint) =>
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnStoreChanged(DictationArea area)
    {
        // The speech engine may call AddEntry from its own thread.
        Dispatcher.BeginInvoke(() =>
        {
            switch (area)
            {
                case DictationArea.History:
                    RefreshHistory();
                    break;
                case DictationArea.Notes:
                    RefreshNotes();
                    break;
                case DictationArea.Dictionary:
                    RefreshWords();
                    break;
                case DictationArea.Snippets:
                    RefreshSnippets();
                    break;
            }
        });
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(() =>
    {
        loading = true;
        LoadSettings();
        loading = false;
    });

    // ---- Dictation + statistics ----

    private void RefreshHistory()
    {
        IReadOnlyList<DictationEntry> entries = DictationStore.Entries;
        string query = HistorySearch.Text.Trim();
        HistoryList.ItemsSource = entries
            .Where(e => query.Length == 0 || e.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e => e.When)
            .Select(e => new HistoryRow(e, e.Text, EntryMeta(e)))
            .ToList();

        bool empty = entries.Count == 0;
        bool noMatch = !empty && HistoryList.Items.Count == 0;
        HistoryEmpty.Visibility = empty || noMatch ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Text = noMatch
            ? $"No dictations match “{query}”."
            : "Your dictations will show here. Hold your dictation key in any app and speak.";
        SamplePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        DateTime today = DateTime.Today;
        string total = DictationStats.TotalWords(entries).ToString("N0");
        string wpm = DictationStats.WordsPerMinute(entries).ToString("N0");
        int streak = DictationStats.DayStreak(entries.Select(e => e.When), today);
        string todayWords = DictationStats.WordsOn(entries, today).ToString("N0");

        DTotal.Text = STotal.Text = total;
        DWpm.Text = SWpm.Text = wpm;
        DStreak.Text = SStreak.Text = streak.ToString("N0");
        DStreakLabel.Text = streak == 1 ? "day in a row" : "days in a row";
        DToday.Text = SToday.Text = todayWords;

        SEntries.Text = entries.Count.ToString("N0");
        int[] perDay = DictationStats.WordsPerDay(entries, today, ChartDays);
        int best = perDay.Length == 0 ? 0 : perDay.Max();
        if (best > 0)
        {
            DateTime bestDay = today.AddDays(Array.LastIndexOf(perDay, best) - (ChartDays - 1));
            SBest.Text = $"{bestDay:ddd d MMM} · {best:N0} words";
        }
        else
        {
            SBest.Text = "None yet";
        }
        double minutes = entries.Sum(e => e.Seconds ?? 0) / 60.0;
        STime.Text = minutes < 1 && minutes > 0 ? "Under 1 min" : $"{Math.Round(minutes):N0} min";

        DrawChart();
    }

    private static string EntryMeta(DictationEntry e)
    {
        string meta = $"{When(e.When)} · {e.Words:N0} {(e.Words == 1 ? "word" : "words")}";
        if (e.Seconds is double s)
        {
            meta += $" · {TimeSpan.FromSeconds(Math.Round(s)):m\\:ss}";
        }
        return meta;
    }

    private static string When(DateTime when)
    {
        DateTime today = DateTime.Today;
        if (when.Date == today)
        {
            return $"Today, {when:HH:mm}";
        }
        if (when.Date == today.AddDays(-1))
        {
            return $"Yesterday, {when:HH:mm}";
        }
        return when.Year == today.Year ? $"{when:ddd d MMM}, {when:HH:mm}" : $"{when:d MMM yyyy}, {when:HH:mm}";
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistory();

    private void AddSample_Click(object sender, RoutedEventArgs e) => DictationStore.AddSampleEntries();

    private void CopyEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryRow row } button)
        {
            return;
        }
        try
        {
            Clipboard.SetText(row.Text);
        }
        catch (ExternalException)
        {
            // Another app is holding the clipboard; the user can simply click again.
            return;
        }
        // Brief check mark so the copy is visible.
        if (button.Content is TextBlock icon)
        {
            icon.Text = "";
            DispatcherTimer back = new() { Interval = TimeSpan.FromSeconds(1.2) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                icon.Text = "";
            };
            back.Start();
        }
    }

    private void TakeNotes_Click(object sender, RoutedEventArgs e)
    {
        FlushNote();
        HubNote note = DictationStore.AddNote($"Call notes, {DateTime.Now:ddd d MMM, HH:mm}\n\n");
        currentNoteId = note.Id;
        RefreshNotes();
        ShowNote(note);
        NavNotes.IsChecked = true;
        NoteEditor.Focus();
        NoteEditor.CaretIndex = NoteEditor.Text.Length;
    }

    /// <summary>Hold-to-talk: opens a fresh note in the Notetaker with the caret ready for voice typing.</summary>
    public void StartVoiceNote()
    {
        FlushNote();
        HubNote note = DictationStore.AddNote($"Voice note, {DateTime.Now:ddd d MMM, HH:mm}\n\n");
        currentNoteId = note.Id;
        RefreshNotes();
        ShowNote(note);
        NavNotes.IsChecked = true;
        Topmost = true; // brings the window forward so the dictated words land in the note
        Activate();
        Topmost = false;
        NoteEditor.Focus();
        NoteEditor.CaretIndex = NoteEditor.Text.Length;
    }

    public void FinishVoiceNote() => FlushNote();

    /// <summary>Adds a dictated phrase to the open note.</summary>
    public void AppendVoiceText(string text)
    {
        string space = NoteEditor.Text.Length > 0 && !char.IsWhiteSpace(NoteEditor.Text[^1]) ? " " : "";
        NoteEditor.AppendText(space + text + " ");
        NoteEditor.CaretIndex = NoteEditor.Text.Length;
        NoteEditor.ScrollToEnd();
    }

    private void DictationSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (loading || sender is not ToggleButton toggle)
        {
            return;
        }
        SettingsStore.Current.DictationEnabled = toggle.IsChecked == true;
        SettingsStore.Save();
    }

    private void UpdateStatus()
    {
        bool on = SettingsStore.Current.DictationEnabled;
        StatusDot.Fill = (Brush)FindResource(on ? "Green" : "Dim4");
        StatusText.Text = on ? "Dictation is on" : "Dictation is off";
    }

    // Bar chart of words per day, drawn with plain shapes so it inherits the glass look.
    private void ChartHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        double width = ChartHost.ActualWidth;
        double height = ChartHost.ActualHeight;
        if (width < 120 || height < 80)
        {
            return;
        }

        DateTime today = DateTime.Today;
        int[] days = DictationStats.WordsPerDay(DictationStore.Entries, today, ChartDays);
        double top = NiceCeiling(days.Max());
        const double left = 46, bottom = 38, topPad = 8;
        double plotW = width - left;
        double plotH = height - bottom - topPad;
        double baseY = topPad + plotH;

        Brush gridBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        Brush labelBrush = (Brush)FindResource("Dim3");
        foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
        {
            double y = baseY - fraction * plotH;
            ChartCanvas.Children.Add(new Line { X1 = left, X2 = width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            TextBlock label = new()
            {
                Text = (top * fraction).ToString("N0"),
                FontSize = 11.5,
                Foreground = labelBrush,
                Width = left - 10,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            ChartCanvas.Children.Add(label);
        }

        double slot = plotW / ChartDays;
        double barW = Math.Min(30, slot * 0.58);
        Brush todayBrush = (Brush)FindResource("Orange");
        Brush pastBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFB, 0x92, 0x3C));
        Brush emptyBrush = (Brush)FindResource("Fill15");
        Brush dayBrush = (Brush)FindResource("Dim");

        for (int i = 0; i < ChartDays; i++)
        {
            DateTime day = today.AddDays(i - (ChartDays - 1));
            int value = days[i];
            double barH = value == 0 ? 3 : Math.Max(5, value / top * plotH);
            double x = left + i * slot + (slot - barW) / 2;

            Rectangle bar = new()
            {
                Width = barW,
                Height = barH,
                RadiusX = Math.Min(5, barW / 2),
                RadiusY = Math.Min(5, barH / 2),
                Fill = value == 0 ? emptyBrush : i == ChartDays - 1 ? todayBrush : pastBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, baseY - barH);
            ChartCanvas.Children.Add(bar);

            // Full-height hit area, so short and empty days still show their tooltip.
            Rectangle hit = new()
            {
                Width = slot,
                Height = plotH + bottom,
                Fill = Brushes.Transparent,
                ToolTip = $"{day:dddd d MMMM}: {value:N0} {(value == 1 ? "word" : "words")}",
            };
            ToolTipService.SetInitialShowDelay(hit, 80);
            hit.MouseEnter += (_, _) => bar.Opacity = 0.75;
            hit.MouseLeave += (_, _) => bar.Opacity = 1;
            Canvas.SetLeft(hit, left + i * slot);
            Canvas.SetTop(hit, topPad);
            ChartCanvas.Children.Add(hit);

            StackPanel dayLabel = new() { Width = slot, IsHitTestVisible = false };
            dayLabel.Children.Add(new TextBlock
            {
                Text = day.ToString("ddd").Substring(0, 2),
                FontSize = 11.5,
                Foreground = labelBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            dayLabel.Children.Add(new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 12,
                FontWeight = i == ChartDays - 1 ? FontWeights.Medium : FontWeights.Normal,
                Foreground = i == ChartDays - 1 ? (Brush)FindResource("Ink") : dayBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Canvas.SetLeft(dayLabel, left + i * slot);
            Canvas.SetTop(dayLabel, baseY + 6);
            ChartCanvas.Children.Add(dayLabel);
        }

        double average = days.Average();
        if (average > 0)
        {
            double y = baseY - average / top * plotH;
            Line avg = new()
            {
                X1 = left,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = (Brush)FindResource("Dim"),
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                IsHitTestVisible = false,
            };
            ChartCanvas.Children.Add(avg);
        }
    }

    /// <summary>Rounds the axis maximum up to 1, 2, 2.5 or 5 times a power of ten.</summary>
    private static double NiceCeiling(int max)
    {
        if (max <= 0)
        {
            return 100;
        }
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (double step in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (step * magnitude >= max)
            {
                return step * magnitude;
            }
        }
        return 10 * magnitude;
    }

    // ---- Notetaker ----

    private void RefreshNotes()
    {
        List<NoteRow> rows = DictationStore.Notes
            .OrderByDescending(n => n.Modified)
            .Select(n =>
            {
                int count = DictationStats.CountWords(n.Text);
                return new NoteRow(n.Id, n.Title, $"{When(n.Modified)} · {count:N0} {(count == 1 ? "word" : "words")}");
            })
            .ToList();
        syncingNotes = true;
        NoteList.ItemsSource = rows;
        NoteList.SelectedItem = rows.FirstOrDefault(r => r.Id == currentNoteId);
        syncingNotes = false;
        NotesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (currentNoteId is Guid id && rows.All(r => r.Id != id))
        {
            ShowNote(null);
        }
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingNotes)
        {
            return;
        }
        FlushNote();
        Guid? id = (NoteList.SelectedItem as NoteRow)?.Id;
        ShowNote(id is Guid g ? DictationStore.Notes.FirstOrDefault(n => n.Id == g) : null);
    }

    private void ShowNote(HubNote? note)
    {
        currentNoteId = note?.Id;
        loadingNote = true;
        NoteEditor.Text = note?.Text ?? string.Empty;
        loadingNote = false;
        NoteEditorPanel.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
        NoteEditorEmpty.Visibility = note == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNoteMeta();
    }

    private void UpdateNoteMeta()
    {
        int count = DictationStats.CountWords(NoteEditor.Text);
        HubNote? note = currentNoteId is Guid id ? DictationStore.Notes.FirstOrDefault(n => n.Id == id) : null;
        string edited = note == null ? string.Empty : $"Edited {When(note.Modified)} · ";
        NoteMeta.Text = $"{edited}{count:N0} {(count == 1 ? "word" : "words")}";
    }

    private void NoteEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loadingNote)
        {
            return;
        }
        UpdateNoteMeta();
        // Save shortly after typing pauses rather than on every keystroke.
        noteSaveTimer.Stop();
        noteSaveTimer.Start();
    }

    private void FlushNote()
    {
        noteSaveTimer.Stop();
        if (currentNoteId is Guid id)
        {
            DictationStore.UpdateNote(id, NoteEditor.Text);
        }
    }

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        FlushNote();
        HubNote note = DictationStore.AddNote(string.Empty);
        currentNoteId = note.Id;
        RefreshNotes();
        ShowNote(note);
        NoteEditor.Focus();
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (currentNoteId is not Guid id)
        {
            return;
        }
        noteSaveTimer.Stop();
        string title = DictationStore.Notes.FirstOrDefault(n => n.Id == id)?.Title ?? "this note";
        if (MessageBox.Show(this, $"Delete “{title}”? This cannot be undone.", "Delete note",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        currentNoteId = null;
        DictationStore.DeleteNote(id);
        RefreshNotes();
        if (NoteList.Items.Count > 0)
        {
            NoteList.SelectedIndex = 0;
        }
    }

    // ---- Dictionary ----

    private void RefreshWords()
    {
        IReadOnlyList<string> words = DictationStore.Words;
        string query = WordSearch.Text.Trim();
        List<string> shown = words.Where(w => query.Length == 0 || w.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        WordList.ItemsSource = shown;
        WordCount.Text = words.Count == 0 ? "YOUR WORDS" : $"YOUR WORDS · {words.Count:N0}";
        WordsEmpty.Text = words.Count == 0 ? "No custom words yet." : $"No words match “{query}”.";
        WordsEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddWord_Click(object sender, RoutedEventArgs e) => AddWord();

    private void WordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddWord();
        }
    }

    private void AddWord()
    {
        string word = WordInput.Text.Trim();
        if (word.Length == 0)
        {
            return;
        }
        if (DictationStore.AddWord(word))
        {
            WordInput.Clear();
            WordMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            WordMessage.Text = $"“{word}” is already in your dictionary.";
            WordMessage.Visibility = Visibility.Visible;
        }
    }

    private void WordInput_TextChanged(object sender, TextChangedEventArgs e) => WordMessage.Visibility = Visibility.Collapsed;

    private void WordSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshWords();

    private void RemoveWord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string word })
        {
            DictationStore.RemoveWord(word);
        }
    }

    // ---- Snippets ----

    private void RefreshSnippets()
    {
        List<Snippet> snippets = DictationStore.Snippets.OrderBy(s => s.Trigger, StringComparer.CurrentCultureIgnoreCase).ToList();
        SnippetList.ItemsSource = snippets;
        SnippetCount.Text = snippets.Count == 0 ? "YOUR SNIPPETS" : $"YOUR SNIPPETS · {snippets.Count:N0}";
        SnippetsEmpty.Visibility = snippets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (editingSnippetId is Guid id && snippets.All(s => s.Id != id))
        {
            ResetSnippetForm();
        }
    }

    private void SnippetInput_TextChanged(object sender, TextChangedEventArgs e) => SnippetMessage.Text = string.Empty;

    private void SaveSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (TriggerInput.Text.Trim().Length == 0 || ExpansionInput.Text.Trim().Length == 0)
        {
            SnippetMessage.Text = "Fill in both the trigger and the text.";
            return;
        }
        if (!DictationStore.SaveSnippet(editingSnippetId, TriggerInput.Text, ExpansionInput.Text.Trim()))
        {
            SnippetMessage.Text = "That trigger is already used by another snippet.";
            return;
        }
        ResetSnippetForm();
    }

    private void CancelSnippet_Click(object sender, RoutedEventArgs e) => ResetSnippetForm();

    private void EditSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Snippet snippet })
        {
            return;
        }
        editingSnippetId = snippet.Id;
        TriggerInput.Text = snippet.Trigger;
        ExpansionInput.Text = snippet.Expansion;
        SnippetFormTitle.Text = "Edit snippet";
        SaveSnippetButton.Content = "Save changes";
        CancelSnippetButton.Visibility = Visibility.Visible;
        PageSnippets.ScrollToTop();
        TriggerInput.Focus();
    }

    private void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Snippet snippet })
        {
            DictationStore.DeleteSnippet(snippet.Id);
        }
    }

    private void ResetSnippetForm()
    {
        editingSnippetId = null;
        TriggerInput.Clear();
        ExpansionInput.Clear();
        SnippetMessage.Text = string.Empty;
        SnippetFormTitle.Text = "New snippet";
        SaveSnippetButton.Content = "Add snippet";
        CancelSnippetButton.Visibility = Visibility.Collapsed;
    }

    // ---- Styles ----

    private DictationStyle SelectedStyle =>
        StyleCasual.IsChecked == true ? DictationStyle.Casual :
        StyleFormal.IsChecked == true ? DictationStyle.Formal :
        StyleBullets.IsChecked == true ? DictationStyle.Bullets : DictationStyle.Raw;

    private void Style_Checked(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (loading)
        {
            return;
        }
        SettingsStore.Current.DictationStyle = SelectedStyle.ToString();
        SettingsStore.Save();
    }

    private void PreviewInput_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewOutput == null)
        {
            return;
        }
        DictationStyle style = SelectedStyle;
        PreviewLabel.Text = $"SAPPHIRE TYPES · {(style == DictationStyle.Bullets ? "BULLET POINTS" : style.ToString().ToUpperInvariant())}";
        PreviewOutput.Text = DictationText.ApplyStyle(PreviewInput.Text, style);
    }

    // ---- Settings ----

    private void LoadSettings()
    {
        AppSettings s = SettingsStore.Current;
        TglDictation.IsChecked = s.DictationEnabled;
        TglDictation2.IsChecked = s.DictationEnabled;
        TglUseDictionary.IsChecked = s.DictationUseDictionary;
        TglSnippets.IsChecked = s.DictationExpandSnippets;
        TglHover.IsChecked = s.OpenOnHover;
        TglStartup.IsChecked = s.StartWithWindows;
        TglMusic.IsChecked = s.ShowMusic;
        TglDev.IsChecked = s.ShowDev;
        TglShare.IsChecked = s.ShowShare;
        TglTools.IsChecked = s.ShowTools;
        TglSystem.IsChecked = s.ShowSystem;
        MarkCloseChoice(s.AutoCloseSeconds);

        RadioButton styleButton = DictationText.ParseStyle(s.DictationStyle) switch
        {
            DictationStyle.Casual => StyleCasual,
            DictationStyle.Formal => StyleFormal,
            DictationStyle.Bullets => StyleBullets,
            _ => StyleRaw,
        };
        styleButton.IsChecked = true;

        DataPath.Text = DictationStore.Folder;
        UpdateStatus();
    }

    private void MarkCloseChoice(int seconds)
    {
        foreach (Button b in new[] { Close5, Close15, Close30 })
        {
            bool on = b.Tag as string == seconds.ToString();
            b.Background = on ? (Brush)FindResource("GlassSelected") : Brushes.Transparent;
            b.Foreground = on ? (Brush)FindResource("Ink") : (Brush)FindResource("Dim");
        }
    }

    private void DictationOptions_Click(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        SettingsStore.Current.DictationUseDictionary = TglUseDictionary.IsChecked == true;
        SettingsStore.Current.DictationExpandSnippets = TglSnippets.IsChecked == true;
        SettingsStore.Save();
    }

    private void General_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        SettingsStore.Current.OpenOnHover = TglHover.IsChecked == true;
        SettingsStore.Current.StartWithWindows = TglStartup.IsChecked == true;
        SettingsStore.Save();
    }

    private void Tab_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }
        AppSettings s = SettingsStore.Current;
        s.ShowMusic = TglMusic.IsChecked == true;
        s.ShowDev = TglDev.IsChecked == true;
        s.ShowShare = TglShare.IsChecked == true;
        s.ShowTools = TglTools.IsChecked == true;
        s.ShowSystem = TglSystem.IsChecked == true;
        SettingsStore.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int seconds))
        {
            SettingsStore.Current.AutoCloseSeconds = seconds;
            MarkCloseChoice(seconds);
            SettingsStore.Save();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DictationStore.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DictationStore.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DictationStore.Entries.Count == 0)
        {
            return;
        }
        if (MessageBox.Show(this, "Delete every dictation in your history? Notes, dictionary and snippets are kept. This cannot be undone.",
                "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
        {
            DictationStore.ClearHistory();
        }
    }
}


using System.IO;
using System.Text.Json;

namespace SapphireNotch.Windows;

/// <summary>One finished dictation as it was typed.</summary>
public sealed class DictationEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime When { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>What the speech engine heard, before style, dictionary and snippets.</summary>
    public string RawText { get; set; } = string.Empty;
    /// <summary>Speaking time, when the engine reports it; null entries are left out of words per minute.</summary>
    public double? Seconds { get; set; }
    public int Words { get; set; }
}

public sealed class HubNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }

    public string Title
    {
        get
        {
            string first = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return first.Length == 0 ? "Untitled note" : first;
        }
    }
}

public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Trigger { get; set; } = string.Empty;
    public string Expansion { get; set; } = string.Empty;
}

public enum DictationArea
{
    History,
    Notes,
    Dictionary,
    Snippets
}

/// <summary>
/// Dictation history, notes, custom words and snippets, as JSON in %LocalAppData%\Sapphire NOTCh\.
/// Thread-safe so a speech engine can call <see cref="AddEntry"/> from its own thread;
/// <see cref="Changed"/> is raised on the caller's thread, so UI listeners must marshal to their dispatcher.
/// </summary>
public static class DictationStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Not readonly and loaded lazily, so nothing touches the disk until the hub or the engine needs it.
    private static string folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh");

    private static List<DictationEntry>? entries;
    private static List<HubNote>? notes;
    private static List<string>? words;
    private static List<Snippet>? snippets;

    public static event Action<DictationArea>? Changed;

    public static string Folder => folder;

    public static IReadOnlyList<DictationEntry> Entries
    {
        get { lock (Gate) { return History.ToArray(); } }
    }

    public static IReadOnlyList<HubNote> Notes
    {
        get { lock (Gate) { return NoteList.ToArray(); } }
    }

    public static IReadOnlyList<string> Words
    {
        get { lock (Gate) { return WordList.ToArray(); } }
    }

    public static IReadOnlyList<Snippet> Snippets
    {
        get { lock (Gate) { return SnippetList.ToArray(); } }
    }

    private static List<DictationEntry> History => entries ??= Load<DictationEntry>("dictation.json");
    private static List<HubNote> NoteList => notes ??= Load<HubNote>("notes.json");
    private static List<string> WordList => words ??= Load<string>("dictionary.json");
    private static List<Snippet> SnippetList => snippets ??= Load<Snippet>("snippets.json");

    // ---- History ----

    /// <summary>
    /// Entry point for the speech engine: applies the chosen style, dictionary and snippets, appends the
    /// result to history and saves it. Returns the stored entry, or null when there was nothing to keep.
    /// </summary>
    public static DictationEntry? AddEntry(string text, double? seconds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        AppSettings s = SettingsStore.Current;
        DictationEntry entry;
        lock (Gate)
        {
            string typed = DictationText.Process(
                text,
                DictationText.ParseStyle(s.DictationStyle),
                s.DictationUseDictionary ? WordList : null,
                s.DictationExpandSnippets ? SnippetList : null);
            entry = new DictationEntry
            {
                When = DateTime.Now,
                Text = typed,
                RawText = text.Trim(),
                Seconds = seconds is > 0 ? seconds : null,
                Words = DictationStats.CountWords(typed),
            };
            History.Add(entry);
            Save("dictation.json", History);
        }
        Changed?.Invoke(DictationArea.History);
        return entry;
    }

    /// <summary>Demo data for the hub while no speech engine is connected (only offered when history is empty).</summary>
    public static void AddSampleEntries()
    {
        DateTime now = DateTime.Now;
        (string Text, double Seconds, DateTime When)[] samples =
        {
            ("Hi Maya, thanks for sending the draft over. I went through it this morning and it reads well. Could you tighten the second section and add the figures from the March report? Then I think we are ready to share it with the team.",
                19.5, now.AddDays(-2).Date.AddHours(9).AddMinutes(41)),
            ("Reminder for Thursday: book the meeting room, order lunch for eight people, and send the agenda to everyone by Wednesday evening.",
                9.0, now.AddDays(-1).Date.AddHours(16).AddMinutes(5)),
            ("The build is green again. The failing test was a time zone issue in the date parser, so I pinned the culture and added a regression test.",
                11.2, now.AddMinutes(-12)),
        };
        lock (Gate)
        {
            foreach ((string text, double seconds, DateTime when) in samples)
            {
                History.Add(new DictationEntry
                {
                    When = when,
                    Text = text,
                    RawText = text,
                    Seconds = seconds,
                    Words = DictationStats.CountWords(text),
                });
            }
            History.Sort((a, b) => a.When.CompareTo(b.When));
            Save("dictation.json", History);
        }
        Changed?.Invoke(DictationArea.History);
    }

    public static void ClearHistory()
    {
        lock (Gate)
        {
            History.Clear();
            Save("dictation.json", History);
        }
        Changed?.Invoke(DictationArea.History);
    }

    // ---- Notes ----

    public static HubNote AddNote(string text)
    {
        HubNote note;
        lock (Gate)
        {
            note = new HubNote { Text = text, Created = DateTime.Now, Modified = DateTime.Now };
            NoteList.Add(note);
            Save("notes.json", NoteList);
        }
        Changed?.Invoke(DictationArea.Notes);
        return note;
    }

    public static void UpdateNote(Guid id, string text)
    {
        lock (Gate)
        {
            HubNote? note = NoteList.FirstOrDefault(n => n.Id == id);
            if (note == null || note.Text == text)
            {
                return;
            }
            note.Text = text;
            note.Modified = DateTime.Now;
            Save("notes.json", NoteList);
        }
        Changed?.Invoke(DictationArea.Notes);
    }

    public static void DeleteNote(Guid id)
    {
        lock (Gate)
        {
            if (NoteList.RemoveAll(n => n.Id == id) == 0)
            {
                return;
            }
            Save("notes.json", NoteList);
        }
        Changed?.Invoke(DictationArea.Notes);
    }

    // ---- Dictionary ----

    /// <summary>Adds a word or phrase; false when it is empty or already there (case-insensitive).</summary>
    public static bool AddWord(string word)
    {
        string clean = word.Trim();
        lock (Gate)
        {
            if (clean.Length == 0 || WordList.Any(w => string.Equals(w, clean, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            WordList.Add(clean);
            WordList.Sort(StringComparer.CurrentCultureIgnoreCase);
            Save("dictionary.json", WordList);
        }
        Changed?.Invoke(DictationArea.Dictionary);
        return true;
    }

    public static void RemoveWord(string word)
    {
        lock (Gate)
        {
            if (!WordList.Remove(word))
            {
                return;
            }
            Save("dictionary.json", WordList);
        }
        Changed?.Invoke(DictationArea.Dictionary);
    }

    // ---- Snippets ----

    /// <summary>Adds a snippet, or updates the one with <paramref name="id"/>. False when the trigger is empty or taken.</summary>
    public static bool SaveSnippet(Guid? id, string trigger, string expansion)
    {
        string cleanTrigger = trigger.Trim();
        lock (Gate)
        {
            if (cleanTrigger.Length == 0 ||
                SnippetList.Any(s => s.Id != id && string.Equals(s.Trigger, cleanTrigger, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            Snippet? existing = id is Guid g ? SnippetList.FirstOrDefault(s => s.Id == g) : null;
            if (existing == null)
            {
                SnippetList.Add(new Snippet { Trigger = cleanTrigger, Expansion = expansion });
            }
            else
            {
                existing.Trigger = cleanTrigger;
                existing.Expansion = expansion;
            }
            Save("snippets.json", SnippetList);
        }
        Changed?.Invoke(DictationArea.Snippets);
        return true;
    }

    public static void DeleteSnippet(Guid id)
    {
        lock (Gate)
        {
            if (SnippetList.RemoveAll(s => s.Id == id) == 0)
            {
                return;
            }
            Save("snippets.json", SnippetList);
        }
        Changed?.Invoke(DictationArea.Snippets);
    }

    // ---- IO ----

    private static List<T> Load<T>(string name)
    {
        try
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path))?.Where(x => x != null).ToList() ?? new List<T>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new List<T>();
    }

    private static void Save<T>(string name, List<T> items)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            // Write beside the target and swap, so a crash mid-write never leaves a half-written file.
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items, WriteOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

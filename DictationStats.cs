using System.Text.RegularExpressions;

namespace SapphireNotch.Windows;

/// <summary>Writing styles a dictation can be typed in.</summary>
public enum DictationStyle
{
    Raw,
    Casual,
    Formal,
    Bullets
}

/// <summary>Pure stats math over dictation history: no IO and no UI, so it can be tested on its own.</summary>
public static class DictationStats
{
    /// <summary>Counts tokens that contain a letter or digit, so stray dashes and bullets are not words.</summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        int count = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord && char.IsLetterOrDigit(c))
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    public static int TotalWords(IEnumerable<DictationEntry> entries) => entries.Sum(e => e.Words);

    /// <summary>
    /// Average speaking speed over entries that have a duration. Weighted by time (total words / total minutes),
    /// so a two-second fragment cannot skew the number the way a mean of per-entry rates would.
    /// </summary>
    public static int WordsPerMinute(IEnumerable<DictationEntry> entries)
    {
        double seconds = 0;
        int words = 0;
        foreach (DictationEntry e in entries)
        {
            if (e.Seconds is double s && s > 0)
            {
                seconds += s;
                words += e.Words;
            }
        }
        return seconds < 1 ? 0 : (int)Math.Round(words / (seconds / 60.0));
    }

    /// <summary>
    /// Consecutive days with at least one entry, ending today. A streak that ended yesterday still counts,
    /// because today is not over yet.
    /// </summary>
    public static int DayStreak(IEnumerable<DateTime> timestamps, DateTime today)
    {
        HashSet<DateTime> days = timestamps.Select(t => t.Date).ToHashSet();
        DateTime day = today.Date;
        if (!days.Contains(day))
        {
            day = day.AddDays(-1);
        }
        int streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    public static int WordsOn(IEnumerable<DictationEntry> entries, DateTime day) =>
        entries.Where(e => e.When.Date == day.Date).Sum(e => e.Words);

    /// <summary>Words per day for the last <paramref name="days"/> days; index 0 is the oldest, the last is today.</summary>
    public static int[] WordsPerDay(IEnumerable<DictationEntry> entries, DateTime today, int days)
    {
        int[] totals = new int[Math.Max(0, days)];
        DateTime first = today.Date.AddDays(-(days - 1));
        foreach (DictationEntry e in entries)
        {
            int index = (e.When.Date - first).Days;
            if (index >= 0 && index < totals.Length)
            {
                totals[index] += e.Words;
            }
        }
        return totals;
    }
}

/// <summary>Pure text transforms applied to a dictation before it is typed: style, dictionary spelling, snippets.</summary>
public static class DictationText
{
    private static readonly Regex Fillers = new(@"\b(?:um+|uh+|erm+|uhm+|hmm+)\b,?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+");
    private static readonly Regex SpaceBeforePunct = new(@"\s+([,.!?;:])");
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+");

    // Long form -> contraction. Casual contracts, Formal expands.
    private static readonly (string Long, string Short)[] Contractions =
    {
        ("do not", "don't"), ("does not", "doesn't"), ("did not", "didn't"), ("is not", "isn't"),
        ("are not", "aren't"), ("was not", "wasn't"), ("were not", "weren't"), ("have not", "haven't"),
        ("cannot", "can't"), ("will not", "won't"), ("would not", "wouldn't"), ("should not", "shouldn't"),
        ("could not", "couldn't"), ("I am", "I'm"), ("I will", "I'll"), ("I have", "I've"), ("you are", "you're"),
        ("we are", "we're"), ("they are", "they're"), ("it is", "it's"), ("that is", "that's"), ("there is", "there's"),
    };

    private static readonly (string Slang, string Formal)[] Slang =
    {
        ("gonna", "going to"), ("wanna", "want to"), ("gotta", "have to"), ("kinda", "kind of"), ("sorta", "sort of"), ("yeah", "yes"),
    };

    public static DictationStyle ParseStyle(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out DictationStyle style) ? style : DictationStyle.Raw;

    /// <summary>Full pipeline: style first, then dictionary spelling, then snippets so expansions are typed verbatim.</summary>
    public static string Process(string text, DictationStyle style, IEnumerable<string>? dictionary, IEnumerable<Snippet>? snippets)
    {
        string result = ApplyStyle(text, style);
        if (dictionary != null)
        {
            result = ApplyDictionary(result, dictionary);
        }
        if (snippets != null)
        {
            result = ApplySnippets(result, snippets);
        }
        return result;
    }

    public static string ApplyStyle(string text, DictationStyle style)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        if (style == DictationStyle.Raw)
        {
            return text.Trim();
        }
        string clean = Tidy(Fillers.Replace(text.Replace('’', '\''), " "));
        return style switch
        {
            DictationStyle.Casual => Casual(clean),
            DictationStyle.Formal => Formal(clean),
            DictationStyle.Bullets => Bullets(clean),
            _ => clean,
        };
    }

    private static string Casual(string text)
    {
        foreach ((string longForm, string shortForm) in Contractions)
        {
            text = ReplaceWord(text, longForm, shortForm);
        }
        // A relaxed message rarely ends with a full stop; keep ! and ? because they carry meaning.
        return text.EndsWith('.') && !text.EndsWith("..") ? text[..^1] : text;
    }

    private static string Formal(string text)
    {
        foreach ((string longForm, string shortForm) in Contractions)
        {
            text = ReplaceWord(text, shortForm, longForm);
        }
        foreach ((string slang, string formal) in Slang)
        {
            text = ReplaceWord(text, slang, formal);
        }
        text = Regex.Replace(text, @"(?<!\w)i(?=$|[\s,.!?;:'])", "I");
        IEnumerable<string> sentences = SentenceBreak.Split(text).Where(s => s.Length > 0).Select(Capitalize);
        string joined = string.Join(" ", sentences);
        return EndsWithPunctuation(joined) ? joined : joined + ".";
    }

    private static string Bullets(string text)
    {
        IEnumerable<string> items = Regex.Split(text, @"(?<=[.!?;])\s+|\s*;\s*")
            .Select(s => s.Trim().TrimEnd('.', ';').Trim())
            .Where(s => s.Length > 0)
            .Select(s => "• " + Capitalize(s));
        return string.Join("\n", items);
    }

    /// <summary>Rewrites words from the custom dictionary with their saved spelling and capitalisation.</summary>
    public static string ApplyDictionary(string text, IEnumerable<string> words)
    {
        foreach (string word in words.Where(w => !string.IsNullOrWhiteSpace(w)).OrderByDescending(w => w.Length))
        {
            string target = word.Trim();
            text = Regex.Replace(text, @"(?<!\w)" + Regex.Escape(target) + @"(?!\w)", _ => target, RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>Expands snippet triggers in one pass, so an expansion is never expanded again.</summary>
    public static string ApplySnippets(string text, IEnumerable<Snippet> snippets)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (Snippet s in snippets)
        {
            string trigger = s.Trigger.Trim();
            if (trigger.Length > 0)
            {
                map[trigger] = s.Expansion;
            }
        }
        if (map.Count == 0)
        {
            return text;
        }
        string pattern = @"(?<!\w)(?:" + string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?!\w)";
        return Regex.Replace(text, pattern, m => map[m.Value], RegexOptions.IgnoreCase);
    }

    private static string Tidy(string text) => SpaceBeforePunct.Replace(Spaces.Replace(text, " "), "$1").Trim();

    private static string ReplaceWord(string text, string from, string to) =>
        Regex.Replace(text, @"(?<!\w)" + Regex.Escape(from) + @"(?!\w)", m =>
        {
            // Keep a capital at the start of a sentence ("It is" -> "It's").
            bool upper = char.IsUpper(m.Value[0]);
            return upper && to.Length > 0 ? char.ToUpperInvariant(to[0]) + to[1..] : to;
        }, RegexOptions.IgnoreCase);

    private static string Capitalize(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsLetter(s[i]))
            {
                return s[..i] + char.ToUpper(s[i]) + s[(i + 1)..];
            }
        }
        return s;
    }

    private static bool EndsWithPunctuation(string s) => s.Length > 0 && ".!?:".Contains(s[^1]);
}

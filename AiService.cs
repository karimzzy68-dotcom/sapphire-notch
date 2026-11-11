using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SapphireNotch.Windows;

/// <summary>One chat turn sent to the model.</summary>
internal sealed record ChatTurn(string Role, string Content, string? ImageDataUrl = null);

/// <summary>OpenRouter client: any model, one API key. The key is encrypted for this Windows user (DPAPI).</summary>
internal static class AiService
{
    private const string Endpoint = "https://openrouter.ai/api/v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    // Two gateways, one chat: OpenRouter (cloud) or OmniRoute (a local gateway you run yourself). Same API format.
    public static bool UsingOmniRoute => SettingsStore.Current.AiProvider == "OmniRoute";

    public static string ProviderName => UsingOmniRoute ? "OmniRoute" : "OpenRouter";

    private static string BaseUrl => UsingOmniRoute ? SettingsStore.Current.AiBaseUrl.Trim().TrimEnd('/') : Endpoint;

    private static string KeyFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh",
        UsingOmniRoute ? "omniroute.key" : "openrouter.key");

    // OpenRouter's own router: picks a working free model for each request, so it never goes stale.
    // (The "openrouter/free" router was dropped: it can route to a safety classifier that only answers "safe".)
    public const string AutoFreeModel = "nvidia/nemotron-3-ultra-550b-a55b:free";

    // Free model that can see images (Nemotron Ultra is text only).
    public const string VisionFreeModel = "google/gemma-4-31b-it:free";

    private static readonly string[] FreeFallbacks = { "google/gemma-4-31b-it:free", "thinkingmachines/inkling:free" };

    // Hand-picked: the strongest free models first, then paid ones (need OpenRouter credits; prices per 1M tokens in/out).
    public static readonly (string Label, string Id)[] BestModels =
    {
        ("Nemotron Ultra - free (recommended)", AutoFreeModel),
        ("Gemma 4 31B - free, lighter", "google/gemma-4-31b-it:free"),
        ("DeepSeek V4.1 Flash - cheapest, fast", "deepseek/deepseek-v4.1-flash"),
        ("Claude Haiku 5.5 - fast, cheap", "anthropic/claude-haiku-5.5"),
        ("GPT-6.1 Terra - balanced", "openai/gpt-6.1-terra"),
        ("Claude Sonnet 5.5 - best value", "anthropic/claude-sonnet-5.5"),
        ("Claude Opus 5.5 - smartest", "anthropic/claude-opus-5.5"),
        ("Auto Router - picks per question (paid)", "openrouter/auto")
    };

    public static string LabelFor(string id) => BestModels.FirstOrDefault(m => m.Id == id).Label ?? BestModels[0].Label;

    public static string IdFor(string label) => BestModels.FirstOrDefault(m => m.Label == label).Id ?? AutoFreeModel;

    // ---- Key -------------------------------------------------------------------------------

    public static bool HasKey => LoadKey().Length > 0;

    public static void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        byte[] sealedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFile, sealedKey);
    }

    public static void ClearKey()
    {
        try
        {
            File.Delete(KeyFile);
        }
        catch (IOException)
        {
        }
    }

    private static string LoadKey()
    {
        try
        {
            if (File.Exists(KeyFile))
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser));
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
        }
        return "";
    }

    // ---- Models ----------------------------------------------------------------------------

    /// <summary>Free models first (their price is 0), then everything else; no key needed for the list.</summary>
    public static async Task<List<string>> ListModelsAsync()
    {
        using HttpRequestMessage listRequest = new(HttpMethod.Get, $"{BaseUrl}/models");
        string listKey = LoadKey();
        if (listKey.Length > 0)
        {
            listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listKey);
        }
        using HttpResponseMessage response = await Http.SendAsync(listRequest);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<(string Id, bool Free)> models = new();
        foreach (JsonElement m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = m.GetProperty("id").GetString() ?? "";
            bool free = id.EndsWith(":free", StringComparison.Ordinal);
            models.Add((id, free));
        }
        if (UsingOmniRoute)
        {
            return models.Select(m => m.Id).Where(id => id.Length > 0).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
        List<string> ordered = models.OrderByDescending(m => m.Free).ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id).ToList();
        ordered.Remove(AutoFreeModel);
        ordered.Insert(0, AutoFreeModel);
        return ordered;
    }

    // ---- Chat ------------------------------------------------------------------------------

    public static async Task<string> ChatAsync(string model, string system, IReadOnlyList<ChatTurn> history)
    {
        string key = LoadKey();
        if (key.Length == 0 && !UsingOmniRoute)
        {
            return "Add your OpenRouter key first (the Key button). Get a free one at openrouter.ai/keys.";
        }
        if (model.Length == 0)
        {
            return "Pick a model first (the model list loads from " + ProviderName + ").";
        }

        List<object> messages = new() { new { role = "system", content = system } };
        // only the newest message carries its picture: older ones would cost credits on every turn
        messages.AddRange(history.Select((t, i) => i == history.Count - 1 && t.ImageDataUrl != null
            ? (object)new
            {
                role = t.Role,
                content = new object[]
                {
                    new { type = "text", text = t.Content },
                    new { type = "image_url", image_url = new { url = t.ImageDataUrl } }
                }
            }
            : new { role = t.Role, content = t.Content }));
        bool hasImage = history.Count > 0 && history[^1].ImageDataUrl != null;
        // free models are busy at times: let OpenRouter fall through to the next free one instead of failing
        Dictionary<string, object> payload = new()
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = 1200,
            ["temperature"] = 0.4
        };
        if (!UsingOmniRoute && !hasImage && model.EndsWith(":free", StringComparison.Ordinal))
        {
            payload["models"] = FreeFallbacks.Prepend(model).Distinct().ToArray();
        }
        string body = JsonSerializer.Serialize(payload);

        using HttpRequestMessage request = new(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        request.Headers.Add("HTTP-Referer", "https://sapphire-notch.local");
        request.Headers.Add("X-Title", "Sapphire NOTCh");

        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request);
            // OpenRouter sends no charset: read as UTF-8 ourselves, or apostrophes come out as "â€™"
            string text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
            if (!response.IsSuccessStatusCode)
            {
                return Describe((int)response.StatusCode, text);
            }
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement choice = doc.RootElement.GetProperty("choices")[0];
            string? content = choice.GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrEmpty(content))
            {
                return "This model spent its whole budget thinking and gave no answer. Pick \"openrouter/free\" or a model that is not a reasoning model.";
            }
            return content;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return UsingOmniRoute
                ? "Could not reach OmniRoute at " + BaseUrl + ". Is it running (run `omniroute` in a terminal)? " + ex.Message
                : "Could not reach OpenRouter: " + ex.Message;
        }
    }

    /// <summary>Like ChatAsync, but the answer arrives word by word (onText gets the whole text so far) and is capped short. Much faster to first words.</summary>
    public static async Task<string> ChatStreamAsync(string model, string system, IReadOnlyList<ChatTurn> history, Action<string> onText, int maxTokens = 350)
    {
        string key = LoadKey();
        if (key.Length == 0 && !UsingOmniRoute)
        {
            return "Add your OpenRouter key first (Chat tab, Key button). Get a free one at openrouter.ai/keys.";
        }
        List<object> messages = new() { new { role = "system", content = system } };
        messages.AddRange(history.Select((t, i) => i == history.Count - 1 && t.ImageDataUrl != null
            ? (object)new
            {
                role = t.Role,
                content = new object[] { new { type = "text", text = t.Content }, new { type = "image_url", image_url = new { url = t.ImageDataUrl } } }
            }
            : new { role = t.Role, content = t.Content }));
        Dictionary<string, object> payload = new()
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.4,
            ["stream"] = true
        };
        using HttpRequestMessage request = new(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (key.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        request.Headers.Add("HTTP-Referer", "https://sapphire-notch.local");
        request.Headers.Add("X-Title", "Sapphire NOTCh");
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return Describe((int)response.StatusCode, Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()));
            }
            using System.IO.StreamReader reader = new(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
            StringBuilder all = new();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                string data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("delta", out JsonElement delta)
                        && delta.TryGetProperty("content", out JsonElement piece) && piece.ValueKind == JsonValueKind.String)
                    {
                        all.Append(piece.GetString());
                        onText(all.ToString());
                    }
                }
                catch (JsonException)
                {
                }
            }
            return all.Length > 0 ? all.ToString().Trim() : "The model sent no answer. Try again.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return "Could not reach " + ProviderName + ": " + ex.Message;
        }
    }

    private static string Describe(int status, string body)
    {
        string detail = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement err) && err.TryGetProperty("message", out JsonElement msg))
            {
                detail = msg.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            401 => ProviderName + " rejected the key. Check it and save it again.",
            402 => "This model needs credits. Pick openrouter/free or a model ending in :free.",
            404 => "That model is no longer available. Pick another from the list (openrouter/free always works).",
            429 => "Rate limited (free models are capped). Wait a minute or pick another model.",
            _ => $"OpenRouter error {status}. {detail}".Trim()
        };
    }

    // ---- Prompts: the agency, the ADHD answer style, planning ------------------------------

    private const string StyleRules =
        "ANSWER STYLE (always): the reader has ADHD. Lead with the next action in the first line. " +
        "Use a numbered list for multi-step tasks, never more than 5 items. No preamble, no recap, no closing offers. " +
        "Give specific time estimates (e.g. '10 min'), never 'a while'. Report errors or bad news plainly, without apology. " +
        "End with exactly one concrete next step. Keep it under 120 words.";

    private const string PlanningRules =
        "PLANNING (use when asked to plan or schedule): state the outcome in one line, break it into steps of 25 minutes or less, " +
        "put them in time blocks with start times, put the hardest step first, and add one 10 minute buffer.";

    public static readonly string[] Personas = { "Auto", "Founder", "CTO", "CMO", "COO", "CFO", "Designer", "Planner", "Coach" };

    private static string Role(string persona) => persona switch
    {
        "Founder" => "You are the FOUNDER: vision, priorities, what to say no to, the one thing that matters most this week.",
        "CTO" => "You are the CTO: architecture, bugs, build vs buy, shipping safely. Be concrete and technical.",
        "CMO" => "You are the CMO: positioning, ads, funnels, content, email flows. Name the channel, the offer and the metric.",
        "COO" => "You are the COO: process, schedules, delegation, checklists, removing blockers.",
        "CFO" => "You are the CFO: costs, pricing, runway, return on effort. Use numbers.",
        "Designer" => "You are the head of design: layout, hierarchy, brand, usability. Give specific fixes.",
        "Planner" => "You are a planner: turn goals into a schedule. " + PlanningRules,
        "Coach" => "You are the user's accountability coach in the style of a 15-minute check-in: ask what they did, compare it with their stated goal, say plainly if they are on track or lacking, then give the next 15-minute target.",
        _ => "You are the leadership team of a small agency: Founder, CTO, CMO, COO, CFO and Head of Design. Pick only the one or two roles this question needs and answer as them, starting each with its tag like [CTO]."
    };

    /// <summary>Added when the user pressed Look: the image is a screenshot of what they are working on.</summary>
    public const string LookRules =
        " The image is a screenshot of the app the user is working in, sent because they pressed Look. " +
        "Say which app and screen you see, answer their question, then give numbered next steps that name the exact buttons, menus or fields you can see. " +
        "You only guide: never say you clicked or typed anything for them. If the screenshot is too small or unclear, say what you could not read instead of guessing. " +
        "Do not repeat passwords, keys, card numbers or other private details you can see.";

    public static string SystemPrompt(string persona) => Role(persona) + " " + PlanningRules + " " + StyleRules;
}

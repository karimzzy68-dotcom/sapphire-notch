using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SapphireNotch.Windows;

/// <summary>
/// Optional better dictation: when VoiceStudio (github.com/debpalash/VoiceStudio) is running on this PC, its speech-to-text
/// is used instead of the built-in Whisper. Talks only to 127.0.0.1; if it is not running, dictation falls back to the built-in model.
/// </summary>
internal static class VoiceStudioClient
{
    private const string Base = "http://127.0.0.1:3900";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static bool Available { get; private set; }

    public static async Task<bool> ProbeAsync()
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(800));
            using HttpResponseMessage response = await Http.GetAsync(Base + "/.well-known/voicestudio-speech", cts.Token);
            Available = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Available = false;
        }
        return Available;
    }

    /// <summary>OpenAI-compatible transcription call; null when VoiceStudio cannot be reached or answers badly.</summary>
    public static async Task<string?> TranscribeAsync(Stream wav)
    {
        try
        {
            using MultipartFormDataContent form = new();
            form.Add(new StreamContent(wav), "file", "dictation.wav");
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("en"), "language");
            using HttpResponseMessage response = await Http.PostAsync(Base + "/v1/audio/transcriptions", form);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            return doc.RootElement.TryGetProperty("text", out JsonElement text) ? text.GetString()?.Trim() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }
}

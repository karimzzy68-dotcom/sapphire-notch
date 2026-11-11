using System.Collections.Concurrent;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;

namespace SapphireNotch.Windows;

/// <summary>
/// Meeting listener: records what the other side says (the sound playing on this PC) and what you say (the microphone),
/// cuts it into sentences at pauses and transcribes each one with Whisper on this PC. No audio leaves the PC.
/// </summary>
internal sealed class MeetingCopilot : IDisposable
{
    private const int Rate = 16000;
    private const float SpeechLevel = 0.01f;

    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh", "models", "ggml-base.en.bin");

    private sealed class Channel(string who)
    {
        public readonly string Who = who;
        public readonly List<float> Buffer = new();
        public int Silent;
        public bool Heard;
        public double Phase;
    }

    private readonly Channel them = new("Them"), you = new("You");
    private readonly BlockingCollection<(string Who, float[] Samples)> queue = new(8);
    private readonly List<(DateTime Time, string Text)> recentThem = new();
    private WasapiLoopbackCapture? loopback;
    private WaveInEvent? mic;
    private WhisperFactory? factory;
    private CancellationTokenSource? cts;

    /// <summary>Speaker ("Them" or "You") and what was said.</summary>
    public event Action<string, string>? Heard;
    public event Action<string>? Failed;

    public bool Running { get; private set; }

    public static bool ModelInstalled => File.Exists(ModelPath);

    /// <summary>One-time download of the speech model (141 MB, from the whisper.cpp project on Hugging Face). Returns an error message, or null when done.</summary>
    public static async Task<string?> DownloadModelAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            using System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromMinutes(15) };
            using System.Net.Http.HttpResponseMessage response = await http.GetAsync(
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin", System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            string temp = ModelPath + ".part";
            await using (Stream input = await response.Content.ReadAsStreamAsync())
            await using (FileStream output = File.Create(temp))
            {
                await input.CopyToAsync(output);
            }
            File.Move(temp, ModelPath, true);
            return null;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException)
        {
            return ex.Message;
        }
    }

    public void Start(bool useMic)
    {
        if (Running)
        {
            return;
        }
        cts = new CancellationTokenSource();
        Running = true;
        try
        {
            loopback = new WasapiLoopbackCapture();
            loopback.DataAvailable += (_, e) => FeedFloat(them, e.Buffer, e.BytesRecorded, loopback!.WaveFormat);
            loopback.StartRecording();
            if (useMic)
            {
                mic = new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 100 };
                mic.DataAvailable += (_, e) => FeedPcm16(you, e.Buffer, e.BytesRecorded);
                mic.StartRecording();
            }
        }
        catch (Exception ex)
        {
            Stop();
            Failed?.Invoke("Could not start listening: " + ex.Message);
            return;
        }
        CancellationToken token = cts.Token;
        Task.Run(() => Worker(token));
    }

    public void Stop()
    {
        Running = false;
        try { loopback?.StopRecording(); } catch (Exception) { }
        try { mic?.StopRecording(); } catch (Exception) { }
        loopback?.Dispose();
        mic?.Dispose();
        loopback = null;
        mic = null;
        cts?.Cancel();
        Flush(them);
        Flush(you);
    }

    public void Dispose()
    {
        Stop();
        factory?.Dispose();
    }

    // ---- audio in ------------------------------------------------------------------------------

    private void FeedFloat(Channel ch, byte[] bytes, int count, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int frames = count / (4 * channels);
        float[] mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += BitConverter.ToSingle(bytes, (i * channels + c) * 4);
            }
            mono[i] = sum / channels;
        }
        Add(ch, Resample(ch, mono, format.SampleRate));
    }

    private void FeedPcm16(Channel ch, byte[] bytes, int count)
    {
        float[] samples = new float[count / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }
        Add(ch, samples);
    }

    private static float[] Resample(Channel ch, float[] input, int rate)
    {
        if (rate == Rate)
        {
            return input;
        }
        double step = rate / (double)Rate;
        List<float> output = new((int)(input.Length / step) + 1);
        double pos = ch.Phase;
        while (pos < input.Length)
        {
            int i = (int)pos;
            int j = Math.Min(i + 1, input.Length - 1);
            double frac = pos - i;
            output.Add((float)(input[i] * (1 - frac) + input[j] * frac));
            pos += step;
        }
        ch.Phase = pos - input.Length;
        return output.ToArray();
    }

    // Cuts at pauses: a sentence ends after about 0.9 s of quiet, or at 15 s whatever happens.
    private void Add(Channel ch, float[] samples)
    {
        if (samples.Length == 0 || !Running)
        {
            return;
        }
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }
        bool speech = Math.Sqrt(sum / samples.Length) > SpeechLevel;
        lock (ch)
        {
            ch.Buffer.AddRange(samples);
            if (speech)
            {
                ch.Heard = true;
                ch.Silent = 0;
            }
            else
            {
                ch.Silent += samples.Length;
            }
            if (!ch.Heard)
            {
                int keep = Rate / 3;
                if (ch.Buffer.Count > keep)
                {
                    ch.Buffer.RemoveRange(0, ch.Buffer.Count - keep);
                }
                return;
            }
            if ((ch.Silent >= Rate * 9 / 10 && ch.Buffer.Count > Rate) || ch.Buffer.Count > Rate * 15)
            {
                Flush(ch);
            }
        }
    }

    private void Flush(Channel ch)
    {
        float[] chunk;
        lock (ch)
        {
            if (!ch.Heard || ch.Buffer.Count < Rate / 2)
            {
                ch.Buffer.Clear();
                ch.Heard = false;
                ch.Silent = 0;
                return;
            }
            chunk = ch.Buffer.ToArray();
            ch.Buffer.Clear();
            ch.Heard = false;
            ch.Silent = 0;
        }
        // Falling behind? drop the oldest sentence rather than the newest.
        if (!queue.TryAdd((ch.Who, chunk)))
        {
            queue.TryTake(out _);
            queue.TryAdd((ch.Who, chunk));
        }
    }

    // ---- speech to text ------------------------------------------------------------------------

    private async Task Worker(CancellationToken token)
    {
        try
        {
            factory ??= WhisperFactory.FromPath(ModelPath);
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Could not load the speech model: " + ex.Message);
            return;
        }
        while (!token.IsCancellationRequested)
        {
            (string who, float[] samples) item;
            try
            {
                item = queue.Take(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                string text = await Transcribe(item.samples);
                if (IsNoise(text) || IsEcho(item.who, text))
                {
                    continue;
                }
                if (item.who == "Them")
                {
                    lock (recentThem)
                    {
                        recentThem.Add((DateTime.Now, text));
                        recentThem.RemoveAll(r => DateTime.Now - r.Time > TimeSpan.FromSeconds(20));
                    }
                }
                Heard?.Invoke(item.who, text);
            }
            catch (Exception ex)
            {
                Failed?.Invoke("Transcribing failed: " + ex.Message);
            }
        }
    }

    /// <summary>Developer aid for the self-test: transcribes a 16 kHz mono clip.</summary>
    public async Task<string> TranscribeClip(float[] samples)
    {
        factory ??= WhisperFactory.FromPath(ModelPath);
        return await Transcribe(samples);
    }

    private async Task<string> Transcribe(float[] samples)
    {
        double seconds = samples.Length / (double)Rate;
        WhisperProcessorBuilder builder = factory!.CreateBuilder().WithLanguage("en").WithThreads(4).WithNoContext();
        if (seconds < 25)
        {
            builder = builder.WithAudioContextSize((int)Math.Min(1500, Math.Max(384, seconds * 50 + 150)));
        }
        await using WhisperProcessor processor = builder.Build();
        System.Text.StringBuilder text = new();
        await foreach (SegmentData segment in processor.ProcessAsync(samples))
        {
            text.Append(segment.Text);
        }
        return text.ToString().Trim();
    }

    private static bool IsNoise(string text)
    {
        if (text.Length < 2)
        {
            return true;
        }
        string t = text.Trim();
        return (t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')')) || t.Contains('♪');
    }

    // Speakers leak into the microphone: if "You" repeats what "Them" just said, it is the echo, not you.
    private bool IsEcho(string who, string text)
    {
        if (who != "You")
        {
            return false;
        }
        string mine = Normalize(text);
        lock (recentThem)
        {
            return recentThem.Any(r =>
            {
                string theirs = Normalize(r.Text);
                return theirs.Length > 0 && mine.Length > 0 && (theirs.Contains(mine) || mine.Contains(theirs));
            });
        }
    }

    private static string Normalize(string text) =>
        new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
}

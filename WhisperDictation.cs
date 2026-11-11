using System.IO;
using NAudio.Wave;
using Whisper.net;

namespace SapphireNotch.Windows;

/// <summary>
/// Offline dictation: records the microphone while the trigger is held, then transcribes it with
/// Whisper on this PC. Nothing is sent anywhere.
/// </summary>
internal sealed class WhisperDictation
{
    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh", "models", "ggml-base.en.bin");

    private WhisperFactory? factory;
    private WaveInEvent? mic;
    private MemoryStream? pcm;
    private WaveFileWriter? writer;

    public bool Recording { get; private set; }

    public static bool ModelInstalled => File.Exists(ModelPath);

    /// <summary>Loads the model in the background so the first dictation is not slow.</summary>
    public void Warm()
    {
        _ = VoiceStudioClient.ProbeAsync();
        if (!ModelInstalled)
        {
            return;
        }
        Task.Run(() =>
        {
            try
            {
                factory ??= WhisperFactory.FromPath(ModelPath);
            }
            catch (Exception ex)
            {
                SpeechDictation.Log("whisper warm-up failed: " + ex.Message);
            }
        });
    }

    public void Start()
    {
        if (Recording)
        {
            return;
        }
        pcm = new MemoryStream();
        WaveFormat format = new(16000, 16, 1);
        writer = new WaveFileWriter(new IgnoreDisposeStream(pcm), format);
        mic = new WaveInEvent { WaveFormat = format, BufferMilliseconds = 50 };
        mic.DataAvailable += (_, e) => writer?.Write(e.Buffer, 0, e.BytesRecorded);
        mic.StartRecording();
        Recording = true;
    }

    /// <summary>Stops recording and returns the transcript (empty when nothing was said).</summary>
    public async Task<string> StopAsync()
    {
        if (!Recording || mic == null || writer == null || pcm == null)
        {
            return "";
        }
        Recording = false;
        mic.StopRecording();
        mic.Dispose();
        mic = null;
        writer.Dispose();
        writer = null;

        MemoryStream audio = pcm;
        pcm = null;
        if (audio.Length < 16000 * 2 / 3) // under about 0.3 s of sound
        {
            return "";
        }
        audio.Position = 0;

        if (await VoiceStudioClient.ProbeAsync())
        {
            string? remote = await VoiceStudioClient.TranscribeAsync(audio);
            if (remote != null)
            {
                SpeechDictation.Log("transcribed by VoiceStudio");
                return remote;
            }
            audio.Position = 0;
        }
        if (!ModelInstalled)
        {
            throw new InvalidOperationException("the Whisper model file is missing");
        }

        return await Task.Run(async () =>
        {
            factory ??= WhisperFactory.FromPath(ModelPath);
            // Whisper normally always works on a 30 s window; shrinking it to the clip's length is what makes short dictations fast
            double seconds = (audio.Length - 44) / 32000.0;
            int context = (int)Math.Min(1500, Math.Max(384, seconds * 50 + 150));
            WhisperProcessorBuilder builder = factory.CreateBuilder().WithLanguage("en").WithThreads(4).WithNoContext();
            if (seconds < 25)
            {
                builder = builder.WithAudioContextSize(context);
            }
            await using WhisperProcessor processor = builder.Build();
            System.Text.StringBuilder text = new();
            await foreach (SegmentData segment in processor.ProcessAsync(audio))
            {
                text.Append(segment.Text);
            }
            return text.ToString().Trim();
        });
    }

    /// <summary>Lets the WAV writer finish without closing the memory stream we still need to read.</summary>
    private sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
        }
    }
}

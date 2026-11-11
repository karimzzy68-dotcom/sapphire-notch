using System.IO;
using System.Windows.Threading;
using Windows.Media.SpeechRecognition;

namespace SapphireNotch.Windows;

/// <summary>
/// Windows' own dictation recognizer (the one behind Win + H). It needs
/// Settings > Privacy &amp; security > Speech > Online speech recognition to be on.
/// All events are raised on the UI thread.
/// </summary>
internal sealed class SpeechDictation
{
    private const int PrivacyNotAccepted = unchecked((int)0x80045509);

    private readonly Dispatcher dispatcher;
    private SpeechRecognizer? recognizer;

    public SpeechDictation(Dispatcher dispatcher) => this.dispatcher = dispatcher;

    public bool Running { get; private set; }

    /// <summary>A finished phrase, already punctuated by the recognizer.</summary>
    public event Action<string>? Phrase;

    /// <summary>The words heard so far in the phrase being spoken.</summary>
    public event Action<string>? Partial;

    /// <summary>Raised when listening ends, with a message when it ended because of a problem.</summary>
    public event Action<string?, bool>? Stopped; // message, privacySettingNeeded

    public async Task StartAsync()
    {
        if (Running)
        {
            return;
        }
        try
        {
            recognizer = new SpeechRecognizer();
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            Log("compiling constraints");
            Task<SpeechRecognitionCompilationResult> compiling = recognizer.CompileConstraintsAsync().AsTask();
            if (await Task.WhenAny(compiling, Task.Delay(10000)) != compiling)
            {
                Release();
                Raise(null, "Speech recognition did not respond (compile timed out). Check Settings > Time & language > Speech.", false);
                return;
            }
            SpeechRecognitionCompilationResult compiled = await compiling;
            Log("compiled: " + compiled.Status);
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                Release();
                Raise(null, "Speech recognition could not start (" + compiled.Status + ").", false);
                return;
            }

            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromSeconds(60);
            recognizer.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
            {
                SpeechRecognitionResult result = args.Result;
                Log("result: status=" + result.Status + " confidence=" + result.Confidence + " length=" + result.Text.Length);
                if (result.Status == SpeechRecognitionResultStatus.Success
                    && result.Confidence != SpeechRecognitionConfidence.Rejected
                    && !string.IsNullOrWhiteSpace(result.Text))
                {
                    string text = result.Text.Trim();
                    dispatcher.BeginInvoke(() => Phrase?.Invoke(text));
                }
            };
            recognizer.HypothesisGenerated += (_, args) =>
            {
                string text = args.Hypothesis.Text;
                Log("hypothesis length=" + text.Length);
                dispatcher.BeginInvoke(() => Partial?.Invoke(text));
            };
            recognizer.ContinuousRecognitionSession.Completed += (_, args) =>
            {
                bool failed = args.Status != SpeechRecognitionResultStatus.Success && args.Status != SpeechRecognitionResultStatus.TimeoutExceeded;
                string? message = args.Status switch
                {
                    SpeechRecognitionResultStatus.TopicLanguageNotSupported => "Windows has no speech language for dictation.",
                    SpeechRecognitionResultStatus.AudioQualityFailure => "I could not hear you clearly. Check the microphone.",
                    SpeechRecognitionResultStatus.NetworkFailure => "Online speech recognition needs an internet connection.",
                    _ => failed ? "Listening stopped (" + args.Status + ")." : null
                };
                Log("session completed: " + args.Status);
                dispatcher.BeginInvoke(() => Finish(message, false));
            };

            recognizer.StateChanged += (_, args) => Log("state: " + args.State);
            Running = true;
            Task starting = recognizer.ContinuousRecognitionSession.StartAsync().AsTask();
            if (await Task.WhenAny(starting, Task.Delay(10000)) != starting)
            {
                Log("StartAsync timed out");
                Running = false;
                Release();
                Raise(null, "Speech recognition did not start (timed out).", false);
                return;
            }
            await starting;
            Log("listening");
        }
        catch (Exception ex)
        {
            Release();
            Running = false;
            Log("start failed 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
            if (ex.HResult == PrivacyNotAccepted)
            {
                Raise(null, "Windows online speech recognition is off. Turn it on, then try again.", true);
            }
            else if (ex is UnauthorizedAccessException)
            {
                Raise(null, "Windows blocked the microphone. Allow microphone access for desktop apps.", false);
            }
            else
            {
                Raise(null, "Voice could not start: " + ex.Message, false);
            }
        }
    }

    public async Task StopAsync()
    {
        if (!Running || recognizer == null)
        {
            return;
        }
        try
        {
            await recognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch (Exception)
        {
            // already stopped
        }
        Finish(null, false);
    }

    private void Finish(string? message, bool privacy)
    {
        if (!Running)
        {
            return;
        }
        Running = false;
        Release();
        Stopped?.Invoke(message, privacy);
    }

    private void Raise(string? _, string message, bool privacy)
    {
        Log(message);
        dispatcher.BeginInvoke(() => Stopped?.Invoke(message, privacy));
    }

    internal static void Log(string line)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"[{DateTime.Now:s}] voice: {line}\r\n");
        }
        catch (IOException)
        {
        }
    }

    private void Release()
    {
        try
        {
            recognizer?.Dispose();
        }
        catch (Exception)
        {
        }
        recognizer = null;
    }
}

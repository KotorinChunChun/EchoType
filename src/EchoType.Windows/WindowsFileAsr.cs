using EchoType.Core;
using System.Diagnostics;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace EchoType.Windows;

public static class WindowsFileAsr
{
    public static async Task<(string Text, float? Confidence, double Seconds)> RecognizeAsync(byte[] wave, CancellationToken ct, bool prepared = false)
    {
        using var input = prepared ? null : RecognitionAudio.Prepare(wave);
        wave = input?.Wave ?? wave;
        var recognizer = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "ja")
            ?? throw new AsrModelException("日本語のWindows音声認識が未導入です。");
        using var engine = new SpeechRecognitionEngine(recognizer);
        engine.InitialSilenceTimeout = TimeSpan.FromSeconds(65);
        engine.BabbleTimeout = TimeSpan.Zero;
        engine.LoadGrammar(WindowsSpeechGrammar.Create(recognizer.Culture));
        using var stream = new MemoryStream(wave, false);
        engine.SetInputToWaveStream(stream);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var texts = new List<string>(); var scores = new List<float>();
        engine.SpeechRecognized += (_, e) => { texts.Add(e.Result.Text); scores.Add(WindowsSpeechGrammar.Confidence(e.Result)); };
        engine.RecognizeCompleted += (_, e) => { if (e.Error is not null) completion.TrySetException(e.Error); else completion.TrySetResult(); };
        var watch = Stopwatch.StartNew(); engine.RecognizeAsync(RecognizeMode.Multiple);
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), ct); }
        catch (TimeoutException) { throw new InvalidOperationException("Windows認識が時間切れになりました。"); }
        finally { engine.RecognizeAsyncCancel(); }
        return (string.Join("", texts), scores.Count == 0 ? null : scores.Min(), watch.Elapsed.TotalSeconds);
    }
}

using EchoType.Core;
using System.Diagnostics;
using System.Speech.Recognition;

namespace EchoType.Windows;

internal static class WindowsSpeechRecognizer
{
    internal static SpeechRecognitionEngine CreateEngine()
    {
        var recognizer = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "ja")
            ?? throw new InvalidOperationException("Windows標準の日本語音声認識が見つかりません。Windowsの日本語音声認識の設定を確認してください。");
        var engine = new SpeechRecognitionEngine(recognizer);
        try
        {
            // 日本語SAPIの別候補取得を避けるため、通常の文法に自由発話を埋め込む。
            var builder = new GrammarBuilder { Culture = recognizer.Culture };
            builder.AppendDictation();
            engine.LoadGrammar(new Grammar(builder));
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(65);
            engine.BabbleTimeout = TimeSpan.Zero;
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }

    internal static async Task<AsrResult> RecognizeAsync(byte[] wave, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        using var engine = CreateEngine();
        using var stream = new MemoryStream(wave, writable: false);
        engine.SetInputToWaveStream(stream);
        var phrases = new List<string>();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SpeechRecognized += (_, e) => phrases.Add(e.Result.Text);
        engine.RecognizeCompleted += (_, e) =>
        {
            if (e.Error is not null) completion.TrySetException(e.Error);
            else completion.TrySetResult(string.Concat(phrases));
        };
        engine.RecognizeAsync(RecognizeMode.Multiple);
        try
        {
            var text = await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellation).ConfigureAwait(false);
            return new(AsrEngines.ExternalTranscription, text, watch.Elapsed.TotalSeconds);
        }
        catch (TimeoutException)
        {
            return new(AsrEngines.ExternalTranscription, "", watch.Elapsed.TotalSeconds, "Windows標準の音声認識が時間切れになりました。");
        }
        finally { if (!completion.Task.IsCompleted) engine.RecognizeAsyncCancel(); }
    }
}

using System.Diagnostics;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private CancellationTokenSource? _warmupCancellation;
    public void StartWhisperWarmup()
    {
        if (_stopping || _paused || _noAudio || _recognize is not null || _warmupCancellation is not null || !Settings.Enabled) return;
        var settings = Effective(Settings);
        var engines = settings.WhisperWarmupModels().Where(_asrs[0].LocalReady)
            .Take(settings.Recognition.Performance.MaxResidentModels).ToArray();
        if (engines.Length == 0) return;
        var cancellation = new CancellationTokenSource();
        _warmupCancellation = cancellation;
        var token = cancellation.Token;
        Track(PrepareAsync());
        async Task PrepareAsync()
        {
            // モデル読込みは画面・録音開始を塞がず、一度だけ実行する。
            var watch = Stopwatch.StartNew();
            var completed = 0; var failures = new List<string>();
            try
            {
                await Task.Run(async () =>
                {
                    foreach (var engine in engines)
                    {
                        token.ThrowIfCancellationRequested();
                        try { await _asrs[0].WarmupAsync(engine, settings.Recognition, token); completed++; }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { failures.Add(engine + ": " + ex.Message); }
                    }
                }, token);
                if (!_stopping) Status?.Invoke(failures.Count == 0
                    ? $"音声入力: Whisper {completed}モデルを準備しました（{watch.Elapsed.TotalSeconds:F1}秒）。"
                    : "音声入力の事前読み込み: " + string.Join(" / ", failures));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                _warmupCancellation = null;
                cancellation.Dispose();
            }
        }
    }
}

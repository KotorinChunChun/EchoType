using EchoType.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace EchoType.Windows;

/// <summary>録音比較とライブ確定で共通の実装。操作権限を持たない。</summary>
public sealed class AsrService(AgentPaths paths, bool voiceInputBudget = true) : IDisposable
{
    private readonly PlaybackDenoiser _denoiser = new(paths);

    private readonly SharedRecognitionRuntime _runtime = SharedRecognitionRuntime.Acquire(paths);
    private int _disposed;

    public bool LocalReady(string engine) => LocalAsrWorker.IsReady(paths, engine);
    public SharedRecognitionRuntime Runtime => _runtime;
    public AppSettings EffectiveSettings(AppSettings settings) => _runtime.EffectiveSettings(settings, voiceInputBudget);
    public Task<AsrWarmupResult> WarmupAsync(string engine, AppSettings settings, CancellationToken ct, bool preferred = false) =>
        WarmupEffectiveAsync(engine, EffectiveSettings(settings), ct, preferred);
    private async Task<AsrWarmupResult> WarmupEffectiveAsync(string engine, AppSettings settings, CancellationToken ct, bool preferred)
    {
        try { return await _runtime.WarmupAsync(engine, settings.LocalAsrDevice, settings.Performance, ct, preferred); }
        catch (Exception ex) { EngineSettingsJournal.Failure(paths, "warmup", engine, settings, ex); throw; }
    }
    public Task<AsrResult> RecognizeCheckedAsync(string engine, SpeechSegment segment, AppSettings settings,
        CancellationToken ct, RecognitionPurpose purpose = RecognitionPurpose.Full)
    {
        if (engine == AsrEngines.WindowsCaptions || settings.ImmediateAsrEngine == AsrEngines.WindowsCaptions || AsrEngines.BaseId(engine) == settings.ImmediateAsrEngine) return RecognizeAsync(engine, segment, settings, ct, purpose: purpose);
        return AsrLengthGuard.RunAsync(
            () => RecognizeAsync(engine, segment, settings, ct, purpose: purpose),
            () => RecognizeAsync(settings.ImmediateAsrEngine, segment, settings, ct, purpose: purpose), settings.AsrLengthCheckEnabled);
    }
    public async Task<AsrResult> RecognizeAsync(string engine, SpeechSegment segment, AppSettings settings, CancellationToken ct, RecognitionPurpose purpose = RecognitionPurpose.Full)
    {
        settings = EffectiveSettings(settings);
        var watch = Stopwatch.StartNew();
        byte[]? denoised = null;
        var denoiseNotice = "";
        try
        {
            ct.ThrowIfCancellationRequested();
            var excluded = FilterInput(segment, new AsrResult(engine, "", 0));
            if (excluded.CensoredText.Length > 0) return excluded;
            // 雑音抑制Pythonの起動より先に無音を除外する。
            if (RecognitionAudio.SilentWave(segment.Wave)) return new(engine, "", watch.Elapsed.TotalSeconds)
                { RecognitionSeconds = 0, AudioHash = Convert.ToHexString(SHA256.HashData(segment.Wave)), InputVerified = engine != AsrEngines.WindowsCaptions };
            if (engine == AsrEngines.WindowsCaptions && !segment.CaptionReplay)
                return InputCensorship.Filter(await ObserveDirectAsync(engine, purpose, "OS管理", ct, () => WindowsCaptionComparison.RecognizeAsync(segment, settings, ct)));
            if (PlaybackDenoiser.Applies(settings, segment))
            {
                if (PlaybackDenoiser.Ready(paths)) denoised = await _denoiser.PrepareAsync(segment.Wave, ct);
                else denoiseNotice = "雑音抑制未導入・原音で認識";
            }
            using var prepared = RecognitionAudio.Prepare(denoised ?? segment.Wave, settings, segment.SilenceProcessed);
            var preparation = watch.Elapsed.TotalSeconds;
            var result = await RecognizeCoreAsync(engine, segment with { Wave = prepared.Wave }, settings, ct, purpose);
            return FilterInput(segment, result) with { AudioHash = Convert.ToHexString(SHA256.HashData(segment.Wave)), ProcessedAudioHash = Convert.ToHexString(SHA256.HashData(prepared.Wave)),
                RecognitionAudioSeconds = prepared.Seconds,
                AudioPreprocessing = string.Join("・", new[] { denoised is null ? denoiseNotice : "GTCRN雑音抑制", prepared.Changed ? "無音短縮" : "" }.Where(x => x.Length > 0)),
                AudioPreprocessingSeconds = preparation, ProcessingSeconds = watch.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or FormatException or ArgumentException or OperationCanceledException or TimeoutException or System.Runtime.InteropServices.COMException)
        { EngineSettingsJournal.Failure(paths, "preprocessing", engine, settings, ex); return new(engine, "", watch.Elapsed.TotalSeconds, "認識前の音声処理に失敗: " + ex.Message) { InputVerified = engine != AsrEngines.WindowsCaptions }; }
        finally { if (denoised is not null) Array.Clear(denoised); }
    }
    private async Task<AsrResult> RecognizeCoreAsync(string engine, SpeechSegment segment, AppSettings settings, CancellationToken ct, RecognitionPurpose purpose = RecognitionPurpose.Full)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var definition = AsrEngines.Get(engine);
            if (definition.Cloud || AsrEngines.HasPostProcessing(engine))
                throw new InvalidOperationException("音声入力ではローカル認識のみ使用できます。");
            ct.ThrowIfCancellationRequested();
            if (engine == AsrEngines.WindowsCaptions)
                return InputCensorship.Filter(await ObserveDirectAsync(engine, purpose, "OS管理", ct, () => WindowsCaptionComparison.RecognizeAsync(segment, settings, ct)));
            if (!SpeechActivityDetector.Shared.Analyze(segment.Wave).HasSpeech)
                return new(engine, "", watch.Elapsed.TotalSeconds) { AudioHash = Convert.ToHexString(SHA256.HashData(segment.Wave)), RecognitionSeconds = 0 };
            if (engine == WindowsAiSpeech.Engine)
            {
                return AsrTextGuard.Filter(await _runtime.RecognizeAsync(engine, segment.Wave, "ja-JP", "windows-ai", settings.Performance, purpose,
                    purpose == RecognitionPurpose.Comparison && !settings.Performance.ComparisonReuse, ct,
                    (wave, token) => WindowsAiSpeech.RecognizeAsync(paths, wave, token)));
            }
            if (engine == AsrEngines.WindowsCaptionsDll)
                return AsrTextGuard.Filter(await _runtime.RecognizeAsync(engine, segment.Wave, "ja-JP", "caption-dll", settings.Performance, purpose,
                    purpose == RecognitionPurpose.Comparison && !settings.Performance.ComparisonReuse, ct,
                    (wave, token) => WindowsCaptionDll.RecognizeAsync(paths, wave, token, new(segment.Id, purpose, segment.AudioSource.Kind))));
            if (engine == "windows")
            {
                return await _runtime.RecognizeAsync(engine, segment.Wave, AsrEngines.VocabularyHint(settings), "windows", settings.Performance, purpose,
                    purpose == RecognitionPurpose.Comparison && !settings.Performance.ComparisonReuse, ct, async (wave, token) =>
                    {
                        var result = await WindowsFileAsr.RecognizeAsync(wave, token, prepared: true);
                        return AsrTextGuard.Filter(new(engine, result.Text, result.Seconds, Confidence: result.Confidence) { RecognitionSeconds = result.Seconds });
                    });
            }
            var output = await _runtime.RecognizeAsync(engine, segment.Wave, AsrEngines.VocabularyHint(settings), settings.LocalAsrDevice,
                settings.Performance, purpose, purpose == RecognitionPurpose.Comparison && !settings.Performance.ComparisonReuse, ct);
            return AsrTextGuard.Filter(output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or FormatException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            EngineSettingsJournal.Failure(paths, "recognition", engine, settings, ex);
            return new(engine, "", watch.Elapsed.TotalSeconds, ex is OperationCanceledException ? "認識が時間切れになりました。" : ex.Message)
                { InputVerified = engine != AsrEngines.WindowsCaptions, ModelError = ex is AsrModelException || ex is HttpRequestException && AsrEngines.Get(engine).Cloud };
        }
    }
    private async Task<AsrResult> ObserveDirectAsync(string engine, RecognitionPurpose purpose, string device, CancellationToken ct, Func<Task<AsrResult>> run)
    {
        using var activity = _runtime.Activities.Begin("音声認識", engine, ModelActivity.RecognitionPurposeLabel(purpose), device, ModelPhase.Processing);
        try { var result = await run().ConfigureAwait(false); activity.Finish(result.Succeeded ? ModelPhase.Completed : ModelPhase.Failed); return result; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { activity.Finish(ModelPhase.Cancelled); throw; }
        catch { activity.Finish(ModelPhase.Failed); throw; }
    }
    private static AsrResult FilterInput(SpeechSegment segment, AsrResult result) => InputCensorship.Filter(segment.Id.ToString("N"), segment.SessionId, segment.AudioSource.Kind, result);
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _denoiser.Dispose(); _runtime.Dispose(); }
}

using EchoType.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace EchoType.App;

public sealed record VoiceInputTiming(DateTimeOffset At, string Stage, string Model, double Waiting, double Processing, string Result)
{
    public string Time => At.ToLocalTime().ToString("HH:mm:ss");
    public AiTokenUsage? Usage { get; init; }
    public string InputTokens => Usage?.InputTokens.ToString("N0") ?? "未取得";
    public string OutputTokens => Usage?.OutputTokens.ToString("N0") ?? "未取得";
}

public sealed partial class VoiceInputController
{
    public ObservableCollection<VoiceInputTiming> Timings { get; } = [];
    private VoiceInputTarget? _undoTarget;
    private bool _reediting;
    private bool ReplaceRequested => Settings.ReplaceTargetText || _reediting;
    private void RecordTiming(string stage, string model, double waiting, double processing, string result)
    {
        Timings.Insert(0, new(DateTimeOffset.Now, stage, model, waiting, processing, result));
        while (Timings.Count > 200) Timings.RemoveAt(Timings.Count - 1);
    }
    private async Task<string> FormatMeasuredAsync(string text, VoiceInputSettings settings, CancellationToken ct)
    {
        ModelExchange? exchange = null;
        using var capture = _formatCodex?.CaptureExchanges(value => { if (value.State != "送信中") exchange = value; });
        var watch = Stopwatch.StartNew(); string outcome = "完了";
        try { return await _format(text, settings, ct); }
        catch (OperationCanceledException) { outcome = "中止"; throw; }
        catch (Exception ex) { outcome = ex.Message; throw; }
        finally { Timings.Insert(0, new(DateTimeOffset.Now, "整形", settings.FormattingModel, 0, watch.Elapsed.TotalSeconds, outcome) { Usage = exchange?.Usage }); while (Timings.Count > 200) Timings.RemoveAt(Timings.Count - 1); }
    }
    private async Task<string> InsertMeasuredAsync(VoiceInputTarget target, string text, bool replace, CancellationToken ct, string reason = "manual-confirm")
    {
        var watch = Stopwatch.StartNew(); string outcome = "完了";
        ConfigureFocusLog(target, reason);
        try
        {
            var mainKey = Settings.GetRecordingShortcut().MainKey;
            outcome = await Task.Run(() => target.InsertAsync(text, ct, replace, mainKey));
            if (target.WriteVerified) _undoTarget = target;
            return outcome;
        }
        catch (OperationCanceledException) { outcome = "中止"; throw; }
        catch (Exception ex) { outcome = ex.Message; throw; }
        finally { RecordTiming("入力", target.ApplicationName, 0, watch.Elapsed.TotalSeconds, outcome); }
    }
    internal void RecordEditorFocus(nint window) => VoiceInputFocusJournal.Write(_paths, Guid.NewGuid().ToString("N"), "activate-editor", "show-or-current-input", window);
    private void ConfigureFocusLog(VoiceInputTarget target, string reason)
    {
        var operation = Guid.NewGuid().ToString("N");
        target.SetFocusLog((stage, window) => VoiceInputFocusJournal.Write(_paths, operation, stage, reason, window));
    }
    public async Task UndoLastInputAsync(bool edit)
    {
        if (ConfigurationLocked || _stopping) { Say("処理完了後に戻してください。"); return; }
        if (_undoTarget is not { } previous) { Say("戻せる入力がありません。"); return; }
        if (_loadingTarget || (!string.IsNullOrEmpty(_window?.DraftText) && (_loadedTarget is null || _window.DraftText != _loadedTarget.OriginalText)) || (_session?.Entries.Count ?? 0) > 0) { Say("現在の候補と編集を完了してから戻してください。"); return; }
        SetBusy(true);
        try
        {
            ConfigureFocusLog(previous, "explicit-undo");
            var mainKey = Settings.GetRecordingShortcut().MainKey;
            var target = await Task.Run(() => previous.UndoAsync(mainKey: mainKey)); _undoTarget = null;
            ++_targetLoadVersion; ++_targetCaptureVersion;
            _target = target; _lastExternal = target;
            if (edit)
            {
                _reediting = true; _loadedTarget = target; Open();
                _window!.ShowReedit(previous.VerifiedText!); _window.ShowTarget(target);
                Say("入力を戻しました。本文を編集して決定してください。");
            }
            else { _reediting = false; _loadedTarget = null; if (_window is not null) { _window.DraftText = ""; _window.ResetFormattingDiff(); _window.ShowTarget(target); } Say("直前の入力を戻しました。"); }
        }
        catch (Exception ex) { Say("戻せません: " + ex.Message); }
        finally { SetBusy(false); }
    }
    public async Task RetryRecognitionAsync(VoiceInputLogEntry entry)
    {
        var session = _session;
        if (ConfigurationLocked || session is null || !session.Entries.Contains(entry)) return;
        // 元の失敗行を選び直しても、同じ録音の最新の試行を使い、採用済みの音声を繰り返さない。
        entry = session.Entries.Last(item => item.RecordingNumber == entry.RecordingNumber);
        if (!entry.Finished || entry.Metadata is null || entry.RecognitionSettings is null) return;
        if (entry.RecognitionSettings.FinalSpeech() != Settings.FinalSpeech())
        {
            Say("認識先が変わりました。現在の認識先で使う場合は録音し直してください。");
            return;
        }
        if (entry.Results.FirstOrDefault() is not { Succeeded: false }) { Say("失敗した認識はありません。"); return; }
        try
        {
            var wave = session.Cache.Read(entry.CachePath);
            var retry = new VoiceInputLogEntry { Number = ++session.RecordingSequence, RetrySourceNumber = entry.RecordingNumber,
                LiveEngine = "", LiveFinished = true, Status = $"#{entry.Number} を再試行", Seconds = entry.Seconds,
                Metadata = entry.Metadata, RecognitionSettings = entry.RecognitionSettings };
            try { retry.CachePath = session.Cache.Save(retry.Number, wave); } finally { Array.Clear(wave); }
            session.Entries.Add(retry); RefreshState();
            if (Settings.AutomaticInput)
            {
                _automaticSession = session; _automaticFaulted = false;
                var retryIndex = _automaticEntries.FindIndex(item => ReferenceEquals(item.Entry, entry));
                _automaticEntries.RemoveAll(item => ReferenceEquals(item.Entry, entry));
                entry.DraftHandled = true;
                _automaticEntries.Insert(retryIndex < 0 ? _automaticEntries.Count : retryIndex, (retry, AsrEngines.ExternalTranscription));
            }
            await RecognizeQueuedAsync(session, retry, retry.Metadata, retry.RecognitionSettings);
        }
        catch (Exception ex) { Say("再試行できません: " + ex.Message); }
    }
}

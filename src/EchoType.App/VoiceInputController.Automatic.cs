using EchoType.Core;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private readonly List<(VoiceInputLogEntry Entry, string Engine)> _automaticEntries = [];
    private VoiceInputSession? _automaticSession;
    private CancellationTokenSource? _automaticCancellation;
    private bool _automaticProcessing, _automaticSending, _automaticFaulted, _automaticTargetPending, _automaticDraftRequested;
    public bool TemporarilyPinned { get; private set; }
    private bool AutomaticConfirmEnabled => Settings.AutomaticConfirm && !TemporarilyPinned;

    public void PinForCurrentDisplay()
    {
        if (_window?.IsVisible != true || TemporarilyPinned) return;
        TemporarilyPinned = true;
        if (_automaticSending) _automaticCancellation?.Cancel();
        _window.ShowAutomaticSettings(Settings);
    }

    public void SetAutomaticInput(bool enabled)
    {
        var settings = Settings with { AutomaticInput = enabled };
        PersistSettings(settings);
        RestartAutomaticInput(enabled ? "自動採用をオンにしました。" : "自動採用をオフにしました。");
    }
    public void SetAutomaticOptions(bool formatting, bool confirm)
    {
        var settings = Settings with { AutomaticFormatting = formatting, AutomaticConfirm = confirm };
        PersistSettings(settings);
        RestartAutomaticInput("自動処理の設定を保存しました。");
    }
    private void RestartAutomaticInput(string message)
    {
        _window?.ShowAutomaticSettings(Settings);
        ResetAutomaticInput();
        Say(message);
        if (Settings.AutomaticInput && _window?.IsVisible == true && _session is { } session)
        {
            _automaticSession = session;
            _automaticDraftRequested = !string.IsNullOrWhiteSpace(_window.DraftText);
            foreach (var entry in session.Entries.Where(entry => !entry.DraftHandled))
                _automaticEntries.Add((entry, AsrEngines.ExternalTranscription));
        }
        RefreshState();
        if (_session is { } current) ApplyFinalResults(current);
    }
    private void ResetAutomaticInput()
    {
        _automaticCancellation?.Cancel();
        _automaticEntries.Clear(); _automaticSession = null; _automaticFaulted = false; _automaticDraftRequested = false;
    }
    private void ContinueAutomaticDraft()
    {
        if (!Settings.AutomaticInput || !AutomaticConfirmEnabled) return;
        ResetAutomaticInput();
        _automaticSession = EnsureSession(); _automaticDraftRequested = true;
        RefreshState(); TryAutomaticInput();
    }
    private void RegisterAutomaticRecording(Recording recording)
    {
        if (!recording.Settings.AutomaticInput) return;
        if (!ReferenceEquals(_automaticSession, recording.Session))
        {
            _automaticEntries.Clear(); _automaticSession = recording.Session;
        }
        _automaticFaulted = false;
        _automaticEntries.Add((recording.Entry, AsrEngines.ExternalTranscription));
    }
    private void TryAutomaticInput()
    {
        if (!Settings.AutomaticInput || _automaticSending || _automaticFaulted || _automaticTargetPending ||
            _loadingTarget || _busy || _window?.IsVisible != true ||
            _automaticSession is not { } session || !Current(session) || _automaticEntries.Count == 0 && !_automaticDraftRequested) return;

        // 録音順の先頭から準備できた分を先に本文へ置く。整形の完了を表示条件にしない。
        var ready = _automaticEntries.TakeWhile(item => item.Entry.Results.Any(r => r.EngineId == item.Engine && r.Succeeded)).ToArray();
        if (ready.Length > 0 && _capture is null)
        {
            var candidate = string.Concat(ready.Select(item => item.Entry.Results.First(r => r.EngineId == item.Engine).Text.Trim()));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (!_window.InsertDraft(VoiceInputSettings.AppendSentence(candidate), focus: false)) { _automaticFaulted = true; RefreshState(); return; }
                _automaticDraftRequested = true;
                _window.ResetFormattingDiff();
                _automaticCancellation?.Cancel();
            }
            foreach (var item in ready) { _automaticEntries.Remove(item); item.Entry.DraftHandled = true; }
        }
        if (_automaticEntries.FirstOrDefault().Entry is { } first && first.Results.Any(r => r.EngineId == _automaticEntries[0].Engine && !r.Succeeded))
        {
            _automaticFaulted = true; RefreshState(); Say("自動採用モデルの文字起こしに失敗しました。録音キューから再試行できます。"); return;
        }
        if (_capture is not null || _automaticProcessing || _automaticEntries.Count > 0) { RefreshState(); return; }
        if (!_automaticDraftRequested || string.IsNullOrWhiteSpace(_window.DraftText)) { ResetAutomaticInput(); RefreshState(); MaybeCloseEmpty(session); return; }
        var target = _target;
        if (AutomaticConfirmEnabled && (target is null || target.Consumed || ReplaceRequested && !ReplacementReady))
        {
            _automaticFaulted = true; RefreshState(); Say("入力先を取得できないため自動入力を停止しました。送信予定文字列は保持しています。"); return;
        }
        var before = _window.DraftText;
        if (before.Length > 16000) { _automaticFaulted = true; RefreshState(); Say("入力予定の本文が16000文字を超えています。"); return; }
        var alreadyFormatted = _window.HasFormattingDiff;
        var settings = Effective(Settings) with
        {
            FormattingModel = _window.SelectedFormattingModel, FormattingEffort = _window.SelectedFormattingEffort, ReplaceTargetText = ReplaceRequested,
            AutomaticConfirm = AutomaticConfirmEnabled
        };
        _automaticProcessing = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
        _automaticCancellation = cancellation; RefreshState();
        Track(RunAsync());

        async Task RunAsync()
        {
            try
            {
                var needsFormatting = settings.AutomaticFormatting && !alreadyFormatted;
                Say(needsFormatting ? "文字起こしを採用し、プロンプト用に整形中…" : "文字起こしを採用しました。");
                var formatted = needsFormatting ? await FormatMeasuredAsync(before, settings, cancellation.Token) : before;
                cancellation.Token.ThrowIfCancellationRequested();
                if (!Current(session) || !Settings.AutomaticInput || _capture is not null || _automaticEntries.Count > 0 ||
                    settings.AutomaticConfirm && AutomaticConfirmEnabled && !ReferenceEquals(_target, target)) return;
                if (string.IsNullOrWhiteSpace(formatted) || formatted.Length > 16000) throw new InvalidOperationException("整形結果が空、または16000文字を超えています。");
                if (needsFormatting) _window.ShowFormattingDiff(before, formatted);
                else if (!alreadyFormatted) _window.ResetFormattingDiff();
                var confirm = settings.AutomaticConfirm && AutomaticConfirmEnabled;
                if (confirm)
                {
                    Say("本文を入力中…");
                    _automaticSending = true;
                    var result = await InsertMeasuredAsync(target!, formatted, settings.ReplaceTargetText, _window.DraftCaretIndex, cancellation.Token, "automatic-confirm");
                    _automaticSending = false;
                    if (!Current(session)) return;
                    Say(result);
                    if (!target!.WriteVerified) { _automaticFaulted = true; return; }
                }
                if (!Current(session)) return;
                _automaticDraftRequested = false;
                if (!confirm)
                {
                    Say("決定で入力できます。");
                    return;
                }
                _window.DraftText = "";
                if (_capture is not null || _automaticEntries.Count > 0)
                {
                    // 入力確定直後の追加録音も保持し、確定後の本文・位置を次の入力先にする。
                    var next = await Task.Run(target!.RefreshSnapshot);
                    _target = next; _loadedTarget = settings.ReplaceTargetText ? next : null;
                    if (settings.ReplaceTargetText) _window.LoadTargetText(next);
                    _window.ShowTarget(next); return;
                }
                _target = null; _loadedTarget = null; _window.ShowTarget(null);
                Dismiss();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (Current(session))
                {
                    _automaticFaulted = true;
                    Say(target?.Consumed == true ? "入力結果が不明です。自動再送はしません。入力先を確認してください。" : "自動入力を停止しました: " + ex.Message);
                }
            }
            finally
            {
                if (target?.Consumed == true && ReferenceEquals(_target, target)) { _target = null; _loadedTarget = null; _window.ShowTarget(null); }
                cancellation.Dispose();
                if (ReferenceEquals(_automaticCancellation, cancellation)) _automaticCancellation = null;
                _automaticProcessing = false; _automaticSending = false; RefreshState();
                if (settings.AutomaticConfirm && Current(session) && _window.IsVisible && (target?.CanReturnFocus ?? false)) _window.FocusDraft();
                TryAutomaticInput();
            }
        }
    }
}

using System.Collections.ObjectModel;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private readonly ObservableCollection<VoiceInputUnsentDraft> _unsentDrafts = [];
    private ReadOnlyObservableCollection<VoiceInputUnsentDraft>? _unsentDraftView;
    public ReadOnlyObservableCollection<VoiceInputUnsentDraft> UnsentDraftHistory => _unsentDraftView ??= new(_unsentDrafts);

    private void RememberUnsentDraft()
    {
        // 未送信本文はこのプロセス内だけに保持し、設定やログへ書き出さない。
        if (_stopping) { _unsentDrafts.Clear(); return; }
        var text = _window?.DraftText;
        if (string.IsNullOrWhiteSpace(text)) return;
        var previous = _unsentDrafts.FirstOrDefault(entry => entry.Text == text);
        if (previous is not null) _unsentDrafts.Remove(previous);
        _unsentDrafts.Insert(0, new(DateTimeOffset.Now, text));
    }

    public bool RestoreUnsentDraft(VoiceInputUnsentDraft entry)
    {
        if (_stopping || _busy || _capture is not null || _loadingTarget || _automaticProcessing ||
            _window?.IsVisible != true || !_unsentDrafts.Contains(entry)) return false;
        if (!_window.AppendUnsentDraft(entry.Text)) return false;
        Say("未送信テキストを末尾へ追記しました。");
        return true;
    }
}

public sealed record VoiceInputUnsentDraft(DateTimeOffset SavedAt, string Text)
{
    public string Preview
    {
        get
        {
            var summary = Text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (summary.Length > 80) summary = summary[..80] + "…";
            return $"{SavedAt.ToLocalTime():HH:mm:ss}　{summary}";
        }
    }
}

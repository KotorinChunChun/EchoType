using EchoType.Core;

namespace EchoType.Windows;

/// <summary>既存UIA観測の差分だけを表示する。独自のOS設定変更や音声再生は行わない。</summary>
public sealed class VoiceInputCaptionPreview : IVoiceInputLive
{
    private readonly WindowsCaptionComparison.ObservationTimeline _timeline = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private DateTimeOffset _last;
    private bool _disposed, _ready, _uncertain;
    public event Action<string>? Updated;
    public VoiceInputCaptionPreview(WindowsCaptionSnapshot? baseline)
    { if (baseline is not null) Observe(baseline); }
    public void Observe(WindowsCaptionSnapshot snapshot)
    {
        if (_disposed) return;
        _last = snapshot.ObservedAt; _ready = snapshot.Ready;
        if (!snapshot.Ready) _uncertain = true;
        _timeline.Observe(snapshot);
        if (_ready && _last >= _started)
        {
            var result = _timeline.Read(_started, _last);
            if (result.Coverage == CaptionTimelineCoverage.Complete) Updated?.Invoke(result.Text);
        }
    }
    public void Feed(byte[] pcm) => Array.Clear(pcm);
    public Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_uncertain || !_ready || _last < _started)
            return Task.FromResult(new VoiceInputLiveResult("", "Windows字幕の観測が不確定です。全文候補を確認してください。"));
        var result = _timeline.Read(_started, _last);
        return Task.FromResult(_ready && result.Coverage == CaptionTimelineCoverage.Complete
            ? new VoiceInputLiveResult(result.Text) : new VoiceInputLiveResult("", "Windows字幕の観測が不確定です。全文候補を確認してください。"));
    }
    public void Dispose() => _disposed = true;
}

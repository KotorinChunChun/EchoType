namespace EchoType.Windows;

/// <summary>Windowsの速報を即時表示し、解放後に精度優先の結果へ置き換える。</summary>
public sealed class VoiceInputPreview(IVoiceInputLive quick, IVoiceInputLive accurate) : IVoiceInputLive
{
    private bool _disposed;
    public event Action<string>? Updated
    {
        add => quick.Updated += value;
        remove => quick.Updated -= value;
    }
    public Task Stopped => Task.WhenAll(quick.Stopped, accurate.Stopped);
    public void Feed(byte[] pcm)
    {
        if (_disposed) return;
        quick.Feed(pcm); accurate.Feed(pcm);
    }
    public async Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct)
    {
        var fast = quick.CompleteAsync(ct); var precise = accurate.CompleteAsync(ct);
        await Task.WhenAll(fast, precise);
        var result = await precise;
        if (result.Text.Length > 0 && result.Error.Length == 0) return result;
        var fallback = await fast;
        return fallback with { Error = string.Join(" / ", new[] { result.Error, fallback.Error }.Where(e => e.Length > 0)) };
    }
    public void Dispose() { if (_disposed) return; _disposed = true; quick.Dispose(); accurate.Dispose(); }
}

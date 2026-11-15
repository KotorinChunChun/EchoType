using EchoType.Core;

namespace EchoType.Windows;

/// <summary>音声が1秒分増えるごとに最新の文字起こしを要求する。各区間は最長30秒。</summary>
public sealed class VoiceInputAccurateLive : IVoiceInputLive
{
    private sealed class Block
    {
        public MemoryStream Pcm { get; } = new();
        public int Sent;
        public bool Closed, Finalized;
        public string Text = "";
    }
    private readonly object _sync = new();
    private readonly List<Block> _blocks = [new()];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Func<SpeechSegment, CancellationToken, Task<AsrResult>> _recognize;
    private readonly Task<VoiceInputLiveResult> _loop;
    private bool _complete, _disposed;
    private string _error = "";
    public event Action<string>? Updated;
    public event Action<string>? Failed;
    public Task Stopped => _loop;
    public VoiceInputAccurateLive(Func<SpeechSegment, CancellationToken, Task<AsrResult>> recognize)
    { _recognize = recognize; _loop = Task.Run(RunAsync); }
    public void Feed(byte[] pcm)
    {
        lock (_sync)
        {
            if (_complete || _disposed) return;
            var offset = 0;
            while (offset < pcm.Length)
            {
                var block = _blocks[^1];
                var count = Math.Min(pcm.Length - offset, 30 * 48000 - (int)block.Pcm.Length);
                block.Pcm.Write(pcm, offset, count); offset += count;
                if (block.Pcm.Length == 30 * 48000) { block.Closed = true; _blocks.Add(new()); }
            }
            Wake();
        }
    }
    private void Wake() { if (_signal.CurrentCount == 0) _signal.Release(); }
    private async Task<VoiceInputLiveResult> RunAsync()
    {
        try
        {
            while (true)
            {
                Block? block; byte[] pcm; bool final;
                lock (_sync)
                {
                    block = _blocks.FirstOrDefault(b => !b.Finalized && (b.Closed || b.Pcm.Length - b.Sent >= 48000));
                    if (block is null)
                    {
                        if (_complete) return Result();
                        pcm = []; final = false;
                    }
                    else { pcm = block.Pcm.ToArray(); block.Sent = pcm.Length; final = block.Closed; }
                }
                if (block is null) { await _signal.WaitAsync(_cancel.Token); continue; }
                _cancel.Token.ThrowIfCancellationRequested();
                if (pcm.Length > 0)
                {
                    var wave = RecordingDevices.ToWave(pcm);
                    try
                    {
                        var result = await _recognize(new(Guid.NewGuid(), wave, DateTimeOffset.UtcNow, pcm.Length / 48000d), _cancel.Token);
                        lock (_sync)
                        {
                            if (result.Succeeded) { block.Text = result.Text.Trim(); _error = ""; }
                            else _error = result.Error ?? "途中認識に失敗しました。";
                        }
                    }
                    finally { Array.Clear(pcm); Array.Clear(wave); }
                }
                string text;
                lock (_sync)
                {
                    if (final) { block.Finalized = true; Array.Clear(block.Pcm.GetBuffer()); block.Pcm.Dispose(); }
                    text = Result().Text;
                    if (_disposed) return Result();
                }
                if (_error.Length > 0) Failed?.Invoke(_error);
                else Updated?.Invoke(text);
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { lock (_sync) return Result(); }
        catch (Exception ex) { lock (_sync) { _error = ex.Message; Failed?.Invoke(_error); return Result(); } }
        finally
        {
            lock (_sync)
            {
                _complete = true;
                foreach (var block in _blocks.Where(b => !b.Finalized)) { Array.Clear(block.Pcm.GetBuffer()); block.Pcm.Dispose(); block.Finalized = true; }
            }
        }
    }
    private VoiceInputLiveResult Result() => new(string.Concat(_blocks.Select(b => b.Text)), _error);
    public async Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct)
    {
        lock (_sync) { _complete = true; foreach (var block in _blocks) block.Closed = true; Wake(); }
        return await _loop.WaitAsync(ct);
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _complete = true; _cancel.Cancel(); }
        _ = _loop.ContinueWith(_ => { _cancel.Dispose(); _signal.Dispose(); }, TaskScheduler.Default);
    }
}

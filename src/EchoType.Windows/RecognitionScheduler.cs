using EchoType.Core;

namespace EchoType.Windows;

/// <summary>実行中の音声は奪わず、空いた枠を呼びかけ・ライブへ優先配分する。</summary>
public sealed class RecognitionScheduler
{
    private readonly object _sync = new();
    private readonly List<Waiter> _waiting = [];
    private readonly List<Waiter> _running = [];
    private int _limit = 2;
    private sealed class Waiter(RecognitionPurpose purpose, object? owner)
    {
        public RecognitionPurpose Purpose = purpose;
        public object? Owner { get; } = owner;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public Task<IDisposable> EnterAsync(RecognitionPurpose purpose, int limit, CancellationToken ct, object? owner = null)
    {
        if (limit is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(limit));
        ct.ThrowIfCancellationRequested();
        var waiter = new Waiter(purpose, owner);
        lock (_sync) { _limit = limit; _waiting.Add(waiter); Dispatch(); }
        return AwaitAsync(waiter, ct);
    }
    private async Task<IDisposable> AwaitAsync(Waiter waiter, CancellationToken ct)
    {
        using var registration = ct.Register(() => { lock (_sync) { if (_waiting.Remove(waiter)) waiter.Completion.TrySetCanceled(ct); Dispatch(); } });
        var lease = await waiter.Completion.Task.ConfigureAwait(false);
        if (ct.IsCancellationRequested) { lease.Dispose(); ct.ThrowIfCancellationRequested(); }
        return lease;
    }
    private void Dispatch()
    {
        while (_running.Count < _limit && _waiting.Count > 0)
        {
            var next = _waiting.OrderBy(x => x.Purpose).First();
            // ライブ処理中は新しい比較・録音再解析・事前読込を待機させ、空き枠とCPUをライブへ譲る。
            if (next.Purpose >= RecognitionPurpose.Archive && _running.Any(w => w.Purpose <= RecognitionPurpose.Full)) return;
            if (next.Purpose == RecognitionPurpose.BatchRetry && _running.Count >= Math.Max(1, _limit - 1)) return;
            _waiting.Remove(next); _running.Add(next);
            next.Completion.SetResult(new Lease(this, next));
        }
    }
    public void Promote(object owner, RecognitionPurpose purpose)
    {
        lock (_sync)
        {
            foreach (var waiter in _waiting.Concat(_running).Where(w => ReferenceEquals(w.Owner, owner)))
                if (purpose < waiter.Purpose) waiter.Purpose = purpose;
            Dispatch();
        }
    }
    private sealed class Lease(RecognitionScheduler owner, Waiter waiter) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) { owner._running.Remove(waiter); owner.Dispatch(); }
        }
    }
}

using EchoType.Core;

namespace EchoType.Windows;

/// <summary>上限内で貸出後も保持する。終了時は最後の返却で破棄し、返却通知で待機を進める。</summary>
internal sealed class ResidentAsrPool(AgentPaths paths) : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Resident> _workers = [];
    private bool _disposed;
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Resident(LocalAsrWorker worker)
    {
        public LocalAsrWorker Worker { get; } = worker;
        public int Users;
        public DateTime Used = DateTime.UtcNow;
        public bool Preferred;
    }

    public async Task<Lease> RentAsync(string key, int limit, CancellationToken ct, bool preferred = false)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var exists = _workers.TryGetValue(key, out var resident);
                var target = exists ? limit : limit - 1;
                foreach (var old in _workers.Where(p => p.Key != key && p.Value.Users == 0)
                    .OrderBy(p => p.Value.Preferred).ThenBy(p => p.Value.Used).Take(Math.Max(0, _workers.Count - target)).ToArray())
                {
                    _workers.Remove(old.Key);
                    old.Value.Worker.Dispose();
                }
                if (resident is not null || _workers.Count < limit)
                {
                    if (resident is null) _workers[key] = resident = new(new(paths));
                    resident.Preferred |= preferred;
                    resident.Users++;
                    return new(resident.Worker, () => Return(resident));
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Return(Resident resident)
    {
        lock (_sync)
        {
            resident.Users--;
            resident.Used = DateTime.UtcNow;
            if (_disposed && resident.Users == 0) resident.Worker.Dispose();
            SignalChange();
        }
    }

    private void SignalChange()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    public string Status(string model)
    {
        lock (_sync)
        {
            var matches = _workers.Where(p => p.Key.StartsWith(model + "/", StringComparison.Ordinal)).Select(p => p.Value).ToArray();
            return matches.Any(r => r.Users > 0) ? "使用中" : matches.Any(r => r.Worker.ProcessId is not null) ? "ロード済み" : "未ロード";
        }
    }

    public async Task UnloadAsync(string model, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            lock (_sync)
            {
                var matches = _workers.Where(p => p.Key.StartsWith(model + "/", StringComparison.Ordinal)).ToArray();
                if (matches.All(p => p.Value.Users == 0))
                {
                    foreach (var pair in matches)
                    {
                        _workers.Remove(pair.Key);
                        pair.Value.Worker.Dispose();
                    }
                    SignalChange();
                    return;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var resident in _workers.Values.Where(r => r.Users == 0)) resident.Worker.Dispose();
            _workers.Clear();
            SignalChange();
        }
    }

    internal sealed class Lease(LocalAsrWorker worker, Action release) : IDisposable
    {
        private Action? _release = release;
        public LocalAsrWorker Worker { get; } = worker;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

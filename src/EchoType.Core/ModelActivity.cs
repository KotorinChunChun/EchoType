namespace EchoType.Core;

public enum ModelPhase { Queued, WorkerWaiting, Loading, Processing, Inferencing, Observing, Cancelling, Completed, Failed, Cancelled, FinishedUnknown }

/// <summary>本文・音声・ファイルパスを含まない一要求の実行状態。</summary>
public sealed record ModelActivity(long Id, string Kind, string Model, string Purpose, string RequestedDevice,
    string ObservedDevice, ModelPhase Phase, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, bool Active)
{
    public static string RecognitionPurposeLabel(RecognitionPurpose purpose) => purpose switch
    {
        RecognitionPurpose.Inquiry => "問い合わせ判定", RecognitionPurpose.Immediate => "即時認識",
        RecognitionPurpose.Full => "全文認識", RecognitionPurpose.Archive => "過去の録音",
        RecognitionPurpose.Comparison => "認識比較", RecognitionPurpose.Warmup => "事前読込み",
        RecognitionPurpose.BatchRetry => "一括再認識", _ => "その他"
    };
    public string PhaseLabel => Phase switch
    {
        ModelPhase.Queued => "待機", ModelPhase.WorkerWaiting => "実行枠待ち・処理中（内訳未取得）",
        ModelPhase.Loading => "モデル読込み", ModelPhase.Processing => "処理中（内訳未取得）", ModelPhase.Inferencing => "認識中",
        ModelPhase.Observing => "監視中（推論の内訳未取得）",
        ModelPhase.Cancelling => "中断処理中", ModelPhase.Completed => "完了", ModelPhase.Failed => "失敗",
        ModelPhase.Cancelled => "中断", _ => "終了（結果未取得）"
    };
    public string DeviceLabel => "要求: " + RequestedDevice + " / 実際: " + ObservedDevice;
    public string DurationLabel => ((Active ? DateTimeOffset.UtcNow : UpdatedAt) - StartedAt).TotalSeconds.ToString("F1") + "秒";
}

/// <summary>同時要求をIDで分離し、完了分は最新64件だけ保持する。</summary>
public sealed class ModelActivityRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<long, ModelActivity> _items = [];
    private long _next;
    public Lease Begin(string kind, string model, string purpose, string requestedDevice, ModelPhase phase = ModelPhase.Queued)
    {
        lock (_sync)
        {
            var id = ++_next; var now = DateTimeOffset.UtcNow;
            _items[id] = new(id, kind, model, purpose, requestedDevice, "未確認", phase, now, now, true);
            return new(this, id);
        }
    }
    public ModelActivity[] Snapshot()
    {
        lock (_sync) return _items.Values.OrderByDescending(x => x.Active).ThenByDescending(x => x.UpdatedAt).ToArray();
    }
    private void Set(long id, ModelPhase phase, string? observedDevice, bool active)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(id, out var value) || !value.Active) return;
            _items[id] = value with { Phase = phase, ObservedDevice = observedDevice ?? value.ObservedDevice, UpdatedAt = DateTimeOffset.UtcNow, Active = active };
            foreach (var old in _items.Values.Where(x => !x.Active).OrderByDescending(x => x.UpdatedAt).Skip(64).Select(x => x.Id).ToArray()) _items.Remove(old);
        }
    }
    private void Rename(long id, string model, string device)
    {
        lock (_sync) if (_items.TryGetValue(id, out var value) && value.Active)
            _items[id] = value with { Model = model, RequestedDevice = device, UpdatedAt = DateTimeOffset.UtcNow };
    }
    public sealed class Lease(ModelActivityRegistry owner, long id) : IDisposable
    {
        private int _ended;
        public void Rename(string model, string requestedDevice) { if (Volatile.Read(ref _ended) == 0) owner.Rename(id, model, requestedDevice); }
        public void Update(ModelPhase phase, string? observedDevice = null) { if (Volatile.Read(ref _ended) == 0) owner.Set(id, phase, observedDevice, true); }
        public void Finish(ModelPhase phase, string? observedDevice = null)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.Set(id, phase, observedDevice, false);
        }
        public void Dispose() => Finish(ModelPhase.FinishedUnknown);
    }
}

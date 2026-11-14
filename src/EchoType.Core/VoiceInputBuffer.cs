namespace EchoType.Core;

public sealed record VoiceInputProgress(int Completed, int Total, int Failed, double Seconds);

/// <summary>モデル単位で即時公開し、表示は録音順。完了済みモデル結果だけを消費し、未完了結果は維持する。</summary>
public sealed class VoiceInputBuffer
{
    private sealed class Batch
    {
        public HashSet<string> Engines { get; } = [];
        public Dictionary<string, AsrResult> Results { get; } = [];
        public HashSet<string> Consumed { get; } = [];
    }
    private readonly List<Batch> _batches = [];
    public int Count => _batches.Count;
    public int Add() { _batches.Add(new()); return Count; }
    public void Schedule(int number, IEnumerable<string> engines) => _batches[number - 1].Engines.UnionWith(engines.Where(e => e.Length > 0));
    public void Complete(int number, AsrResult result)
    {
        var batch = _batches[number - 1];
        if (!batch.Engines.Contains(result.EngineId) || !batch.Results.TryAdd(result.EngineId, result))
            throw new InvalidOperationException("録音のモデルまたは完了状態が不正です。");
    }
    public AsrResult[] PendingResults(string engine) => _batches.Where(b => !b.Consumed.Contains(engine) && b.Results.ContainsKey(engine))
        .Select(b => b.Results[engine]).ToArray();
    public string Text(string engine) => string.Concat(PendingResults(engine).Where(r => r.Succeeded).Select(r => r.Text.Trim()));
    public VoiceInputProgress Progress(string engine)
    {
        var results = _batches.Where(b => !b.Consumed.Contains(engine) && b.Results.ContainsKey(engine)).Select(b => b.Results[engine]).ToArray();
        return new(results.Length, _batches.Count(b => !b.Consumed.Contains(engine) && b.Engines.Contains(engine)), results.Count(r => !r.Succeeded), results.Sum(r => r.ProcessingSeconds));
    }
    public void Consume()
    {
        // この瞬間に完了しているモデル結果だけを消費。処理中・待機中は維持する。
        foreach (var batch in _batches) batch.Consumed.UnionWith(batch.Results.Keys);
    }
    public void ConsumeRecordings(IEnumerable<int> numbers)
    {
        foreach (var number in numbers)
        {
            var batch = _batches[number - 1];
            batch.Consumed.UnionWith(batch.Engines);
        }
    }
    public void ConsumeModel(int number, string engine) => _batches[number - 1].Consumed.Add(engine);
    public bool IsPending(int number, string engine)
    {
        var batch = _batches[number - 1];
        return batch.Engines.Contains(engine) && !batch.Consumed.Contains(engine);
    }
}

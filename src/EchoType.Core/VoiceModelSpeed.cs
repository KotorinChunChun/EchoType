namespace EchoType.Core;

public static class VoiceModelSpeed
{
    // 2026-09-27の比較履歴を照合し、音声入力向け短文の読込済み実測順を採用する。
    // 未測定の追加モデルは末尾。履歴が得られたモデルから実測順へ組み込む。
    public static string[] Fallback => ["windows", "whisper-base", "whisper-small", "whisper-turbo-hotwords", "whisper-turbo", "kotoba-v2", "qwen3-0.6b", "qwen3-ja", "whisper-large-v3", "reazon-k2-v2", "parakeet-ja-ctc", "nemotron-3.5"];
    public static (string Engine, int Count, double Ratio)[] Measure(IEnumerable<AsrComparison> comparisons) => comparisons
        .Where(c => c.AudioSeconds > 0 && double.IsFinite(c.AudioSeconds))
        .SelectMany(c => c.Results.Where(r => Fallback.Contains(r.EngineId))
            .Select(r => (r.EngineId, Seconds: AsrComparisonTiming.Seconds(r)))
            .Where(r => r.Seconds.HasValue).Select(r => (r.EngineId, Ratio: r.Seconds!.Value / c.AudioSeconds)))
        .GroupBy(r => r.EngineId).Select(g =>
        {
            var values = g.Select(r => r.Ratio).Order().ToArray();
            return (g.Key, values.Length, (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2);
        }).OrderBy(r => r.Item3).ToArray();
    public static string[] Order(IEnumerable<AsrComparison> comparisons)
    {
        var measured = Measure(comparisons).Select(r => r.Engine).ToArray();
        return [.. measured, .. Fallback.Except(measured)];
    }
}

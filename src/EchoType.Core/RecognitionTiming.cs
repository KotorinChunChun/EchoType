namespace EchoType.Core;

public sealed record RecognitionTiming
{
    public DateTimeOffset? LastSoundAt { get; init; }
    public DateTimeOffset? SegmentedAt { get; init; }
    public DateTimeOffset? RecognizedAt { get; init; }
    public double PendingSeconds { get; init; }
    public double? RecognitionSeconds { get; init; }
    public double? CorrectionSeconds { get; init; }
    public DateTimeOffset? SpeechDeliveryAt { get; init; }
    public string SpeechDeliveryKind { get; init; } = "";
    public string Description => string.Join(" / ", new[] {
        LastSoundAt is { } end && SegmentedAt is { } cut ? $"区切り待ち {Math.Max(0, (cut-end).TotalSeconds):F2}秒" : "",
        RecognizedAt is not null ? $"処理待ち {PendingSeconds:F2}秒" : "",
        RecognitionSeconds is { } recognition ? $"認識 {recognition:F2}秒" : "",
        CorrectionSeconds is { } correction ? $"LLM補正 {correction:F2}秒" : "",
        LastSoundAt is { } sound && RecognizedAt is { } done ? $"発話終了→全文 {Math.Max(0, (done-sound).TotalSeconds):F2}秒" : "",
        LastSoundAt is { } last && SpeechDeliveryAt is { } delivery ? $"発話終了→{SpeechDeliveryKind} {Math.Max(0, (delivery-last).TotalSeconds):F2}秒" : ""
    }.Where(x => x.Length > 0));
}

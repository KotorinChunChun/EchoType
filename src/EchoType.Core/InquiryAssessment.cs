using System.Text.Json;

namespace EchoType.Core;

/// <summary>数値はLLMの推定値。時間と受付閾値はプログラム側で適用する。</summary>
public sealed record InquiryAssessment(double AssistantCalledProbability, double ContinuationProbability, double ValidInquiryProbability, string Reason)
{
    private static readonly JsonSerializerOptions ReadableJson = new(JsonFormat.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    public const double SilenceThreshold = 1, RemainingPerSecond = .85;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => new[] { AssistantCalledProbability, ContinuationProbability, ValidInquiryProbability }.All(x => double.IsFinite(x) && x is >= 0 and <= 100);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAddressedInquiry => IsValid && AssistantCalledProbability >= 50 && ValidInquiryProbability >= 50;
    public double ContinuationAt(double silenceSeconds) => ContinuationProbability * Math.Pow(RemainingPerSecond, Math.Max(0, silenceSeconds - SilenceThreshold));
    public string ToJson() => JsonSerializer.Serialize(this, ReadableJson);
    public static InquiryAssessment Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var name in new[] { "assistantCalledProbability", "continuationProbability", "validInquiryProbability", "reason" })
            if (!doc.RootElement.TryGetProperty(name, out _)) throw new InvalidOperationException("呼びかけ評価の必須項目がありません。");
        var value = JsonSerializer.Deserialize<InquiryAssessment>(json, JsonFormat.Options)!;
        if (value.Reason is null || value.Reason.Length > 500 || !value.IsValid)
            throw new InvalidOperationException("呼びかけ評価の値が不正です。実行しません。");
        return value;
    }
}

public sealed record InquirySignal(double SilenceSeconds, double ContinuationProbability, bool Acknowledge, bool Submit);

public sealed class InquiryProgress(InquiryAssessment assessment, DateTimeOffset lastSoundAt, bool acknowledged = false, RecognitionPerformance? options = null, bool completeQuestion = false, double? sentenceWaitSeconds = null)
{
    public InquiryAssessment Assessment { get; } = assessment;
    public DateTimeOffset LastSoundAt { get; } = lastSoundAt;
    public bool Acknowledged { get; private set; } = acknowledged;
    public bool Submitted { get; private set; }
    public bool Invalidated { get; private set; }
    public void Invalidate() => Invalidated = true;
    public InquirySignal Advance(DateTimeOffset now, bool allowSubmit = true)
    {
        var silence = Math.Max(0, (now - LastSoundAt).TotalSeconds);
        var continued = Assessment.ContinuationAt(silence);
        var eligible = !Invalidated && !Submitted && Assessment.IsAddressedInquiry && silence >= InquiryAssessment.SilenceThreshold;
        var acknowledge = eligible && !Acknowledged && (sentenceWaitSeconds is { } acknowledgeWait ? silence >= acknowledgeWait : continued <= 50);
        var timed = options is not null && (options.MaximumInquiryWaitSeconds > 0 && silence >= options.MaximumInquiryWaitSeconds ||
            completeQuestion && options.CompleteQuestionWaitSeconds > 0 && silence >= options.CompleteQuestionWaitSeconds);
        var submit = eligible && allowSubmit && (sentenceWaitSeconds is { } wait ? silence >= wait : continued <= 10 || timed);
        if (acknowledge) Acknowledged = true;
        if (submit) Submitted = true;
        return new(silence, continued, acknowledge, submit);
    }
}

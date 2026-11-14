namespace EchoType.Core;

public sealed record RecognitionFailure(string Id, string ComparisonId, DateTimeOffset CapturedAt,
    DateTimeOffset AttemptedAt, double AudioSeconds, AudioSourceInfo AudioSource, string DeviceName,
    string EngineId, string Error, string RawText, double ProcessingSeconds, double? RecognitionSeconds, string Mode)
{
    public string AudioFile { get; init; } = "";
    public bool SilenceProcessed { get; init; }
    public DateTimeOffset? OriginalStartedAt { get; init; }
    public double OriginalSeconds { get; init; }
    public RetainedAudioRange[] RetainedRanges { get; init; } = [];
    public string AudioHash { get; init; } = "";
    public bool IsPinned { get; init; }
    public string ImmediateFullText { get; init; } = "";
    public string LengthCheck { get; init; } = "";
    public string PinLabel => IsPinned ? "●" : "";
    public string EngineName => AsrEngines.All.FirstOrDefault(x => x.Id == EngineId)?.Name ?? EngineId;
    public string Category => Error.Contains("文字数差") ? "文字数差を不採用" : Error.Contains("繰り返し") ? "反復を不採用" : Error.Contains("LLM補正失敗") ? "LLM補正失敗" :
        Error.Contains("中止") || Error.Contains("優先したため") ? "中止・省略" : "認識失敗";
    public string Detail => $"発話開始: {CapturedAt.ToOffset(TimeSpan.FromHours(9)):yyyy/MM/dd HH:mm:ss} JST\n試行日時: {AttemptedAt.ToOffset(TimeSpan.FromHours(9)):yyyy/MM/dd HH:mm:ss} JST\n" +
        $"{Mode} / {AudioSource.SpeakerLabel} / {AudioSource.KindLabel} / {DeviceName}\nモデル: {EngineName}\n" +
        $"認識入力音声: {AudioSeconds:F2}秒 / 総処理: {ProcessingSeconds:F2}秒 / 認識処理: {(RecognitionSeconds is { } seconds ? seconds.ToString("F2") + "秒" : "不明")}\n理由: {Error}\n\n" +
        (LengthCheck.Length > 0 ? LengthCheck + "\n即時認識モデルで全文認識した結果:\n" + ImmediateFullText + "\n\n" : "") +
        (RawText.Length > 0 ? "診断用の認識出力（不採用・操作には使用しません）:\n" + RawText : "診断用の出力本文はありません。旧版で破棄された本文、または出力前に失敗した本文は復元できません。");
    public static IEnumerable<RecognitionFailure> From(AsrComparison comparison) => comparison.Results.Where(r => !r.Succeeded).Select(r => new RecognitionFailure(
        comparison.Id + ":" + comparison.AttemptId + ":" + r.EngineId, comparison.Id, comparison.StartedAt,
        comparison.LastAttemptAt ?? comparison.HistoryTime, comparison.AudioSeconds, comparison.AudioSource, comparison.DeviceName,
        r.EngineId, r.Error, r.RejectedText.Length > 0 ? r.RejectedText : r.Text, r.ProcessingSeconds, r.RecognitionSeconds,
        comparison.Replay ? "録音比較" : r.EngineId == comparison.PrimaryEngine ? "ライブ全文認識" : "ライブ比較") { AudioFile = comparison.ComparisonAudioFile, AudioHash = comparison.AudioHash, ImmediateFullText = r.ImmediateFullText, LengthCheck = r.LengthCheck,
            SilenceProcessed = comparison.SilenceProcessed, OriginalStartedAt = comparison.OriginalStartedAt, OriginalSeconds = comparison.OriginalSeconds, RetainedRanges = comparison.RetainedRanges });
}

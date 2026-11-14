namespace EchoType.Core;

/// <summary>同じ発言の各段階・再試行を上書きせず保持する。音声本文は参照のみ。</summary>
public sealed record AudioTrace(string Id, string SubjectId, DateTimeOffset At, string Stage, string State)
{
    public string AttemptId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Input { get; init; } = "";
    public string Output { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? ProcessingSeconds { get; init; }
    public double? WaitingSeconds { get; init; }
    public double? AudioSeconds { get; init; }
    public AudioSourceInfo AudioSource { get; init; } = new();
    public string SessionId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string RecordingFile { get; init; } = "";
    public string ComparisonId { get; init; } = "";
    public string ComparisonAudioFile { get; init; } = "";
    public static IReadOnlyList<AudioTrace> DisplayHistory(IEnumerable<AudioTrace> records, bool detailed = true)
    {
        var rows = records.ToArray();
        var finished = rows.Where(x => x.Stage == "全文文字起こし" && x.State is "完了" or "失敗" or "不採用" or "発話なし" or "検閲・対象外")
            .Select(x => (x.SubjectId, x.AttemptId, x.Model)).ToHashSet();
        var completed = rows.Where(x => x.Stage == "LLM補正" && x.State != "処理中")
            .GroupBy(x => x.SubjectId).ToDictionary(x => x.Key, x => x.Max(r => r.At));
        return rows.Where(x => x.State != "待機" || x.Stage is not ("全文認識受付" or "再認識") || x.AttemptId.Length == 0 ||
            !finished.Any(f => f.SubjectId == x.SubjectId && f.AttemptId == x.AttemptId && (x.Model.Length == 0 || f.Model == x.Model)))
            .Where(x => x.Stage != "LLM補正" || x.State != "処理中" ||
            !completed.TryGetValue(x.SubjectId, out var at) || x.At > at).Where(x => detailed || x.Stage is not ("音声の前処理" or "採否判定" or "ライブキャプション" or "音声の連結" or "継続・追加発言" or "区切り待機" or "継続待機終了" or "連結・再認識" or "発話区切り")).OrderBy(x => x.At).ThenBy(x => StageOrder(x.Stage)).ThenBy(x => x.Id).ToArray();
    }
    public static int StageOrder(string stage) => stage switch { "録音・波形" or "録音・発話範囲" => 0, "区切り待機" => 1, "連結・再認識" or "発話区切り" => 2, "即時モデルの全区間認識" => 3, "分類判定" => 4, "継続判定" => 5, "全文認識受付" or "再認識" => 6, "音声の前処理" => 7, "全文文字起こし" => 8, "採否判定" => 9, "LLM補正" => 10, _ => 11 };
    public string ModelName => Stage is "継続判定" or "操作受付" or "録音・発話範囲" or "区切り待機" or "連結・再認識" or "発話区切り" or "音声の連結" or "継続・追加発言" or "継続待機終了" or "音声の前処理" or "採否判定" || Model == "音量・区切り規則"
        ? "アルゴリズム" : AsrEngines.All.FirstOrDefault(e => e.Id == Model)?.Name ?? Model;
    public DateTimeOffset? TimelineStartedAt { get; init; }
    public const string SpeakerReviewStage = "話者の再処理";
    [System.Text.Json.Serialization.JsonIgnore]
    public double? ElapsedSeconds => Stage == SpeakerReviewStage
        ? TimelineStartedAt is { } start ? Math.Max(0, (At - start).TotalSeconds) : ProcessingSeconds
        : (TimelineStartedAt ?? (CapturedAt == default ? (DateTimeOffset?)null : CapturedAt)) is { } origin
            ? Math.Max(0, (At - origin).TotalSeconds) : null;
    public string ElapsedDisplay => ElapsedSeconds?.ToString("F2") ?? "—";
    public string SecondsDisplay => ProcessingSeconds is { } s ? s.ToString("F2") : "—";
    public string InputPreview => Preview(Input);
    public string OutputPreview => Preview(Output);
    private static string Preview(string text) => text.Replace('\r', ' ').Replace('\n', ' ') is var single && single.Length > 160 ? single[..160] + "…" : single;
    public static string Speech(string id) => "speech:" + id;
    public static string Recording(string file) => "recording:" + file;
}

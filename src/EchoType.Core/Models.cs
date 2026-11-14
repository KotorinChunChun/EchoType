using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoType.Core;

public sealed record AppSettings
{
    public DecisionSettings Decisions { get; init; } = new();
    public RecognitionPerformance Performance { get; init; } = new();
    public int SchemaVersion { get; init; } = 1;
    public string WakeWord { get; init; } = "ルナ,Luna";
    public bool WakeConfirmationEnabled { get; init; } = true;
    public string[] WakeConfirmationModels { get; init; } = AsrEngines.WakeConfirmationEnsemble.ToArray();
    // 旧単独モデル選択は読込みだけ受け付ける。
    [JsonPropertyName("wakeConfirmationEngine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyWakeConfirmationEngine { get => null; init { } }
    public double WakeFollowupSeconds { get; init; } = 12;
    public bool DialogueAutoStart { get; init; } = true;
    public double DialogueTimeoutSeconds { get; init; } = 60;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DialogueActive { get; init; }
    [JsonIgnore] public string DecisionUtteranceId { get; init; } = "";
    public bool RecordOnStart { get; init; }
    public bool CloudTranscription { get; init; }
    public bool GeminiTranscription { get; init; }
    public string TranscriptionModel { get; init; } = "gpt-4o-transcribe";
    public bool LocalVoiceCommands { get; init; } = true;
    public string ImmediateAsrEngine { get; init; } = "windows";
    public string? PlaybackImmediateAsrEngine { get; init; }
    public string? PlaybackPrimaryAsrEngine { get; init; }
    public bool WindowsLiveCaptions { get; init; }
    public int AsrVersion { get; init; }
    public string PrimaryAsrEngine { get; init; } = "nemotron-3.5";
    // 旧設定の読込専用。全文モデルを変更せず、次の保存では廃止キーを書き出さない。
    [JsonPropertyName("recordingAsrEngine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRecordingModel { get => null; init { } }
    [JsonPropertyName("playbackRecordingAsrEngine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlaybackRecordingModel { get => null; init { } }
    public bool AsrLengthCheckEnabled { get; init; } = true;
    public bool PlaybackDenoiseEnabled { get; init; } = true;
    public bool CompareAsrEngines { get; init; } = true;
    public string[] ComparisonAsrEngines { get; init; } = ["windows", "whisper-turbo", "qwen3-0.6b"];
    public string[]? ComparisonModelSlots { get; init; }
    public int ComparisonSortVersion { get; init; }
    public bool ComparisonFastestFirst { get; init; } = true;
    public string AsrVocabulary { get; init; } = "EchoType、Codex、Luna、IME、ユーザー辞書、PowerShell、ビルド";
    public string TranscriptCorrectionPrompt { get; init; } = TranscriptionCorrection.DefaultPrompt;
    public bool SkillVocabularyEnabled { get; init; } = true;
    public string SkillVocabulary { get; init; } = "";
    public bool ScreenVocabularyEnabled { get; init; } = true;
    public string LocalAsrDevice { get; init; } = "cpu";
    public bool UseSentenceEndingCountdown { get; init; }
    public double SentencePauseSeconds { get; init; } = 3;
    public double ContinuationPauseSeconds { get; init; } = 6;
    public double MaximumUtteranceSeconds { get; init; } = 30;
    public double EdgeSilenceSeconds { get; init; }
    public bool CutInteriorSilence { get; init; } = true;
    public double InteriorSilenceSeconds { get; init; } = .1;
    public bool ShortenOnSpeakerChange { get; init; } = true;
    public double SpeakerChangePauseSeconds { get; init; } = .5;
    public double PlaybackSpeechStartRms { get; init; } = .002;
    public double SpeechStartRms { get; init; } = .012;
    public ApiEnvSettings ApiEnvironment { get; init; } = new();
    public TtsSettings Tts { get; init; } = new();
    public bool SpeakAnswers { get; init; }
    public int AutomaticReplyWaitSeconds { get; init; } = 5;
    public bool SpeakAutomaticQuestions { get; init; } = true;
    public bool AutomaticReplyDetailed { get; init; }
    public bool SpeakSkillGuidance { get; init; } = true;
    public bool SuppressMicrophoneDuringSpeech { get; init; }
    public bool SuppressPlaybackDuringSpeech { get; init; }
    public string[] ProgramSearchRoots { get; init; } = [@"C:\develop"];
    public bool SearchInstalledApps { get; init; } = true;
    public int DeviceNumber { get; init; } = 0;
    public string DeviceName { get; init; } = "";
    public bool CapturePlayback { get; init; }
    public string PlaybackDeviceId { get; init; } = "";
    public string ReviewPlaybackDeviceId { get; init; } = "";
    public bool PlaybackDiarization { get; init; }
    public string DiarizationEnvironment { get; init; } = @"C:\AI\Models\Speech\Diarization";
    public string DiarizationDiscordUserId { get; init; } = "";
    public string DiarizationDiscordDeviceId { get; init; } = "";
    public string DiarizationDiscordCliPath { get; init; } = "";
    public string DiarizationDiscordPythonPath { get; init; } = "";
    public double DiarizationSimilarity { get; init; } = SpeakerProfiles.MinimumSimilarity;
    public double DiarizationLead { get; init; } = SpeakerProfiles.MinimumLead;
    public string MicrophoneSpeakerName { get; init; } = "あなた";
    public string PlaybackSpeakerName { get; init; } = "";
    public int DailyCloudMinutes { get; init; } = 120;
    public string CodexExecutable { get; init; } = "";
    public bool CodexEnabled { get; init; } = true;
    public string CodexHome { get; init; } = "";
    public long MinimumFreeBytes { get; init; } = 512L * 1024 * 1024;
}
public sealed record ActionResult(bool Success, string Message, string Status = "completed");
public sealed record Note(string Id, string Text, DateTimeOffset CreatedAt, string? SourceId = null);
public sealed record Transcript(string Id, string Text, DateTimeOffset CapturedAt, string Source)
{
    public DateTimeOffset? AssistantSentAt { get; init; }
    public string AssistantSentText { get; init; } = "";
    public bool AssistantWasSent => AssistantSentAt is not null || Handling.Contains("送信済み");
    public string AssistantMark => AssistantWasSent ? "送信済み" : Handling.Contains("聞き直し") ? "聞き直し" : Handling.StartsWith("呼びかけ受付") ? "呼びかけ受付" : "";
    public bool CategoryManuallySet { get; init; }
    public bool IsVoiceInput { get; init; }
    public string CategoryReason { get; init; } = "";
    public DateTimeOffset? CategoryAt { get; init; }
    public string CensoredText { get; init; } = "";
    public string DisplayText => CensoredText.Length > 0 ? CensoredText : Text;
    public RecognitionTiming Timing { get; init; } = new();
    public string InquiryText { get; init; } = "";
    public string InquiryEvaluationJson { get; init; } = "";
    public string OriginalText { get; init; } = "";
    public double? AudioSeconds { get; init; }
    public string DurationDisplay => (AudioSeconds ?? (EndedAt is { } end && end >= CapturedAt ? (end - CapturedAt).TotalSeconds : (double?)null)) is { } seconds ? seconds.ToString("F2") : "—";
    public string RecognitionError { get; init; } = "";
    public string PostProcessingStatus { get; init; } = "";
    public AudioSourceInfo AudioSource { get; init; } = new();
    public DateTimeOffset? EndedAt { get; init; }
    public string SessionId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Classification { get; init; } = "会話";
    [JsonIgnore] public string ClassificationDisplay => Classification == "プロンプト" ? "操作" : Classification;
    public string Reason { get; init; } = "過去の記録";
    public string Handling { get; init; } = "記録のみ";
    public bool IsFinal { get; init; } = true;
    public float? Confidence { get; init; }
    public string AsrModel { get; init; } = "";
    public string LocalDraft { get; init; } = "";
    public bool ComparisonOnly { get; init; }
    public string Response { get; init; } = "";
    public string LocalTime => CapturedAt.ToLocalTime().ToString("HH:mm:ss");
    public string DisplayState => CensoredText.Length > 0 ? "検閲・対象外" : Handling.StartsWith("自動破棄") ? "自動破棄" : RecognitionError.Length > 0 ? "全文認識失敗" : !IsFinal ? "即時認識中" :
        PostProcessingStatus.Replace("Luna", "LLM").StartsWith("LLM語彙補正済み") || PostProcessingStatus.Replace("Luna", "LLM").StartsWith("LLM語彙確認済み") ? "LLM補正済み" :
        PostProcessingStatus.Replace("Luna", "LLM") == "LLM語彙補正中" ? "全文認識済み → LLM補正中" : "全文認識済み";
    public string RecognitionDisplay => DisplayState + " / " +
        (AsrEngines.All.FirstOrDefault(e => e.Id == AsrModel)?.Name ?? AsrModel);
}
public sealed record RetainedAudioRange(int StartSample, int SampleCount);
public sealed record RecordingText(string FileName, string Text, string EngineId, bool UserConfirmed, DateTimeOffset UpdatedAt)
{
    public string RecognitionError { get; init; } = "";
    public string OriginalText { get; init; } = "";
    public string PostProcessingModel { get; init; } = "";
    public string SourceLabel => (EngineId == "manual" ? "手入力" : AsrEngines.All.FirstOrDefault(x => x.Id == EngineId)?.Name ?? "旧ライブ認識") + (RecognitionError.Length > 0 ? "（認識失敗: " + RecognitionError + "）" : PostProcessingModel.Length > 0 ? " ＋ LLM語彙補正" : "");
}
public sealed record Recording(string FileName, DateTimeOffset StartedAt, double Seconds, string DeviceName, string SessionId)
{
    public bool IsPinned { get; init; }
    public string RecordingAsrEngine { get; init; } = "";
    [JsonIgnore] public string FinalText { get; init; } = "";
    [JsonIgnore] public string FinalTextSource { get; init; } = "未確定";
    [JsonIgnore] public string PinLabel => IsPinned ? "📌" : "";
    [JsonIgnore] public string PlaybackDescription => $"{PinLabel}{LocalTime} / {AudioSource.SpeakerLabel} / {FinalTextSource} / {(FinalText.Length == 0 ? "文字起こし未確定" : FinalText[..Math.Min(FinalText.Length, 120)].Replace('\r', ' ').Replace('\n', ' ') + (FinalText.Length > 120 ? "…" : ""))}";
    public AudioSourceInfo AudioSource { get; init; } = new();
    public int AudioProcessingVersion { get; init; }
    public double OriginalSeconds { get; init; }
    public RetainedAudioRange[] RetainedRanges { get; init; } = [];
    public string LevelsCsv { get; init; } = "";
    [JsonIgnore] public double SourceSeconds => OriginalSeconds > 0 ? OriginalSeconds : Seconds;
    public double ToPlaybackSeconds(double sourceSeconds)
    {
        if (RetainedRanges.Length == 0) return Seconds > 0 ? Math.Clamp(sourceSeconds, 0, Seconds) : Math.Max(0, sourceSeconds);
        double elapsed = 0;
        foreach (var range in RetainedRanges)
        {
            var start = range.StartSample / 24000d; var length = range.SampleCount / 24000d;
            if (sourceSeconds < start + length) return elapsed + Math.Clamp(sourceSeconds - start, 0, length);
            elapsed += length;
        }
        return elapsed;
    }
    public string LocalTime => StartedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string Duration => Seconds > 0 ? TimeSpan.FromSeconds(Seconds).ToString(@"mm\:ss") : "再生時に確認";
}
public sealed record RunRecord(string Id, string SkillId, DateTimeOffset StartedAt, string Status, string Message);
public sealed record ModelReply(string Kind, string Text, string? SkillId)
{
    public string? Query { get; init; }
    public string? KnowledgeText { get; init; }
}
public sealed record SkillDraft(string Name, string UseWhen, string[] Examples, string SuccessMessage, string[] Arguments);
public sealed record ProgramCandidate(string Id, string Path, string Name, string Description, string SearchRoot, string Source)
{
    public string Kind => Path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? "PowerShell" : "アプリ";
}
public sealed record CandidateSuggestion(string CandidateId, string Reason);
public sealed record CandidateRanking(CandidateSuggestion[] Suggestions, string Explanation);

public static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}

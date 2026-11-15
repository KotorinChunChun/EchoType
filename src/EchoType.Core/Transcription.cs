using System.Globalization;
using System.Text;

namespace EchoType.Core;

public sealed record AsrEngine(string Id, string Name, bool Cloud = false, bool ComparisonOnly = false)
{
    public override string ToString() => Name;
}

public static class AsrEngines
{
    public static readonly string[] WakeConfirmationEnsemble = ["windows", "whisper-base", WindowsCaptionsDll, "nemotron-3.5"];
    public static string[] WakeModels(AppSettings settings) => WakeConfirmationEnsemble.Where(id => settings.WakeConfirmationModels.Contains(id)).ToArray();
    public static readonly AsrEngine[] Immediate = [new("windows", "Win System.Speech"), new("windows-ai-speech", "Win AI Speech"), new(WindowsCaptions, "Win LiveCaptions UIA"), new(WindowsCaptionsDll, "Win Speech SDK"), new("whisper-base", "Whisper base"), new("whisper-small", "Whisper small"), new("nemotron-3.5", "Nemotron 3.5 0.6B"), new(Haas, "Haas-ja-v1.0（Windows）")];
    public static string VocabularyHint(AppSettings settings) => string.Join("、",
        WakeWords.Parse(settings.AsrVocabulary).Concat(WakeWords.Parse(settings.WakeWord)).Concat(WakeWords.Parse(settings.SkillVocabularyEnabled ? settings.SkillVocabulary : "")).Distinct(StringComparer.OrdinalIgnoreCase));
    public static string CorrectionHint(AppSettings settings, string screen = "")
    {
        var hints = new StringBuilder();
        foreach (var word in WakeWords.Parse(VocabularyHint(settings)).Concat(WakeWords.Parse(screen)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (hints.Length + word.Length + 1 > 2000) break;
            if (hints.Length > 0) hints.Append('、');
            hints.Append(word);
        }
        return hints.ToString();
    }
    public static readonly AsrEngine[] Base = [
        new("windows", "Win System.Speech"),
        new("windows-ai-speech", "Win AI Speech"),
        new(WindowsCaptionsDll, "Win Speech SDK"),
        new("whisper-base", "Whisper base"),
        new("whisper-small", "Whisper small"),
        new("whisper-turbo", "Whisper large-v3-turbo"),
        new("whisper-turbo-hotwords", "Whisper large-v3-turbo（語彙強調）"),
        new("kotoba-v2", "Kotoba-Whisper v2.0-faster"),
        new("qwen3-0.6b", "Qwen3-ASR 0.6B"),
        new("qwen3-ja", "Qwen3-ASR 1.7B JA（日本語特化）"),
        new("whisper-large-v3", "Whisper large-v3"),
        new("reazon-k2-v2", "ReazonSpeech k2-v2"),
        new("parakeet-ja-ctc", "Parakeet 0.6B JA CTC"),
        new("nemotron-3.5", "Nemotron 3.5 0.6B"),
        new(Haas, "Haas-ja-v1.0（Windows）"),
        new("gpt-transcribe", "OpenAI gpt-transcribe", true),
        new("gpt-4o-transcribe", "OpenAI gpt-4o-transcribe", true),
        new(GeminiTranscribe, "Gemini 3.5 Transcribe", true)
    ];
    public const string GeminiTranscribe = "gemini-transcribe";
    public const string ExternalTranscription = "external-transcription";
    public const string Haas = "haas-ja-v1";
    public static bool CloudAllowed(AppSettings settings, string id) => BaseId(id) == GeminiTranscribe ? settings.GeminiTranscription : settings.CloudTranscription;
    public const string WindowsCaptions = "windows-live-captions";
    public const string WindowsCaptionsDll = "windows-live-captions-dll";
    public static readonly AsrEngine[] Recognition = [Base[0], new(WindowsCaptions, "Win LiveCaptions UIA"), .. Base.Skip(1)];
    public static readonly AsrEngine[] All = [.. Recognition];
    // 旧履歴のモデル名・計測条件を読むための互換情報。選択欄には公開しない。
    private static readonly AsrEngine[] Legacy = [
        new(ExternalTranscription, "外部音声認識", true),
        new("qwen3-1.7b", "Qwen3-ASR 1.7B（公式・日本語対応）"),
        new("whisper-turbo-nohint", "Whisper large-v3-turbo（ヒントなし）")
    ];
    public static bool IsSelectable(string id) => All.Any(e => e.Id == id);
    public static bool HasPostProcessing(string id) => id.EndsWith("+llm", StringComparison.Ordinal);
    public static string BaseId(string id) => HasPostProcessing(id) ? id[..^4] : id;
    public static AsrEngine Get(string id)
    {
        var engine = All.Concat(Legacy).FirstOrDefault(e => e.Id == BaseId(id))
            ?? throw new InvalidOperationException("未対応の音声認識エンジンです。");
        return HasPostProcessing(id) ? new(id, "LLM補正（Luna）＋ " + engine.Name, engine.Cloud, true) : engine;
    }
    public static string MigrateSelection(string id) => BaseId(id) switch
    {
        "qwen3-1.7b" => "qwen3-0.6b",
        "whisper-turbo-nohint" => "whisper-turbo",
        var current => current
    };
    public static AppSettings MigrateSelections(AppSettings settings) => settings with
    {
        PrimaryAsrEngine = MigrateSelection(settings.PrimaryAsrEngine),
        PlaybackPrimaryAsrEngine = settings.PlaybackPrimaryAsrEngine is { } playback ? MigrateSelection(playback) : null,
        ImmediateAsrEngine = MigrateSelection(settings.ImmediateAsrEngine),
        PlaybackImmediateAsrEngine = settings.PlaybackImmediateAsrEngine is { } immediate ? MigrateSelection(immediate) : null,
        ComparisonAsrEngines = settings.ComparisonAsrEngines.Select(MigrateSelection).Distinct().ToArray(),
        ComparisonModelSlots = settings.ComparisonModelSlots?.Select(MigrateSelection).ToArray()
    };
    public static bool IsLocal(string id) => BaseId(id) is not ("windows" or WindowsCaptions) && !Get(id).Cloud;
    public static string ModelId(string id) => BaseId(id) is "whisper-turbo-hotwords" or "whisper-turbo-nohint" ? "whisper-turbo" : BaseId(id);
    public static bool IsQwen(string id) => BaseId(id) is "qwen3-ja" or "qwen3-0.6b" or "qwen3-1.7b";
    public static bool IsWhisper(string id) => ModelId(id) is "whisper-base" or "whisper-small" or "whisper-turbo" or "kotoba-v2" or "whisper-large-v3";
    public static bool IsFastLocal(string id) => BaseId(id) is "reazon-k2-v2" or "parakeet-ja-ctc" or "nemotron-3.5";
    public static bool IsCpuOnly(string id) => BaseId(id) is "reazon-k2-v2" or "parakeet-ja-ctc";
    public static string[] Slots(AppSettings settings) => (InitializeComparisonSelection(settings).ComparisonModelSlots ?? [])
        .Concat(Enumerable.Repeat("", 8)).Take(8).ToArray();
    public static string[] Assigned(IEnumerable<string> slots) => slots.Where(id => id.Length > 0).Distinct().ToArray();
    public static AppSettings UseForCaptions(AppSettings settings, string engine)
    {
        Get(engine);
        if (!IsSelectable(engine)) throw new InvalidOperationException("このモデルは選択肢から削除されています。別のモデルを選んでください。");
        if (HasPostProcessing(engine)) throw new InvalidOperationException("LLM併用は比較専用です。全文認識には元のモデルを選んでください。");
        return settings with { PrimaryAsrEngine = engine };
    }
    public static string[] Selected(AppSettings settings) => (settings.CompareAsrEngines
        ? settings.ComparisonAsrEngines.Append(settings.PrimaryAsrEngine)
        : [settings.PrimaryAsrEngine]).Distinct().ToArray();
    public static AppSettings InitializeComparisonSelection(AppSettings settings)
    {
        settings = MigrateSelections(settings);
        if (settings.ComparisonSortVersion == 0) settings = settings with { ComparisonSortVersion = 1, ComparisonFastestFirst = true };
        if (settings.ComparisonModelSlots is not null) return settings;
        // 旧既定値だけを移行する。保存済みの独自選択や、移行後の変更は上書きしない。
        var engines = settings.ComparisonAsrEngines.SequenceEqual(new[] { "windows", "whisper-turbo", "kotoba-v2" })
            ? new AppSettings().ComparisonAsrEngines : settings.ComparisonAsrEngines;
        var slots = engines.Concat(new AppSettings().ComparisonAsrEngines).Distinct().Take(Math.Clamp(engines.Length, 3, 8)).ToArray();
        return settings with { ComparisonAsrEngines = engines, ComparisonModelSlots = slots };
    }
    public static void Validate(AppSettings settings)
    {
        if (settings.WakeConfirmationModels is null || settings.WakeConfirmationModels.Any(id => !WakeConfirmationEnsemble.Contains(id)) ||
            settings.WakeConfirmationModels.Distinct().Count() != settings.WakeConfirmationModels.Length ||
            (settings.WakeConfirmationEnabled && settings.WakeConfirmationModels.Length == 0))
            throw new InvalidOperationException("呼びかけ名の確認には1つ以上のモデルを選んでください。対象はWindows音声認識・Whisper base・Windowsライブキャプション（DLL）・Nemotronです。");
        ValidateSilence(settings);
        settings.Performance.Validate();
        if (!Immediate.Any(e => e.Id == settings.ImmediateAsrEngine)) throw new InvalidOperationException("対応する即時認識モデルを選んでください。");
        foreach (var id in Selected(settings).Concat(settings.ComparisonModelSlots ?? []).Where(id => id.Length > 0))
            if (!IsSelectable(id)) throw new InvalidOperationException("選択可能な音声認識モデルを指定してください。");
        if (settings.CompareAsrEngines && settings.ComparisonAsrEngines.Length is < 1 or > 8)
            throw new InvalidOperationException("比較モデルは1〜8個で指定してください。");
        if (settings.LocalAsrDevice is not ("cpu" or "cuda")) throw new InvalidOperationException("音声認識の処理デバイスが不正です。");
        if (!double.IsFinite(settings.SentencePauseSeconds) || !double.IsFinite(settings.ContinuationPauseSeconds) ||
            settings.SentencePauseSeconds is < 2 or > 10 || settings.ContinuationPauseSeconds < settings.SentencePauseSeconds || settings.ContinuationPauseSeconds > 15)
            throw new InvalidOperationException("文末の待機は2〜10秒、文中の待機は文末以上〜15秒で設定してください。");
        if (!double.IsFinite(settings.WakeFollowupSeconds) || settings.WakeFollowupSeconds is < 1 or > 60) throw new InvalidOperationException("呼びかけ後の待機は1〜60秒で指定してください。");
        if (settings.AsrVocabulary.Length > 1000) throw new InvalidOperationException("語彙ヒントは1000文字以内にしてください。");
        if (settings.TranscriptCorrectionPrompt.Length > 8000) throw new InvalidOperationException("LLM補正プロンプトは8000文字以内にしてください。");
        if (!double.IsFinite(settings.SpeakerChangePauseSeconds) || settings.SpeakerChangePauseSeconds is < .1 or > 2)
            throw new InvalidOperationException("話者交代の残り待機は0.1〜2秒で入力してください。");
        if (!double.IsFinite(settings.MaximumUtteranceSeconds) || settings.MaximumUtteranceSeconds is < 5 or > 60 || settings.MaximumUtteranceSeconds < settings.ContinuationPauseSeconds)
            throw new InvalidOperationException("最大発話秒数は5〜60秒かつ文中待機以上にしてください。");
        if (!double.IsFinite(settings.PlaybackSpeechStartRms) || settings.PlaybackSpeechStartRms is < .0001 or > .2) throw new InvalidOperationException("再生音声の発話開始RMSは0.0001〜0.2で指定してください。");
        if (!double.IsFinite(settings.SpeechStartRms) || settings.SpeechStartRms is < .001 or > .2)
            throw new InvalidOperationException("発話開始のRMSは0.001〜0.2で指定してください。");
    }
    public static void ValidateSilence(AppSettings settings)
    {
        if (!double.IsFinite(settings.EdgeSilenceSeconds) || settings.EdgeSilenceSeconds is < 0 or > 10 ||
            !double.IsFinite(settings.InteriorSilenceSeconds) || settings.InteriorSilenceSeconds is < 0 or > 10)
            throw new InvalidOperationException("開始・終了と途中に残す無音は0〜10秒で入力してください。");
    }
}

public sealed record AsrResult(string EngineId, string Text, double ProcessingSeconds, string Error = "", float? Confidence = null)
{
    public bool ModelError { get; init; }
    public double? ModelLoadSeconds { get; init; }
    public bool? ModelRetained { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModelPreparationDetail => Reused ? "認識結果再利用（モデル処理なし）" : ModelLoadSeconds is { } seconds ? $"モデル準備 {seconds:F3}秒 / 常駐モデル{(ModelRetained == true ? "再利用" : "初回ロード")}" : "";
    public string CensoredText { get; init; } = "";
    public double QueueSeconds { get; init; }
    public double PendingSeconds { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool Reused { get; init; }
    public string ComputeProfile { get; init; } = "";
    public string AudioHash { get; init; } = "";
    public string ProcessedAudioHash { get; init; } = "";
    public string AudioPreprocessing { get; init; } = "";
    public double AudioPreprocessingSeconds { get; init; }
    public double? RecognitionAudioSeconds { get; init; }
    public string RejectedText { get; init; } = "";
    public bool InputVerified { get; init; } = true;
    public string ImmediateFullText { get; init; } = "";
    public string LengthCheck { get; init; } = "";
    public string OriginalText { get; init; } = "";
    public double? PostProcessingSeconds { get; init; }
    public string PostProcessingModel { get; init; } = "";
    public string PostProcessingDetail { get; init; } = "";
    public double? RecognitionSeconds { get; init; }
    public double? StartOffsetSeconds { get; init; }
    public string CaptionCoverage { get; init; } = "";
    public CaptionMissingRange[] CaptionMissingRanges { get; init; } = [];
    public double? CaptionReplaySeconds { get; init; }
    public double? CaptionSetupSeconds { get; init; }
    public double? CaptionTailWaitSeconds { get; init; }
    public DateTimeOffset? CaptionPlaybackEndedAt { get; init; }
    public DateTimeOffset? CaptionLastTextChangeAt { get; init; }
    public DateTimeOffset? CaptionStabilityConfirmedAt { get; init; }
    public double? CaptionTextSettleSeconds { get; init; }
    public string EngineName => AsrEngines.Get(EngineId).Name;
    public bool Succeeded => Error.Length == 0;
    public string Status => (Succeeded ? (Text.Length == 0 ? "発話なし" : "完了") : Error) + (AudioPreprocessing.Length > 0 ? $" / {AudioPreprocessing} {AudioPreprocessingSeconds:F2}秒" : "");
}
public sealed record AsrComparison(string Id, DateTimeOffset StartedAt, double AudioSeconds, string SessionId, string DeviceName, string PrimaryEngine, bool Replay, AsrResult[] Results)
{
    public bool SilenceProcessed { get; init; }
    public DateTimeOffset? OriginalStartedAt { get; init; }
    public double OriginalSeconds { get; init; }
    public RetainedAudioRange[] RetainedRanges { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset AudioEndedAt => OriginalStartedAt is { } original && RetainedRanges.Length > 0
        ? original.AddSeconds(((long)RetainedRanges[^1].StartSample + RetainedRanges[^1].SampleCount) / 24000d)
        : StartedAt.AddSeconds(AudioSeconds);
    public DateTimeOffset? LastSoundAt { get; init; }
    public DateTimeOffset? SegmentedAt { get; init; }
    public DateTimeOffset? EnqueuedAt { get; init; }
    public AudioSourceInfo AudioSource { get; init; } = new();
    public string ReferenceText { get; init; } = "";
    public string EvaluationTerms { get; init; } = "";
    public string RecordingFile { get; init; } = "";
    public string AudioHash { get; init; } = "";
    public string[] RequestedEngines { get; init; } = [];
    public bool Complete { get; init; }
    public string AttemptId { get; init; } = "";
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? ComparedAt { get; init; }
    public string ComparisonAudioFile { get; init; } = "";
    public string AudioLevelsCsv { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("saveAsRecordingTranscript"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyRecordingRecognition { get => null; init { } }
    public DateTimeOffset HistoryTime => ComparedAt ?? StartedAt;
    public string LocalTime => HistoryTime.ToLocalTime().ToString("MM/dd HH:mm:ss");
    public string Description => $"{LocalTime} ・ {AudioSource.SpeakerLabel} / {AudioSource.KindLabel} ・ {AudioSeconds:F1}秒 ・ {(Replay ? "録音比較（操作なし）" : "ライブ")}";
}

/// <summary>語尾は確定的な意味解析ではなく、短い無音で切らないための保守的な補助情報。</summary>
public static class SpeechBoundary
{
    public static bool SeemsComplete(string text)
    {
        var value = text.Trim().TrimEnd('。', '！', '!', '？', '?');
        return value.Length > 1 && new[] { "です", "ます", "ました", "でした", "ません", "ませんでした", "ください", "ですか", "ますか", "でしょうか", "して", "開いて", "お願い", "お願いします", "してほしい", "教えて", "調べて", "したい", "と思う", "だよ", "だね", "ですよ", "ですね", "ますね", "よね", "かな", "だろうか", "ほしいんですけど" }.Any(value.EndsWith);
    }
    public static double PauseSeconds(string hint, AppSettings settings) => SeemsComplete(hint) ? settings.SentencePauseSeconds : settings.ContinuationPauseSeconds;
    public static bool ShouldFinish(double silenceSeconds, double durationSeconds, string hint, AppSettings settings) =>
        durationSeconds >= settings.MaximumUtteranceSeconds || silenceSeconds >= PauseSeconds(hint, settings);
}

public static class TranscriptionScore
{
    // 句読点・空白・英字大小・全半角を除外。漢字/かなの表記差は誤りとして残す。
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes()
        .Where(r => !Rune.IsWhiteSpace(r) && !Rune.IsPunctuation(r) && Rune.GetUnicodeCategory(r) != UnicodeCategory.Format));
    public static double? CharacterErrorRate(string reference, string hypothesis)
    {
        var expected = Normalize(reference).EnumerateRunes().ToArray();
        var actual = Normalize(hypothesis).EnumerateRunes().ToArray();
        if (expected.Length == 0) return null;
        if (expected.Length > 8000 || actual.Length > 8000) throw new InvalidOperationException("評価する文章は8000文字以内にしてください。");
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray(); var current = new int[actual.Length + 1];
        for (var i = 1; i <= expected.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= actual.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return (double)previous[actual.Length] / expected.Length;
    }
}

public sealed class UtteranceDraft
{
    private readonly Dictionary<Guid, (DateTimeOffset At, string Text)> _pieces = [];
    public string Update(Guid id, DateTimeOffset at, string text)
    {
        _pieces[id] = (at, text);
        return string.Join("", _pieces.Values.OrderBy(p => p.At).Select(p => p.Text));
    }
}

public static class AsrRouting
{
    public static bool AllowsVoiceCommand(Transcript transcript) => transcript.AsrModel != AsrEngines.WindowsCaptions && transcript.CensoredText.Length == 0 && !InputCensorship.Contains(transcript.Text) && transcript.AudioSource.Kind == AudioSourceKind.Microphone;
    public static Transcript? PrimaryTranscript(AsrComparison comparison, string currentPrimary)
    {
        var primary = comparison.Results.FirstOrDefault(r => r.EngineId == comparison.PrimaryEngine);
        if (primary is not null) primary = AsrTextGuard.Filter(primary);
        if (comparison.Replay || primary is null || (!primary.InputVerified && primary.EngineId != AsrEngines.WindowsCaptions) || !primary.Succeeded || string.IsNullOrWhiteSpace(primary.Text)) return null;
        return new Transcript(comparison.Id, primary.Text, comparison.StartedAt, "asr")
        {
            AudioSeconds = comparison.AudioSeconds, EndedAt = comparison.AudioEndedAt, SessionId = comparison.SessionId,
            DeviceName = comparison.DeviceName, AudioSource = comparison.AudioSource, AsrModel = primary.EngineId, Confidence = primary.Confidence,
            ComparisonOnly = comparison.PrimaryEngine != currentPrimary || primary.EngineId == AsrEngines.WindowsCaptions
            , Timing = new() { LastSoundAt = comparison.LastSoundAt, SegmentedAt = comparison.SegmentedAt,
                RecognizedAt = primary.CompletedAt, RecognitionSeconds = primary.RecognitionSeconds,
                PendingSeconds = primary.PendingSeconds + primary.QueueSeconds, CorrectionSeconds = primary.PostProcessingSeconds }
        };
    }
}

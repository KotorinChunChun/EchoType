namespace EchoType.Core;

/// <summary>通常録音・比較の設定と独立した音声入力専用設定。</summary>
public sealed record VoiceInputSettings
{
    public bool Enabled { get; init; } = true;
    public bool AutomaticInput { get; init; }
    public bool AutomaticFormatting { get; init; } = true;
    public bool AutomaticConfirm { get; init; } = true;
    public bool ReplaceTargetText { get; init; }
    public bool CapturePanelExpanded { get; init; } = true;
    public double WindowWidth { get; init; } = 1080;
    public double WindowHeight { get; init; } = 850;
    public int TargetGap { get; init; } = 32;
    public int[] TriggerKeys { get; init; } = [0xA2, 0xA0];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VoiceRecordingShortcut? RecordingShortcut { get; init; }
    public VoiceRecordingShortcut GetRecordingShortcut() => RecordingShortcut ?? VoiceRecordingShortcut.FromModifiers(TriggerKeys);
    public static (int Key, string Name)[] ModifierKeys => [(0xA2, "左Ctrl"), (0xA3, "右Ctrl"), (0xA0, "左Shift"), (0xA1, "右Shift"), (0xA4, "左Alt"), (0xA5, "右Alt"), (0x5B, "左Windows"), (0x5C, "右Windows")];
    public string[] SentenceEndings { get; init; } = ["です", "ます", "ください", "ですか", "ますか", "でした", "ました", "ません", "ませんか", "ましょう", "でしょう", "でしょうか", "くださいね", "くださいませ", "お願いします", "ありがとう", "ありがとうございます", "了解しました", "承知しました", "必要です", "ほしい", "ほしいです", "だと思います", "なのです", "なのだ", "である", "だった", "しました", "できます", "できますか", "できません", "したい", "したいです", "してほしい", "してください", "してくださいませんか"];
    public string CompleteCandidate(string text)
    {
        var value = text.TrimEnd();
        return SentenceEndings.Any(ending => ending.Length > 0 && value.EndsWith(ending, StringComparison.Ordinal)) ? value + "。" : value;
    }
    public VoiceInputSettings WithShared(AppSettings shared) => this with { Recognition = Recognition with { AsrVocabulary = shared.AsrVocabulary, CodexExecutable = shared.CodexExecutable, Performance = shared.Performance, Decisions = shared.Decisions } };
    public int MaximumSeconds { get; init; } = 60;
    public string LiveEngine { get; init; } = "windows";
    public ExternalSpeechSettings ExternalSpeech { get; init; } = new();
    public bool LiveDisplayEnabled { get; init; }
    public ExternalSpeechSettings LiveSpeech { get; init; } = new();
    public ExternalSpeechSettings FinalSpeech() => ExternalSpeech.Enabled ? ExternalSpeech : ExternalSpeechSettings.SpeechCppDefault;
    public ExternalSpeechSettings PreviewSpeech() => LiveSpeech.Enabled ? LiveSpeech : ExternalSpeechSettings.SpeechCppDefault;
    public string FormattingModel { get; init; } = "gpt-6-luna";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PersonalCorrections { get; init; } = "[]";
    public string FormattingPrompt { get; init; } = DefaultFormattingPrompt;
    public string SimpleFormattingPrompt { get; init; } = "意味・情報・依頼内容を変えず、誤字、句読点、明らかな言い直しや重複だけを修正してください。要約、情報の追加、指示文への再構成はせず、自然で読みやすい文章にしてください。修正した本文だけを返してください。";
    public string FormattingEffort { get; init; } = "low";
    public static string[] FormattingEfforts => ["low", "medium", "high"];
    public static string AppendSentence(string text) => text.TrimEnd() is var value && value.Length > 0 && !value.EndsWith('。') ? value + "。" : value;
    public static string[] FormattingModels => ["gpt-6-luna", "gpt-6-sol", "gpt-6-astra"];
    public const string PreviousFormattingPrompt = "AIに送るプロンプトとして適切な、分かりやすい指示文に整形してください。目的、依頼内容、条件が伝わるように、話し言葉や重複を整理し、必要なら文章の構成や表現を大幅に書き直してください。元の意図や重要な条件は保ち、述べられていない事実や要求は作らないでください。整形した指示文だけを返してください。";
    public const string DefaultFormattingPrompt = """
        話し言葉の文字起こし文章を、AIに送るプロンプトとして適切な分かりやすい指示文に整形してください。
        前提、目的、依頼内容、条件が正しく伝わるように、撤回や重複を整理し、必要なら文章の構成や表現を大幅に書き直してでも整形してください。
        元の意図や重要な条件は保ち、述べられていない事実や要求は作らないでください。整形した指示文だけを返してください。
        フォーマットは箇条書きで、「前提は～です」「条件は～です」という解説か、「～して」という簡潔な命令文か、「～か教えて 」という疑問文に揃えてください。複雑なプロンプトに限り##見出しも使ってください。
        """;
    public static AsrEngine[] LocalEngines => AsrEngines.All.Where(e => !e.Cloud && !e.ComparisonOnly && e.Id == AsrEngines.BaseId(e.Id)).ToArray();
    public static AsrEngine[] LiveEngines => LocalEngines.Where(e => e.Id is "windows" or "windows-ai-speech" or "whisper-turbo" or AsrEngines.WindowsCaptions or AsrEngines.WindowsCaptionsDll).ToArray();
    public VoiceInputSettings LocalOnly()
    {
        var models = Models.Select(m => LocalEngines.Any(e => e.Id == m) ? m : "").ToArray();
        if (!models.Any(m => m.Length > 0)) models = ["windows", "", "", "", "", "", "", ""];
        return this with { Models = models, Recognition = Recognition with { CloudTranscription = false, GeminiTranscription = false } };
    }
    public VoiceInputSettings MigrateSelections() => this with
    {
        Models = Models.Select(AsrEngines.MigrateSelection).ToArray(),
        LiveEngine = AsrEngines.MigrateSelection(LiveEngine),
        Recognition = AsrEngines.MigrateSelections(Recognition with
        {
            DeviceNumber = Recognition.DeviceNumber == 0 && Recognition.DeviceName.Length == 0 ? -1 : Recognition.DeviceNumber
        })
    };
    public string[] Models { get; init; } = ["windows", "", "", "", "", "", "", ""];
    public string[] CandidateModels() => ExternalSpeech.Enabled ? [AsrEngines.ExternalTranscription] : Models;
    public string[] RecognitionModels() => (AutomaticInput ? CandidateModels().Where(m => m.Length > 0).Take(1) : CandidateModels().Where(m => m.Length > 0))
        .Distinct().ToArray();
    public string[] WhisperWarmupModels() => !Enabled || ExternalSpeech.Enabled ? [] : (AutomaticInput ? RecognitionModels() : Models.Take(1).Concat([LiveEngine]).Concat(Models))
        .Where(id => !string.IsNullOrEmpty(id)).Select(AsrEngines.ModelId)
        .Where(id => id.StartsWith("whisper-", StringComparison.Ordinal) || id == "kotoba-v2").Distinct().ToArray();
    public AppSettings Recognition { get; init; } = new()
    {
        DeviceNumber = -1, WakeWord = "", AsrVocabulary = "", ScreenVocabularyEnabled = false,
        SkillVocabularyEnabled = false, PlaybackDenoiseEnabled = false, AsrLengthCheckEnabled = false
    };
    public void Validate()
    {
        if (RecordingShortcut is null && (TriggerKeys is null || TriggerKeys.Length == 0 || TriggerKeys.Distinct().Count() != TriggerKeys.Length || TriggerKeys.Any(k => !ModifierKeys.Any(m => m.Key == k))))
            throw new InvalidOperationException("録音に使う修飾キーを1個以上選択してください。");
        GetRecordingShortcut().Validate();
        if (SentenceEndings is null || SentenceEndings.Length > 200 || SentenceEndings.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 80 || s.Any(char.IsControl))) throw new InvalidOperationException("終了文字列は200件以内、各1〜80文字で指定してください。");
        if (!double.IsFinite(WindowWidth) || !double.IsFinite(WindowHeight) || WindowWidth is < 200 or > 16000 || WindowHeight is < 150 or > 16000) throw new InvalidOperationException("保存ウィンドウサイズが不正です。");
        if (TargetGap is < 0 or > 200) throw new InvalidOperationException("入力欄との間隔は0〜200で指定してください。");
        if (MaximumSeconds is < 2 or > 300) throw new InvalidOperationException("最大録音時間は2〜300秒です。");
        if (ExternalSpeech.Mode == ExternalSpeechMode.TranscriptionServer && ExternalSpeech.Model.Length == 0) ExternalSpeech.ValidateAddress();
        else if (ExternalSpeech.Enabled) ExternalSpeech.ValidateEndpoint();
        if (LiveDisplayEnabled) PreviewSpeech().ValidateEndpoint();
        if (!LiveEngines.Any(e => e.Id == LiveEngine)) throw new InvalidOperationException("途中認識モデルを確認してください。");
        if (Models is null || Models.Length is < 1 or > 8 || !Models.Any(m => !string.IsNullOrEmpty(m)) ||
            Models.Any(m => m is null || m.Length > 0 && !LocalEngines.Any(e => e.Id == m)))
            throw new InvalidOperationException("1〜8枠に認識モデルを選択してください。");
        if (Recognition.CloudTranscription || Recognition.GeminiTranscription || Recognition.LocalAsrDevice is not ("cpu" or "cuda") || Recognition.AsrVocabulary.Length > 1000)
            throw new InvalidOperationException("ローカル認識デバイス・語彙ヒントの指定を確認してください。");
        if (!FormattingEfforts.Contains(FormattingEffort) || string.IsNullOrWhiteSpace(SimpleFormattingPrompt) || SimpleFormattingPrompt.Length > 8000)
            throw new InvalidOperationException("エフォートと単純整形の指示を確認してください。");
        if (!FormattingModels.Contains(FormattingModel) || string.IsNullOrWhiteSpace(FormattingPrompt) || FormattingPrompt.Length > 8000)
            throw new InvalidOperationException("整形モデルと整形指示（1〜8000文字）を確認してください。");
    }
}

/// <summary>左修飾キーだけの長押し。時刻は呼出側の単調時計から与える。</summary>
public sealed class VoiceInputGesture
{
    private bool _leftControl, _leftShift, _blocked, _active;
    private readonly HashSet<int> _otherKeys = [];
    private long _started;
    public bool Active => _active;
    public enum Change { None, Started, Accepted, Discarded }
    public Change Key(int key, bool down, long milliseconds)
    {
        if (key == 0xA2) _leftControl = down;
        else if (key == 0xA0) _leftShift = down;
        else if (down)
        {
            _otherKeys.Add(key);
            _blocked = true;
            if (_active) { _active = false; return Change.Discarded; }
        }
        else _otherKeys.Remove(key);
        if (!_leftControl || !_leftShift)
        {
            var wasActive = _active; _active = false;
            if (!_leftControl && !_leftShift) _blocked = false;
            return wasActive ? milliseconds - _started >= 1000 ? Change.Accepted : Change.Discarded : Change.None;
        }
        if (_blocked || _active || _otherKeys.Count > 0) return Change.None;
        _active = true; _started = milliseconds; return Change.Started;
    }
    public void Reset() { _active = false; _blocked = true; }

    /// <summary>現在のキー状態を一括判定し、キーアップの取りこぼしを持ち越さない。</summary>
    public Change Update(bool leftControl, bool leftShift, bool otherKey, long milliseconds)
    {
        if (otherKey)
        {
            var wasActive = _active; _active = false; _blocked = true;
            return wasActive ? Change.Discarded : Change.None;
        }
        if (!leftControl || !leftShift)
        {
            var wasActive = _active; _active = false;
            if (!leftControl && !leftShift) _blocked = false;
            return wasActive ? milliseconds - _started >= 1000 ? Change.Accepted : Change.Discarded : Change.None;
        }
        if (_blocked || _active) return Change.None;
        _active = true; _started = milliseconds; return Change.Started;
    }
}

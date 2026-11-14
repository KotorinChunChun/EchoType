namespace EchoType.Core;

public static class DecisionTasks
{
    public const string Inquiry = "inquiry", Wake = "wake", Intent = "intent", Correction = "correction", Candidates = "candidates", Knowledge = "knowledge", Utterance = "utterance";
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [Inquiry] = "3軸評価", [Wake] = "呼びかけ分類", [Intent] = "指示・技の選択", [Correction] = "補正の要否",
        [Candidates] = "アプリ候補の選択", [Knowledge] = "ナレッジ候補の選択", [Utterance] = "発言の用途分類"
    };
}

public sealed record DecisionSettings
{
    public string FdsEnvFile { get; init; } = "";
    public string CloudflareEnvFile { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool FdsConnectionRequired { get; init; }
    public string Provider { get; init; } = "fds";
    public Dictionary<string, string> Overrides { get; init; } = [];
    public string Endpoint { get; init; } = "http://127.0.0.1:8767";
    public string ServerRoot { get; init; } = @"C:\develop\FastDecisionServer";
    public string Model { get; init; } = "jeff-qwen-2b";
    public string Device { get; init; } = "auto";
    public bool FallbackToLuna { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 20;
    public double MinimumConfidence { get; init; } = .85;
    public string CloudflareAccountId { get; init; } = "";
    public bool CloudflareEnabled { get; init; }
    public bool OpenAiEnabled { get; init; }
    public string OpenAiModel { get; init; } = "gpt-5-mini";
    public string For(string task) => Overrides.TryGetValue(task, out var value) && value != "inherit" ? value : Provider;
    public string ModelFor(string task) => For(task) switch { "clef" => "clef-flash", "openai" => OpenAiModel, "luna" => "gpt-6-luna", _ => Model };
    public bool Available(string task, bool codexEnabled) => For(task) switch { "fds" => true, "clef" => CloudflareEnabled, "openai" => OpenAiEnabled, _ => codexEnabled };
    public DecisionSettings Validate()
    {
        if (Provider is not ("fds" or "luna" or "clef" or "openai") || Overrides.Any(x => !DecisionTasks.Names.ContainsKey(x.Key) || x.Value is not ("inherit" or "fds" or "luna" or "clef" or "openai")))
            throw new InvalidOperationException("判定モデルの選択が不正です。");
        if (OpenAiModel.Length is < 1 or > 100 || OpenAiModel.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("OpenAI判定のモデルIDが不正です。");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("fdsの接続先は同じPCのHTTPアドレスとポートで指定してください。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 100 || Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) ||
            Device is not ("auto" or "cpu" or "cuda") || TimeoutSeconds is < 1 or > 120 || !double.IsFinite(MinimumConfidence) || MinimumConfidence is < .5 or > .99)
            throw new InvalidOperationException("fdsのモデル・デバイス・待ち時間・判定しきい値が不正です。");
        if (CloudflareAccountId.Length > 0 && (CloudflareAccountId.Length != 32 || CloudflareAccountId.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidOperationException("CloudflareアカウントIDは32桁の16進数で指定してください。");
        return this;
    }
}

public sealed record DecisionTrace(DateTimeOffset At, string Task, string Provider, string Model, string Device, double Seconds, bool Fallback, string Detail)
{
    public AiTokenUsage? Usage { get; init; }
    // 通常の診断JSONLは本文を持たない。暗号化された発言の履歴だけで使う。
    [System.Text.Json.Serialization.JsonIgnore] public string Prompt { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Response { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string ExchangeDetail => AiTokenUsage.Label(Usage) + "\n" + (Prompt.Length == 0 ? "送信なし（アルゴリズム）または旧記録" : "送信プロンプト\n" + Prompt + "\n\n応答原文\n" + Response);
}
public sealed record CorrectionOutcome(string Text, string Model, string Status)
{
    public AiTokenUsage? Usage { get; init; }
    public string Prompt { get; init; } = "";
    public string Response { get; init; } = "";
    public string ExchangeDetail => AiTokenUsage.Label(Usage) + "\n" + (Prompt.Length == 0 ? "補正生成プロンプト・応答は未記録（省略または旧記録）" : "送信プロンプト\n" + Prompt + "\n\n応答原文\n" + Response);
}

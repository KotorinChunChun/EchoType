namespace EchoType.Core;

/// <summary>操作画面のセッション内にだけ保持する送受信原文。</summary>
public sealed record ModelExchange(string Id, DateTimeOffset At, string Purpose, string Model, string State, string Prompt, string Response = "", double? Seconds = null)
{
    public string RequestId { get; init; } = "";
    public string PostId { get; init; } = "";
    public AiTokenUsage? Usage { get; init; }
    public string InputTokens => Usage?.InputTokens.ToString("N0") ?? "未取得";
    public string OutputTokens => Usage?.OutputTokens.ToString("N0") ?? "未取得";
    public string Time => At.ToOffset(TimeSpan.FromHours(9)).ToString("HH:mm:ss");
    public string Duration => Seconds?.ToString("F2") ?? "—";
    public string Detail => $"{Time} / {Purpose} / {Model} / {State} / {Duration}秒\nログID：{Id}\n投稿ID：{PostId}\n依頼ID：{RequestId}\n{AiTokenUsage.Label(Usage)}\n\n送信プロンプト：\n{Prompt}\n\n応答原文：\n{Response}";
}

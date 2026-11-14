using EchoType.Core;

namespace EchoType.Windows;

public sealed partial class CodexClient
{
    public static event Action<string, AiTokenUsage?>? TokenUsageReported;
    private readonly AsyncLocal<Action<ModelExchange>?> _exchangeObserver = new();
    internal Func<ModelExchange, CancellationToken, Task<string>>? RunForTest { get; set; }
    private sealed class ExchangeScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public IDisposable CaptureExchanges(Action<ModelExchange> observer, bool replace = false)
    {
        var previous = _exchangeObserver.Value; _exchangeObserver.Value = exchange => { try { if (!replace) previous?.Invoke(exchange); } finally { observer(exchange); } };
        return new ExchangeScope(() => _exchangeObserver.Value = previous);
    }
    internal void ReportExchange(ModelExchange exchange)
    {
        if (exchange.State != "送信中")
            try { TokenUsageReported?.Invoke(exchange.Purpose + " / " + exchange.Model + " / " + exchange.Id, exchange.Usage); } catch (Exception) { /* 表示通知で再送しない。 */ }
        try { _exchangeObserver.Value?.Invoke(exchange); }
        catch (Exception) { /* 画面の通知失敗で送信・操作を再実行しない。 */ }
    }
    private static readonly System.Text.Json.JsonSerializerOptions PromptJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string SerializePrompt(object? value) => System.Text.Json.JsonSerializer.Serialize(value, PromptJson);
}

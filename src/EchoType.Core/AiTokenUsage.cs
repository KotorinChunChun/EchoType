using System.Text.Json;

namespace EchoType.Core;

public sealed record AiTokenUsage(long InputTokens, long OutputTokens, long CachedInputTokens = 0)
{
    public string Display => $"入力 {InputTokens:N0} / 出力 {OutputTokens:N0} トークン（入力内キャッシュ {CachedInputTokens:N0}）";
    public static string Label(AiTokenUsage? value) => value?.Display ?? "入力・出力トークン：未取得";
    public static AiTokenUsage? Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        long? Read(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
        var input = Read("input_tokens") ?? Read("prompt_tokens"); var output = Read("output_tokens") ?? Read("completion_tokens");
        var cached = Read("cached_input_tokens") ?? 0;
        return input is { } i && output is { } o && cached <= i ? new(i, o, cached) : null;
    }
}

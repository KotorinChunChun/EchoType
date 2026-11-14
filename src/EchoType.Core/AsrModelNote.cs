namespace EchoType.Core;

public sealed record AsrModelNote(string EngineId, string Text, DateTimeOffset UpdatedAt)
{
    public static AsrEngine[] Models => [.. AsrEngines.All,
        new("gemini-api", "Gemini API（メモのみ）", true)];

    public void Validate()
    {
        // 廃止モデルの過去メモもバックアップに保持する。選択一覧とは別の互換判定。
        if (EngineId != "gemini-api") AsrEngines.Get(EngineId);
        if (Text is null || Text.Length > 20000)
            throw new InvalidOperationException("モデルメモは対応するモデルに20000文字以内で記録してください。");
    }
}

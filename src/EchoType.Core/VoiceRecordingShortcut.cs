namespace EchoType.Core;

public enum VoiceModifierSide { None, Left, Right, Either, Both }

public sealed record VoiceRecordingShortcut(VoiceModifierSide Control, VoiceModifierSide Shift,
    VoiceModifierSide Alt = VoiceModifierSide.None, VoiceModifierSide Windows = VoiceModifierSide.None, int MainKey = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<(string Name, int Left, int Right, VoiceModifierSide Side)> Modifiers =>
        [("Ctrl", 0xA2, 0xA3, Control), ("Shift", 0xA0, 0xA1, Shift),
         ("Alt", 0xA4, 0xA5, Alt), ("Windows", 0x5B, 0x5C, Windows)];

    public static VoiceRecordingShortcut FromModifiers(int[] keys)
    {
        VoiceModifierSide Side(int left, int right) => (keys.Contains(left), keys.Contains(right)) switch
        {
            (true, true) => VoiceModifierSide.Both,
            (true, false) => VoiceModifierSide.Left,
            (false, true) => VoiceModifierSide.Right,
            _ => VoiceModifierSide.None
        };
        return new(Side(0xA2, 0xA3), Side(0xA0, 0xA1), Side(0xA4, 0xA5), Side(0x5B, 0x5C));
    }

    public static string MainKeyName(int key) => key switch
    {
        >= 'A' and <= 'Z' => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x70 + 1}",
        _ => "なし"
    };

    public string Label()
    {
        var keys = Modifiers.Where(modifier => modifier.Side != VoiceModifierSide.None).Select(modifier => modifier.Side switch
        {
            VoiceModifierSide.Left => "左" + modifier.Name,
            VoiceModifierSide.Right => "右" + modifier.Name,
            VoiceModifierSide.Both => $"左右{modifier.Name}（両方）",
            _ => $"{modifier.Name}（左右どちらか）"
        });
        return string.Join("＋", MainKey == 0 ? keys : keys.Append(MainKeyName(MainKey)));
    }

    public void Validate()
    {
        if (Modifiers.Any(modifier => !Enum.IsDefined(modifier.Side)) || MainKey is not (0 or >= 'A' and <= 'Z' or >= 0x70 and <= 0x87))
            throw new InvalidOperationException("録音キーの指定を確認してください。");
        if (MainKey == 0 && Modifiers.All(modifier => modifier.Side == VoiceModifierSide.None))
            throw new InvalidOperationException("録音に使う修飾キーまたは主キーを選択してください。");
    }
}

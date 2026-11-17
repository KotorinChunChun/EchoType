namespace EchoType.Core;

/// <summary>設定した録音キーの完全一致で長押しを判定する。</summary>
public sealed class VoiceModifierGesture
{
    private VoiceRecordingShortcut _shortcut = new(VoiceModifierSide.Left, VoiceModifierSide.Left);
    private (string Name, int Left, int Right, VoiceModifierSide Side)[] _modifiers = [];
    private static readonly HashSet<int> ModifierKeyCodes = VoiceInputSettings.ModifierKeys.Select(key => key.Key).ToHashSet();
    private bool _active, _blocked = true;
    private long _started;
    public VoiceModifierGesture() => Configure(_shortcut);
    public void Configure(VoiceRecordingShortcut shortcut)
    {
        shortcut.Validate();
        _shortcut = shortcut;
        _modifiers = shortcut.Modifiers.ToArray();
        Reset();
    }
    public void Reset() { _active = false; _blocked = true; }
    public VoiceInputGesture.Change Update(Func<int, bool> down, long now)
    {
        var any = _shortcut.MainKey != 0 && down(_shortcut.MainKey);
        var all = _shortcut.MainKey == 0 || any;
        var other = false;
        foreach (var (_, left, right, side) in _modifiers)
        {
            var leftDown = down(left);
            var rightDown = down(right);
            var allowLeft = side is VoiceModifierSide.Left or VoiceModifierSide.Either or VoiceModifierSide.Both;
            var allowRight = side is VoiceModifierSide.Right or VoiceModifierSide.Either or VoiceModifierSide.Both;
            any |= leftDown && allowLeft || rightDown && allowRight;
            other |= leftDown && !allowLeft || rightDown && !allowRight;
            all &= side switch
            {
                VoiceModifierSide.Left => leftDown,
                VoiceModifierSide.Right => rightDown,
                VoiceModifierSide.Either => leftDown || rightDown,
                VoiceModifierSide.Both => leftDown && rightDown,
                _ => true
            };
        }
        for (var key = 8; !other && key <= 0xFE; key++)
        {
            // VK_DBE_ALPHANUMERIC～VK_DBE_DBCSCHARは、IMEのモードが切り替わるまで押下中として残ることがある。
            if (key is >= 0xF0 and <= 0xF4) continue;
            if (key is 0x10 or 0x11 or 0x12 || key == _shortcut.MainKey || ModifierKeyCodes.Contains(key)) continue;
            if (down(key)) { other = true; break; }
        }
        if (other) { var active = _active; _active = false; _blocked = true; return active ? VoiceInputGesture.Change.Discarded : VoiceInputGesture.Change.None; }
        if (!all)
        {
            var active = _active;
            _active = false;
            if (active || !any) _blocked = any;
            return active ? now - _started >= 1000 ? VoiceInputGesture.Change.Accepted : VoiceInputGesture.Change.Discarded : VoiceInputGesture.Change.None;
        }
        if (_active || _blocked) return VoiceInputGesture.Change.None;
        _active = true; _started = now; return VoiceInputGesture.Change.Started;
    }
}

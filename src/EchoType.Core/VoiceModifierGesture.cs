namespace EchoType.Core;

/// <summary>設定した左右別修飾キーの完全一致で長押しを判定する。</summary>
public sealed class VoiceModifierGesture
{
    private int[] _keys = [0xA2, 0xA0];
    private bool _active, _blocked = true;
    private long _started;
    public void Configure(int[] keys) { _keys = keys.ToArray(); Reset(); }
    public void Reset() { _active = false; _blocked = true; }
    public VoiceInputGesture.Change Update(Func<int, bool> down, long now)
    {
        var any = _keys.Any(down); var all = _keys.All(down); var other = false;
        for (var key = 8; key <= 0xE2; key++)
        {
            if (key is 0x10 or 0x11 or 0x12 || _keys.Contains(key)) continue;
            if (down(key)) { other = true; break; }
        }
        if (other) { var active = _active; _active = false; _blocked = true; return active ? VoiceInputGesture.Change.Discarded : VoiceInputGesture.Change.None; }
        if (!all)
        {
            var active = _active; _active = false; if (!any) _blocked = false;
            return active ? now - _started >= 1000 ? VoiceInputGesture.Change.Accepted : VoiceInputGesture.Change.Discarded : VoiceInputGesture.Change.None;
        }
        if (_active || _blocked) return VoiceInputGesture.Change.None;
        _active = true; _started = now; return VoiceInputGesture.Change.Started;
    }
}

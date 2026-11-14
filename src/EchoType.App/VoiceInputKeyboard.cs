using EchoType.Core;
using System.Runtime.InteropServices;

namespace EchoType.App;

/// <summary>UIスレッドの処理時間に依存せず、現在の左右キー状態を監視する。</summary>
internal sealed class VoiceInputKeyboard : IDisposable
{
    private readonly VoiceModifierGesture _gesture = new();
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly Func<int, bool> _isDown;
    public event Action<VoiceInputGesture.Change, VoiceInputTarget.FocusStamp>? Changed;
    public event Action? EscapePressed;
    public VoiceInputKeyboard(Func<int, bool>? isDown = null, int[]? keys = null)
    {
        _isDown = isDown ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
        _gesture.Configure(keys ?? [0xA2, 0xA0]);
        _thread = new Thread(Watch) { IsBackground = true, Name = "音声入力キー監視" };
        _thread.Start();
    }
    private void Watch()
    {
        var escaped = false;
        while (!_stop.Wait(10))
        {
            var escape = _isDown(0x1B);
            if (escape && !escaped) EscapePressed?.Invoke();
            escaped = escape;
            VoiceInputGesture.Change change;
            lock (_sync) change = _gesture.Update(_isDown, Environment.TickCount64);
            if (change != VoiceInputGesture.Change.None)
                Changed?.Invoke(change, change == VoiceInputGesture.Change.Started ? VoiceInputTarget.CaptureFocusStamp() : default);
        }
    }
    public void Reset() { lock (_sync) _gesture.Reset(); }
    public void Configure(int[] keys) { lock (_sync) _gesture.Configure(keys); }
    public void Dispose() { _stop.Set(); _thread.Join(); _stop.Dispose(); }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

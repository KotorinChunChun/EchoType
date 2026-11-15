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
    private bool _paused, _escaped;
    public event Action<VoiceInputGesture.Change, VoiceInputTarget.FocusStamp>? Changed;
    public event Action? EscapePressed;
    public VoiceInputKeyboard(VoiceRecordingShortcut shortcut, Func<int, bool>? isDown = null)
    {
        _isDown = isDown ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
        _gesture.Configure(shortcut);
        _thread = new Thread(Watch) { IsBackground = true, Name = "音声入力キー監視" };
        _thread.Start();
    }
    private void Watch()
    {
        while (!_stop.Wait(10))
        {
            lock (_sync)
            {
                if (_paused) continue;
                var escape = _isDown(0x1B);
                if (escape && !_escaped) EscapePressed?.Invoke();
                _escaped = escape;
                var change = _gesture.Update(_isDown, Environment.TickCount64);
                if (change != VoiceInputGesture.Change.None)
                    Changed?.Invoke(change, change == VoiceInputGesture.Change.Started ? VoiceInputTarget.CaptureFocusStamp() : default);
            }
        }
    }
    public void Reset() { lock (_sync) _gesture.Reset(); }
    public void Configure(VoiceRecordingShortcut shortcut) { lock (_sync) _gesture.Configure(shortcut); }
    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _paused = paused;
            _gesture.Reset();
            if (!paused) _escaped = _isDown(0x1B);
        }
    }
    public void Dispose() { _stop.Set(); _thread.Join(); _stop.Dispose(); }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace EchoType.App;

public partial class VoiceInputWindow
{
    public void FocusDraft()
    {
        if (!IsVisible || SettingsOpen) return;
        var handle = new WindowInteropHelper(this).Handle;
        _controller.RecordEditorFocus(handle);
        Activate();
        // WPF内の論理フォーカスだけでは外部アプリへのキー配送が止まらない。
        if (!ActivateNativeWindow(handle)) { SetStatus("音声入力画面をアクティブにできません。編集欄をクリックしてください。"); return; }
        Editor.Focus(); Keyboard.Focus(Editor);
    }
    internal static bool ActivateNativeWindow(nint handle)
    {
        if (GetForegroundWindow() == handle) return true;
        SetForegroundWindow(handle);
        if (GetForegroundWindow() != handle)
        {
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try { SetForegroundWindow(handle); SetActiveWindow(handle); }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        }
        return GetForegroundWindow() == handle;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
}

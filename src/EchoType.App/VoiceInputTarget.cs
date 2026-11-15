using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace EchoType.App;

/// <summary>同一要素と文字範囲を保持する。表示パスを使った再検索は行わない。</summary>
public sealed partial class VoiceInputTarget
{
    private AutomationElement _element;
    private TextPattern? _pattern;
    private ValuePattern? _value;
    private TextPatternRange? _range;
    private readonly EmEditorTarget? _emEditor;
    private readonly string _original;
    private bool _preservePoint;
    private string _prefix = "", _suffix = "";
    private readonly string _automationId, _className;
    private readonly ControlType _controlType;
    private int[] _runtimeId;
    private readonly AutomationElement? _identityParent;
    private readonly int[]? _identityParentId;
    private readonly string _frameworkId;
    private readonly int _pid;
    private readonly DateTime _processStart;
    private readonly nint _window;
    private readonly nint _nativeWindow;
    private int _consumed;
    private Action<string, nint>? _focusLog;
    internal bool CanReturnFocus => IsOwnOrExpectedForeground(_window);
    internal void SetFocusLog(Action<string, nint> log) => _focusLog = log;
    private void LogFocus(string stage) => _focusLog?.Invoke(stage, _nativeWindow != 0 ? _nativeWindow : _window);
    private void RequireInputForeground()
    {
        if (GetForegroundWindow() == _window) return;
        LogFocus("blocked-foreground-changed");
        throw new InvalidOperationException("別のアプリへ移動したため入力を停止しました。入力先を確認して決定してください。");
    }
    public string ApplicationName { get; }
    public string ApplicationPath { get; }
    public static string ApplicationForWindow(nint window)
    {
        try { GetWindowThreadProcessId(window, out var pid); using var process = Process.GetProcessById((int)pid); return process.MainModule?.FileName ?? ""; }
        catch { return ""; }
    }
    public string UiaPath { get; }
    public bool Consumed => Volatile.Read(ref _consumed) != 0;
    public bool WriteVerified { get; private set; }
    public bool CaretPlaced { get; private set; }
    public string? VerifiedText { get; private set; }
    private DateTimeOffset _verifiedAt;
    private int _undoStarted;
    public Rect HighlightBounds()
    {
        if (Consumed || !IsWindow(_window) || IsIconic(_window)) return Rect.Empty;
        // 枠の監視は選択範囲・書込み用の参照を変更しない。
        var element = _emEditor is null ? FindCurrentElement() : _element;
        return element.Current.IsOffscreen ? Rect.Empty : element.Current.BoundingRectangle;
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    public readonly record struct FocusStamp(nint Window, nint Focus, NativeRect Caret)
    {
        public int ProcessId { get; init; }
        public uint ThreadId { get; init; }
    }
    internal static nint ForegroundWindow => GetForegroundWindow();
    internal static bool IsOwnOrExpectedForeground(nint expected) => IsOwnOrExpectedWindow(GetForegroundWindow(), expected);
    private static bool IsOwnOrExpectedWindow(nint current, nint expected)
    {
        GetWindowThreadProcessId(current, out var pid);
        return current != 0 && (current == expected || pid == Environment.ProcessId);
    }
    [StructLayout(LayoutKind.Sequential)] public record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    public static FocusStamp CaptureFocusStamp()
    {
        var window = GetForegroundWindow(); var thread = GetWindowThreadProcessId(window, out var pid);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (pid == Environment.ProcessId || !GetGUIThreadInfo(thread, ref info)) return default;
        return new(window, info.Focus, info.CaretRect) { ProcessId = pid, ThreadId = thread };
    }
    public static bool ExternalForeground
    {
        get { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid != 0 && pid != Environment.ProcessId; }
    }
    private VoiceInputTarget(AutomationElement element, TextPattern? pattern, TextPatternRange? range, EmEditorTarget? emEditor = null)
    {
        _nativeWindow = (nint)element.Current.NativeWindowHandle;
        _element = element; _pattern = pattern; _range = range?.Clone(); _emEditor = emEditor;
        _value = GetValue(element);
        _automationId = element.Current.AutomationId; _className = element.Current.ClassName; _controlType = element.Current.ControlType;
        _frameworkId = element.Current.FrameworkId;
        _identityParent = TreeWalker.ControlViewWalker.GetParent(element);
        _identityParentId = _identityParent?.GetRuntimeId();
        _pid = element.Current.ProcessId;
        using var process = Process.GetProcessById(_pid);
        _processStart = process.StartTime;
        ApplicationName = process.ProcessName;
        try { ApplicationPath = process.MainModule?.FileName ?? ""; } catch { ApplicationPath = ""; }
        _runtimeId = element.GetRuntimeId();
        var parts = new List<string>();
        var current = element;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            var info = current.Current;
            if (info.ProcessId != _pid) break;
            var id = info.AutomationId;
            parts.Insert(0, info.ControlType.ProgrammaticName.Replace("ControlType.", "") +
                (id.Length > 0 ? $"[AutomationId={id}]" : $"[Name={info.Name[..Math.Min(info.Name.Length, 80)]}]"));
            if (info.NativeWindowHandle != 0) _window = GetAncestor((nint)info.NativeWindowHandle, 2);
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }
        UiaPath = "UIA: /" + string.Join("/", parts) + " / RuntimeId=" + string.Join(".", _runtimeId);
        if (_window == 0) throw new InvalidOperationException("入力先ウィンドウを特定できません。");
        _original = ReadDocument();
        if (_emEditor is not null)
        {
            var (start, end) = _emEditor.ReadSelection(_original.Length);
            _prefix = _original[..start]; _suffix = _original[end..];
            UiaPath += $" / Win32: EmEditorView / Document={_emEditor.Document:X}";
            return;
        }
        // 擬似要素のヒント範囲は実際の入力位置ではない。空欄にはValuePatternで入力する。
        if (_original.Length == 0 && _value is { Current.IsReadOnly: false }) { _range = null; return; }
        if (_pattern is null || _range is null) { _prefix = _original; return; }
        var before = _pattern.DocumentRange.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, _range, TextPatternRangeEndpoint.Start);
        _prefix = before.GetText(200001);
        var after = _pattern.DocumentRange.Clone();
        after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, _range, TextPatternRangeEndpoint.End);
        _suffix = after.GetText(200001);
        if (_prefix + _range.GetText(200001) + _suffix != _original)
            throw new InvalidOperationException("入力先の文字位置を正確に保持できません。");
    }
    public static VoiceInputTarget CaptureFocused() => CaptureFocused(CaptureFocusStamp());
    private static bool SameFocus(FocusStamp a, FocusStamp b) => a.Window == b.Window && a.Focus == b.Focus;
    public static VoiceInputTarget CaptureFocused(FocusStamp stamp)
    {
        if (stamp.Window == 0 || !SameFocus(stamp, CaptureFocusStamp()) || !ExternalForeground) throw new InvalidOperationException("入力開始時のフォーカスが変わりました。外部アプリの入力欄で設定した録音キーを押してください。");
        var emEditor = EmEditorTarget.TryCreateFocused(stamp.Focus, out var nativeFocus);
        var target = emEditor is not null ? new VoiceInputTarget(nativeFocus!, null, null, emEditor)
            : CaptureNear(AutomationElement.FocusedElement ?? throw new InvalidOperationException("入力欄がありません。"), null);
        if (!SameFocus(stamp, CaptureFocusStamp()))
            throw new InvalidOperationException("入力先の取得中にフォーカスが変わりました。ターゲットを指定し直してください。");
        return target;
    }
    internal static VoiceInputTarget CaptureRemembered(FocusStamp stamp)
    {
        if (!IsOwnOrExpectedForeground(stamp.Window)) throw new InvalidOperationException("別のアプリへ移動しました。ターゲットを指定し直してください。");
        var thread = GetWindowThreadProcessId(stamp.Window, out var pid);
        if (stamp.Window == 0 || stamp.Focus == 0 || !IsWindow(stamp.Focus) ||
            GetAncestor(stamp.Focus, 2) != stamp.Window || pid != stamp.ProcessId || thread != stamp.ThreadId)
            throw new InvalidOperationException("保持した入力先が変わりました。ターゲットを指定し直してください。");
        if (SameFocus(stamp, CaptureFocusStamp())) return CaptureFocused(stamp);
        // 画面の表示を明示したときだけ、直前のネイティブ入力欄を読む。監視からは呼ばない。
        return CaptureNear(AutomationElement.FromHandle(stamp.Focus), null);
    }
    public static VoiceInputTarget CapturePoint(Point point)
    {
        return CaptureNear(AutomationElement.FromPoint(point), point);
    }
    private static TextPattern? RequireEditable(AutomationElement element)
    {
        var info = element.Current;
        if (info.ProcessId == Environment.ProcessId || info.IsPassword || !info.IsEnabled || !info.IsKeyboardFocusable)
            throw new InvalidOperationException("この入力欄は音声入力の対象にできません。");
        var writableValue = GetValue(element) is { Current.IsReadOnly: false };
        var pattern = element.TryGetCurrentPattern(TextPattern.Pattern, out var value) ? (TextPattern)value : null;
        if (!writableValue && (pattern is null || pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false))
            throw new InvalidOperationException("編集可能な文字範囲を取得できません。別の入力欄を指定してください。");
        return writableValue && pattern?.SupportedTextSelection == SupportedTextSelection.None ? null : pattern;
    }
    private string ReadDocument()
    {
        if (_emEditor is not null) return _emEditor.ReadText();
        var text = _pattern?.DocumentRange.GetText(200001) ?? _value?.Current.Value ?? throw new InvalidOperationException("入力先の本文を取得できません。");
        if (text.Length > 200000) throw new InvalidOperationException("20万文字を超える入力欄は対象外です。");
        return IsDisplayedPlaceholder(text) ? "" : text;
    }
    public void Validate()
    {
        if (Consumed) throw new InvalidOperationException("この入力先への送信は実行済みです。入力先を取り直してください。");
        using var process = Process.GetProcessById(_pid);
        GetWindowThreadProcessId(_window, out var owner);
        if (process.HasExited || process.StartTime != _processStart || owner != _pid || !IsWindow(_window)) throw new InvalidOperationException("保持した入力先が終了または変更されました。");
        ResolveElement();
        if (_emEditor is null) RequireEditable(_element); else _emEditor.Validate();
        if (ReadDocument() != _original) throw new InvalidOperationException("入力先の本文が変更されています。ターゲットを指定し直してください。");
        if (_pattern is null || _range is null) return;
        try
        {
            var before = _pattern.DocumentRange.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, _range, TextPatternRangeEndpoint.Start);
            var after = _pattern.DocumentRange.Clone();
            after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, _range, TextPatternRangeEndpoint.End);
            if (before.GetText(200001) != _prefix || after.GetText(200001) != _suffix) UseDocumentEnd();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { UseDocumentEnd(); }
    }
    private static readonly int[] InputGuardKeys = [0x0D, 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01];
    private static bool InputKeysHeld(int mainKey)
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        return InputGuardKeys.Any(Down) || (mainKey != 0 && Down(mainKey));
    }
    public async Task<string> InsertAsync(string text, CancellationToken ct = default, bool replace = false, int mainKey = 0)
    {
        if ((!replace && string.IsNullOrWhiteSpace(text)) || text.Length > 16000 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("入力文は1〜16000文字です。制御文字は入力できません。");
        var initialForeground = GetForegroundWindow();
        if (!IsOwnOrExpectedWindow(initialForeground, _window))
        {
            LogFocus("blocked-before-input");
            throw new InvalidOperationException("別のアプリへ移動したため入力を停止しました。");
        }
        if (replace || _preservePoint) Validate(); else ReadCurrentSelection();
        ct.ThrowIfCancellationRequested();
        // 決定用のキーと録音キーを離してから入力先へ戻す。
        for (var attempt = 0; attempt < 150 && InputKeysHeld(mainKey); attempt++) await Task.Delay(20, ct);
        RequireInputKeysReleased();
        // UI Automationの再取得中に指定先自身が前面に戻る場合がある。第三の窓への移動は中止する。
        var foreground = GetForegroundWindow();
        if (foreground != initialForeground && foreground != _window)
        {
            LogFocus("blocked-activation");
            throw new InvalidOperationException("入力の準備中に前面が変わったため停止しました。");
        }
        LogFocus("activate-root");
        ShowWindow(_window, 9);
        if (GetForegroundWindow() != _window && !SetForegroundWindow(_window)) throw new InvalidOperationException("入力先を前面に戻せません。入力先を表示してから決定してください。");
        RequireInputForeground();
        if (_emEditor is not null)
        {
            var start = replace ? 0 : SelectionStart;
            var end = replace ? _original.Length : SelectionStart + SelectionLength;
            RequireInputForeground(); _emEditor.Select(start, end);
            Validate(); ct.ThrowIfCancellationRequested();
            if (_emEditor.ReadSelection(_original.Length) != (start, end))
                throw new InvalidOperationException("EmEditorの文字位置を復元できなかったため入力しませんでした。");
            RequireInputKeysReleased();
            if (Interlocked.Exchange(ref _consumed, 1) != 0) throw new InvalidOperationException("二重送信を停止しました。");
            RequireInputForeground(); _emEditor.ReplaceSelection(text);
            return await VerifyAsync(replace ? text : _prefix + text + _suffix, replace ? text : _prefix + text, mainKey);
        }
        if (replace && _value is { Current.IsReadOnly: false } || _pattern is null || _range is null)
        {
            if (_value is not { Current.IsReadOnly: false }) throw new InvalidOperationException("本文を書き込める入力欄を取得できません。");
            FocusForInput();
            Validate(); ct.ThrowIfCancellationRequested();
            RequireInputKeysReleased();
            if (Interlocked.Exchange(ref _consumed, 1) != 0) throw new InvalidOperationException("二重送信を停止しました。");
            RequireInputForeground(); _value.SetValue(replace ? text : _original + text);
            return await VerifyAsync(replace ? text : _original + text, replace ? text : _original + text, mainKey);
        }
        FocusForInput();
        Validate(); // フォーカス取得で再描画される編集欄も、選択範囲を使う前に取り直す。
        var insertion = replace ? _pattern.DocumentRange.Clone() : _range;
        RequireInputForeground(); insertion.Select();
        await Task.Delay(100, ct);
        Validate();
        var selected = _pattern.GetSelection();
        if (GetForegroundWindow() != _window || !Automation.Compare(_element, AutomationElement.FocusedElement) ||
            selected.Length != 1 || !selected[0].Compare(insertion))
            throw new InvalidOperationException("入力先のフォーカス・文字位置を復元できなかったため入力しませんでした。");
        RequireInputKeysReleased();
        if (Interlocked.Exchange(ref _consumed, 1) != 0) throw new InvalidOperationException("二重送信を停止しました。");
        // Unicode文字として一括送信する。Enterキーやアプリの送信操作は実行しない。
        var inputs = text.Length == 0 && replace ? new[] { Input.KeyPress(0x08, false), Input.KeyPress(0x08, true) }
            : text.SelectMany(c => new[] { Input.Unicode(c, false), Input.Unicode(c, true) }).ToArray();
        RequireInputForeground();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            return "入力結果を確認できません。自動再送はしません。対象の本文を確認してください。";
        return await VerifyAsync(replace ? text : _prefix + text + _suffix, replace ? text : _prefix + text, mainKey);

        void RequireInputKeysReleased()
        {
            ct.ThrowIfCancellationRequested();
            if (InputKeysHeld(mainKey)) throw new InvalidOperationException("録音キーとマウスボタンを離してから決定してください。");
        }
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    public async Task<VoiceInputTarget> UndoAsync(CancellationToken ct = default, int mainKey = 0)
    {
        if (!WriteVerified || VerifiedText is null || DateTimeOffset.UtcNow - _verifiedAt > TimeSpan.FromMinutes(2) || _original.Length > 16000)
            throw new InvalidOperationException("取り消せるのは、2分以内の確認済み入力（元の本文16000文字以内）です。");
        using var process = Process.GetProcessById(_pid);
        GetWindowThreadProcessId(_window, out var owner);
        if (process.HasExited || process.StartTime != _processStart || owner != _pid || !IsWindow(_window))
            throw new InvalidOperationException("入力先が変わりました。");
        ResolveElement(restoreSelection: false);
        if (_emEditor is null) RequireEditable(_element); else _emEditor.Validate();
        if (Normalize(ReadDocument()) != Normalize(VerifiedText)) throw new InvalidOperationException("入力後に本文が変更されています。");
        var target = TryCapture(_element, null, []) ?? throw new InvalidOperationException("入力欄を取得できません。");
        if (Normalize(target.OriginalText) != Normalize(VerifiedText)) throw new InvalidOperationException("本文が変更されています。");
        if (_focusLog is not null) target.SetFocusLog(_focusLog);
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _undoStarted, 1, 0) != 0) throw new InvalidOperationException("取り消しは実行済みです。");
        string result;
        try { result = await target.InsertAsync(_original, ct, replace: true, mainKey: mainKey); }
        catch { if (!target.Consumed) Interlocked.Exchange(ref _undoStarted, 0); throw; }
        if (!target.WriteVerified) throw new InvalidOperationException(result);
        return target.RefreshSnapshot();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input
    {
        public uint Type; public InputUnion Data;
        public static Input Unicode(char c, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = (uint)(4 | (up ? 2 : 0)) } } };
        public static Input KeyPress(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } };
    }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}

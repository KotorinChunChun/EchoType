using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace EchoType.App;

public sealed partial class VoiceInputTarget
{
    /// <summary>UIAの編集パターンを公開しないEmEditor本文。文字ポインタは標準メッセージだけで渡す。</summary>
    private sealed class EmEditorTarget
    {
        // Emurasoft公式plugin.h: EE_FIRST=WM_USER+0x400、EE_INFO=EE_FIRST+58。
        // EE_INFOではポインタを渡さない照会だけを使用する。
        private const uint EeInfo = 0x800 + 58;
        private const int GetActiveDocument = 325, GetReadOnly = 296;
        private readonly nint _view, _root;
        private readonly int _pid;
        public nint Document { get; }

        private EmEditorTarget(nint view, int pid)
        {
            _view = view; _pid = pid; _root = GetAncestor(view, 2);
            Document = Message(EeInfo, GetActiveDocument);
            if (Document == 0) throw new InvalidOperationException("EmEditorの文書を識別できません。");
            Validate();
        }

        public static EmEditorTarget? TryCreate(AutomationElement element)
        {
            var info = element.Current;
            if (info.ClassName != "EmEditorView" || info.NativeWindowHandle == 0 || info.ProcessId == Environment.ProcessId || info.IsPassword) return null;
            using var process = Process.GetProcessById(info.ProcessId);
            if (!string.Equals(process.ProcessName, "EmEditor", StringComparison.OrdinalIgnoreCase)) return null;
            return new((nint)info.NativeWindowHandle, info.ProcessId);
        }

        public static EmEditorTarget? TryCreateFocused(nint window, out AutomationElement? element)
        {
            element = null;
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            if (name.ToString() != "EmEditorView") return null;
            element = AutomationElement.FromHandle(window); return TryCreate(element);
        }

        public void Validate()
        {
            GetWindowThreadProcessId(_view, out var pid);
            var name = new StringBuilder(256); GetClassName(_view, name, name.Capacity);
            if (!IsWindow(_view) || pid != _pid || GetAncestor(_view, 2) != _root || name.ToString() != "EmEditorView" || !IsWindowEnabled(_view))
                throw new InvalidOperationException("保持したEmEditorの入力欄が変更されました。");
            if (Message(EeInfo, GetActiveDocument) != Document)
                throw new InvalidOperationException("EmEditorの文書が切り替わりました。ターゲットを指定し直してください。");
            if (Message(EeInfo, GetReadOnly) != 0)
                throw new InvalidOperationException("EmEditorの文書は読み取り専用です。");
        }

        public string ReadText()
        {
            Validate();
            var length = Message(0x000E).ToInt64();
            if (length < 0 || length > 200000) throw new InvalidOperationException("20万文字を超える入力欄は対象外です。");
            var buffer = new StringBuilder((int)length + 1);
            Check(SendTextRead(_view, 0x000D, buffer.Capacity, buffer, 2, 1000, out var copied));
            // EmEditorはWM_GETTEXTの戻り値に終端NULを含める版がある（実機ではlength+1）。
            if ((copied.ToInt64() != length && copied.ToInt64() != length + 1) || buffer.Length != length || Message(0x000E).ToInt64() != length)
                throw new InvalidOperationException("EmEditorの本文が取得中に変化しました。ターゲットを指定し直してください。");
            Validate(); return buffer.ToString();
        }

        public (int Start, int End) ReadSelection(int textLength)
        {
            Validate();
            var type = (int)Message(0x800 + 45);
            if ((type & 0x0080) != 0 || (type & 0x000f) > 2)
                throw new InvalidOperationException("EmEditorの複数選択・矩形選択・セル選択には対応していません。通常の文字位置を指定してください。");
            // 戻り値の16ビット詰め合わせは使わず、65535文字以降も32ビットで取得する。
            uint start = uint.MaxValue, end = uint.MaxValue;
            Check(SendSelection(_view, 0x00B0, ref start, ref end, 2, 1000, out _));
            Validate();
            return start <= end && end <= textLength ? ((int)start, (int)end) : (textLength, textLength);
        }

        public void Select(int start, int end) { Validate(); Message(0x00B1, start, end); }

        public void ReplaceSelection(string text)
        {
            Validate();
            // EM_REPLACESELはWindowsがプロセス間のUTF-16を転送する。Undo履歴を残す。
            Check(SendTextWrite(_view, 0x00C2, 1, text, 2, 1000, out _));
        }

        private nint Message(uint message, nint wParam = 0, nint lParam = 0)
        {
            Check(SendMessageTimeout(_view, message, wParam, lParam, 2, 1000, out var result)); return result;
        }
        private static void Check(nint result)
        {
            if (result == 0) throw new InvalidOperationException("EmEditorが応答しないか、アクセスできません。入力結果が不明な場合は自動再送しません。");
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int size);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendTextRead(nint window, uint message, nint size, StringBuilder text, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendTextWrite(nint window, uint message, nint undo, string text, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendSelection(nint window, uint message, ref uint start, ref uint end, uint flags, uint timeout, out nint result);
    }
}

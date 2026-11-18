using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace EchoType.App;

public sealed partial class VoiceInputTarget
{
    private async Task<string> VerifyAsync(string expectedText, string caretPrefix, int mainKey)
    {
        var expected = Normalize(expectedText);
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (GetForegroundWindow() != _window)
            {
                LogFocus("verification-foreground-changed");
                return "入力後に別のアプリへ移動しました。入力結果は対象の本文で確認してください。自動再送はしません。";
            }
            try
            {
                // Web入力欄は書き込みに伴って要素・パターンを再生成することがある。
                ResolveElement(restoreSelection: false);
                var actual = ReadDocument();
                // TextPatternの段落表現とValuePatternの値が異なる入力欄でも、
                // 書き込み対象そのもののValueが完全一致する場合は確認できる。空白は削らない。
                if (Normalize(actual) != expected && (_value is null || Normalize(_value.Current.Value) != expected)) continue;
                WriteVerified = true; VerifiedText = actual; _verifiedAt = DateTimeOffset.UtcNow;
                try { CaretPlaced = await MoveCaretAsync(caretPrefix, actual, mainKey); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException) { CaretPlaced = false; }
                return CaretPlaced ? "保持した位置への入力を確認しました。"
                    : "本文の入力を確認しました。カーソル位置は移動できませんでした。";
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
        }
        return "文字を送信しましたが結果の一致を確認できません。自動再送はしません。対象の本文を確認してください。";
    }

    private async Task<bool> MoveCaretAsync(string prefix, string actual, int mainKey)
    {
        if (GetForegroundWindow() != _window) return false;
        if (_emEditor is not null)
        {
            var offset = CaretOffset(actual, prefix);
            _emEditor.Select(offset, offset);
            return _emEditor.ReadSelection(actual.Length) == (offset, offset);
        }
        RequireInputForeground(); LogFocus("caret-set-focus"); _element.SetFocus();
        if (HasNativeEditFocus() && TryMoveNativeEditCaret(prefix))
            return true;
        var normalizedPrefix = Normalize(prefix);
        if (_pattern is not null)
        {
            // UIAのCharacterはUTF-16の文字数と一致しない（改行・絵文字等）。
            // 実際の範囲本文を照合して挿入末尾を探し、選択を解除する。
            var document = _pattern.DocumentRange;
            var low = 0; var high = actual.Length;
            while (low <= high)
            {
                var units = low + (high - low) / 2;
                var range = document.Clone();
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, units);
                var before = Normalize(range.GetText(200001));
                if (before == normalizedPrefix)
                {
                    range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                    RequireInputForeground(); range.Select();
                    RequireInputForeground(); range.ScrollIntoView(false);
                    var selection = _pattern.GetSelection();
                    return selection.Length == 1 && selection[0].Compare(range);
                }
                if (before.Length < normalizedPrefix.Length) low = units + 1; else high = units - 1;
            }
            return false;
        }
        if (Normalize(actual) != normalizedPrefix) return false;
        // ValuePatternだけの欄は全文置換・末尾追記のみ。対象フォーカスを照合してCtrl+End。
        if (!Automation.Compare(_element, AutomationElement.FocusedElement) || InputKeysHeld(mainKey)) return false;
        var inputs = new[] { Input.KeyPress(0x11, false), Input.KeyPress(0x23, false), Input.KeyPress(0x23, true), Input.KeyPress(0x11, true) };
        RequireInputForeground();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            var release = new[] { Input.KeyPress(0x23, true), Input.KeyPress(0x11, true) };
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            return false;
        }
        await Task.Delay(50);
        return GetForegroundWindow() == _window && Automation.Compare(_element, AutomationElement.FocusedElement);
    }

    private bool HasNativeEditFocus() => CaptureFocusStamp().Focus == _nativeWindow && IsNativeEdit();

    private bool IsNativeEdit()
    {
        if (_nativeWindow == 0 || _nativeWindow != (nint)_element.Current.NativeWindowHandle) return false;
        var name = new StringBuilder(32);
        return GetClassName(_nativeWindow, name, name.Capacity) != 0 &&
            string.Equals(name.ToString(), "Edit", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryMoveNativeEditCaret(string prefix)
    {
        RequireInputForeground();
        const uint abortIfHung = 2, getTextLength = 0x000E, getText = 0x000D, setSelection = 0xB1, getSelection = 0xB0, scrollCaret = 0xB7;
        if (SendMessageTimeout(_nativeWindow, getTextLength, 0, 0, abortIfHung, 250, out var length) == 0 || length > 200000)
            return false;
        var buffer = new StringBuilder((int)length + 1);
        if (ReadNativeText(_nativeWindow, getText, buffer.Capacity, buffer, abortIfHung, 250, out _) == 0)
            return false;
        var offset = CaretOffset(buffer.ToString(), prefix);
        if (offset >= ushort.MaxValue) return false;
        RequireInputForeground();
        if (SendMessageTimeout(_nativeWindow, setSelection, offset, offset, abortIfHung, 250, out _) == 0 ||
            SendMessageTimeout(_nativeWindow, getSelection, 0, 0, abortIfHung, 250, out var selection) == 0 ||
            selection != (nuint)(offset | offset << 16)) return false;
        RequireInputForeground();
        SendMessageTimeout(_nativeWindow, scrollCaret, 0, 0, abortIfHung, 250, out _);
        return CaptureFocusStamp().Focus == _nativeWindow;
    }

    private static int CaretOffset(string actual, string prefix)
    {
        var normalized = Normalize(prefix);
        var offset = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (offset >= actual.Length) throw new InvalidOperationException("入力後のカーソル位置を確認できません。");
            var c = actual[offset++];
            if (c == '\r')
            {
                if (offset < actual.Length && actual[offset] == '\n') offset++;
                c = '\n';
            }
            if (c != normalized[i]) throw new InvalidOperationException("入力後のカーソル位置を確認できません。");
        }
        return offset;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint milliseconds, out nuint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint SendNativeText(nint window, uint message, nint wParam, string text, uint flags, uint milliseconds, out nuint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint ReadNativeText(nint window, uint message, nint capacity, StringBuilder text, uint flags, uint milliseconds, out nuint result);
}

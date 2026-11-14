using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace EchoType.App;

public sealed partial class VoiceInputTarget
{
    public VoiceInputTarget RefreshSnapshot()
    {
        ResolveElement();
        if (_emEditor is not null)
        {
            var refreshed = new VoiceInputTarget(_element, null, null, _emEditor);
            if (refreshed._original == _original) { refreshed._prefix = _prefix; refreshed._suffix = _suffix; }
            return refreshed;
        }
        if (ReadDocument() == _original) return new(_element, _pattern, _range);
        return TryCapture(_element, null, []) ?? throw new InvalidOperationException("現在の入力欄を読み取れません。");
    }
    private void ReadCurrentSelection()
    {
        // 決定直前の対象側の選択を読む。本文変更の検査は維持し、起動時の選択は使わない。
        Validate();
        var current = TryCapture(_element, null, []) ?? throw new InvalidOperationException("現在の入力位置を読み取れません。");
        if (current._original != _original) throw new InvalidOperationException("入力先の本文が変更されています。ターゲットを指定し直してください。");
        _pattern = current._pattern; _value = current._value; _range = current._range;
        _prefix = current._prefix; _suffix = current._suffix;
    }
    private void UseDocumentEnd()
    {
        _range = _pattern!.DocumentRange.Clone();
        _range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, _range, TextPatternRangeEndpoint.End);
        _prefix = _original; _suffix = "";
    }
    public string OriginalText => _original;
    public int SelectionStart => _prefix.Length;
    public int SelectionLength => Math.Max(0, _original.Length - _prefix.Length - _suffix.Length);
    private static ValuePattern? GetValue(AutomationElement element) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? (ValuePattern)value : null;

    internal static VoiceInputTarget CaptureNear(AutomationElement hit, Point? point)
    {
        var visited = new HashSet<string>(); var errors = new List<string>();
        var current = hit;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            if (current.Current.ProcessId == Environment.ProcessId || current.Current.ControlType == ControlType.Window) break;
            var target = TryCapture(current, point, visited, errors);
            if (target is not null) return target;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        if (point is { } location && FindPointEditor(hit, location) is { } editor) return editor;
        throw new InvalidOperationException("編集可能な入力欄が見つかりません。入力欄の内側へターゲットをドラッグしてください。" + string.Join(" / ", errors.Distinct().Take(3)));
    }

    private static VoiceInputTarget? FindPointEditor(AutomationElement hit, Point point)
    {
        // Chromiumのヒットテストは上位コンテナを返すことがある。display:contents等の
        // 親の矩形・階層深さでは剪定せず、同じ実ウィンドウ内の編集欄自身の矩形で絞る。
        var pid = hit.Current.ProcessId;
        if (pid == Environment.ProcessId) return null;
        var current = hit; nint window = 0;
        for (var depth = 0; current is not null && depth < 128 && current.Current.ProcessId == pid; depth++)
        {
            if (current.Current.NativeWindowHandle != 0) { window = GetAncestor((nint)current.Current.NativeWindowHandle, 2); break; }
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        if (window == 0) return null;
        var targets = new List<VoiceInputTarget>();
        var visited = new HashSet<string>();
        foreach (AutomationElement candidate in hit.FindAll(
            System.Windows.Automation.TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            if (candidate.Current.ProcessId != pid || candidate.Current.IsOffscreen || !candidate.Current.BoundingRectangle.Contains(point)) continue;
            if (TryCapture(candidate, point, visited) is { } target && target._window == window) targets.Add(target);
            if (targets.Count > 1) throw new InvalidOperationException("指定位置に複数の入力欄があります。入力欄の内側へ指定し直してください。");
        }
        return targets.SingleOrDefault();
    }

    private static VoiceInputTarget? TryCapture(AutomationElement element, Point? point, HashSet<string> visited, List<string>? errors = null)
    {
        try
        {
            if (!visited.Add(string.Join(".", element.GetRuntimeId()))) return null;
            if (point is { } p && !element.Current.BoundingRectangle.Contains(p)) return null;
            if (EmEditorTarget.TryCreate(element) is { } emEditor) return new(element, null, null, emEditor);
            var pattern = RequireEditable(element);
            TextPatternRange? range = null;
            if (pattern is not null)
            {
                try
                {
                    if (point is { } location)
                    {
                        range = pattern.RangeFromPoint(location);
                        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                    }
                    else
                    {
                        var selection = pattern.GetSelection();
                        if (selection.Length == 1) range = selection[0];
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { }
                if (range is null)
                {
                    range = pattern.DocumentRange.Clone();
                    range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                }
            }
            return new(element, pattern, range);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { errors?.Add(ex.Message); return null; }
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement root, Point? point, int limit)
    {
        var pending = new Queue<(AutomationElement Element, int Depth)>(); pending.Enqueue((root, 0));
        var count = 0;
        while (pending.Count > 0 && count < limit)
        {
            var (parent, depth) = pending.Dequeue();
            if (depth >= 16) continue;
            var child = TreeWalker.RawViewWalker.GetFirstChild(parent);
            while (child is not null && count++ < limit)
            {
                if (point is null || child.Current.BoundingRectangle.Contains(point.Value))
                {
                    yield return child; pending.Enqueue((child, depth + 1));
                }
                child = TreeWalker.RawViewWalker.GetNextSibling(child);
            }
        }
    }

    private void ResolveElement(bool restoreSelection = true)
    {
        if (_emEditor is not null) { _emEditor.Validate(); return; }
        var element = FindCurrentElement();
        if (element.GetRuntimeId().SequenceEqual(_runtimeId))
        {
            if (!restoreSelection) { _pattern = RequireEditable(element); _value = GetValue(element); }
            return;
        }
        _element = element; _runtimeId = element.GetRuntimeId(); _pattern = RequireEditable(element); _value = GetValue(element);
        if (!restoreSelection) return;
        if (ReadDocument() != _original) throw new InvalidOperationException("入力先の本文が変更されています。ターゲットを指定し直してください。");
        if (_original.Length == 0 && _value is { Current.IsReadOnly: false }) { _range = null; return; }
        if (_pattern is not null)
        {
            var restored = _pattern.DocumentRange.Clone();
            restored.MoveEndpointByRange(TextPatternRangeEndpoint.End, restored, TextPatternRangeEndpoint.Start);
            restored.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, SelectionStart);
            if (restored.GetText(200001) == _prefix)
            {
                restored.MoveEndpointByRange(TextPatternRangeEndpoint.Start, restored, TextPatternRangeEndpoint.End);
                restored.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, SelectionLength);
                _range = restored;
            }
            else UseDocumentEnd();
        }
    }
    private AutomationElement FindCurrentElement()
    {
        try
        {
            if (_element.GetRuntimeId().SequenceEqual(_runtimeId) && _element.Current.IsEnabled && _element.Current.IsKeyboardFocusable && !_element.Current.BoundingRectangle.IsEmpty)
                return _element;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException) { }
        if (_automationId.Length == 0)
        {
            // 名前や座標は別欄でも一致する。元の親が生存している場合だけ、直下の一意な欄を使う。
            try
            {
                if (_identityParent is not null && _identityParentId is not null &&
                    _identityParent.GetRuntimeId().SequenceEqual(_identityParentId))
                {
                    var local = _identityParent.FindAll(System.Windows.Automation.TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, _controlType)).Cast<AutomationElement>()
                        .Where(MatchesIdentity).Take(2).ToArray();
                    if (local.Length == 1) { RequireEditable(local[0]); return local[0]; }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException) { }
            throw new InvalidOperationException("保持した入力欄を一意に再取得できません。ターゲットを指定し直してください。");
        }
        // 再描画された要素だけを同じプロセス・ウィンドウ内で再取得する。
        var matches = Descendants(AutomationElement.FromHandle(_window), null, 1500)
            .Where(e => e.Current.ProcessId == _pid &&
                (e.GetRuntimeId().SequenceEqual(_runtimeId) ||
                 _automationId.Length > 0 && e.Current.AutomationId == _automationId &&
                 e.Current.ClassName == _className && e.Current.ControlType == _controlType)).Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("保持した入力欄を一意に再取得できません。ターゲットを指定し直してください。");
        RequireEditable(matches[0]); return matches[0];
    }
    private void FocusForInput()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RequireInputForeground();
            try { LogFocus("uia-set-focus"); _element.SetFocus(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // フォーカスを受けた元要素が消えた場合だけ、同一欄へ取り直して照合する。
                var previous = _runtimeId;
                Validate();
                if (_runtimeId.SequenceEqual(previous)) throw;
            }
            RequireInputForeground(); Validate();
            if (Automation.Compare(_element, AutomationElement.FocusedElement)) return;
        }
        throw new InvalidOperationException("入力先へフォーカスを戻せませんでした。");
    }
    private bool MatchesIdentity(AutomationElement element) => element.Current.ProcessId == _pid &&
        element.Current.AutomationId == _automationId && element.Current.ControlType == _controlType &&
        element.Current.FrameworkId == _frameworkId && StableClass(element.Current.ClassName) == StableClass(_className);
    private string StableClass(string value) => _frameworkId == "Chrome"
        ? string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => c != "ProseMirror-focused").Order()) : value;
}

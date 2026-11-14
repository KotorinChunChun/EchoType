using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace EchoType.App;

public sealed partial class VoiceInputTarget
{
    private bool IsDisplayedPlaceholder(string text)
    {
        var info = _element.Current;
        if (_pattern is null || info.FrameworkId != "Chrome" || !info.ClassName.Split(' ').Contains("ProseMirror") ||
            info.Name.Length is 0 or > 256 || text != info.Name + "\n") return false;
        try
        {
            // 空欄のCSSヒントと実際の「同文＋改行」は文字列も要素種別も同一。
            // ヒントだけが末尾の空行と同じ位置に重なる。色・ラベルの一致だけで除去しない。
            var children = _element.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Take(3).ToArray();
            if (children.Length != 2 || children.Any(e => e.Current.ControlType != ControlType.Text)) return false;
            var hint = children[0]; var lineBreak = children[1];
            if (hint.Current.Name != info.Name || hint.Current.ClassName.Length != 0 ||
                lineBreak.Current.ClassName != "ProseMirror-trailingBreak" || lineBreak.Current.Name != "\n") return false;
            return PlaceholderRangesOverlap(_pattern.RangeFromChild(hint).GetBoundingRectangles(),
                _pattern.RangeFromChild(lineBreak).GetBoundingRectangles());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException)
        {
            // 不明な構造では利用者の本文を捨てない。
            return false;
        }
    }

    internal static bool PlaceholderRangesOverlap(Rect[] hint, Rect[] lineBreak)
    {
        if (hint.Length != 1 || lineBreak.Length != 1) return false;
        var h = hint[0]; var b = lineBreak[0];
        return !h.IsEmpty && !b.IsEmpty && h.Width > 2 && h.Height > 0 && b.Width > 0 && b.Width <= 2 &&
            Math.Abs(h.Left - b.Left) <= .5 && Math.Abs(h.Top - b.Top) <= .5 && Math.Abs(h.Height - b.Height) <= .5;
    }
}

namespace EchoType.Core;

/// <summary>行単位の差分。極端に行数が多い場合は共通の先頭・末尾以外を変更として扱う。</summary>
public static class VoiceTextDiff
{
    public static (bool[] Before, bool[] After) Compare(string before, string after)
    {
        var a = before.Replace("\r", "").Split('\n'); var b = after.Replace("\r", "").Split('\n');
        var left = Enumerable.Repeat(true, a.Length).ToArray(); var right = Enumerable.Repeat(true, b.Length).ToArray();
        int start = 0, endA = a.Length, endB = b.Length;
        while (start < endA && start < endB && a[start] == b[start]) { left[start] = right[start] = false; start++; }
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { left[--endA] = right[--endB] = false; }
        if ((long)(endA - start) * (endB - start) > 1000000) return (left, right);
        var lengths = new int[endA - start + 1, endB - start + 1];
        for (var i = endA - 1; i >= start; i--)
            for (var j = endB - 1; j >= start; j--)
                lengths[i - start, j - start] = a[i] == b[j] ? 1 + lengths[i - start + 1, j - start + 1] : Math.Max(lengths[i - start + 1, j - start], lengths[i - start, j - start + 1]);
        int x = start, y = start;
        while (x < endA && y < endB)
        {
            if (a[x] == b[y]) { left[x++] = right[y++] = false; }
            else if (lengths[x - start + 1, y - start] >= lengths[x - start, y - start + 1]) x++;
            else y++;
        }
        return (left, right);
    }
}

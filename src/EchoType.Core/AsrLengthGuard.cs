using System.Diagnostics;
using System.Text;

namespace EchoType.Core;

/// <summary>同じ全文音声から得た二つの結果を比較する。途中認識は使用しない。</summary>
public static class AsrLengthGuard
{
    public static int Count(string text) => text.Trim().EnumerateRunes().Count();

    public static AsrResult Check(AsrResult full, AsrResult immediate)
    {
        full = InputCensorship.Filter(full); immediate = InputCensorship.Filter(immediate);
        if (immediate.CensoredText.Length > 0) return InputCensorship.Filter(full with { CensoredText = immediate.CensoredText });
        var a = Count(full.Text); var b = Count(immediate.Text);
        var note = immediate.Succeeded
            ? $"全文認識 {a}文字 / 即時認識（全文） {b}文字"
            : "文字数比較未実施: 即時認識エラー: " + immediate.Error;
        full = full with { LengthCheck = note, ImmediateFullText = immediate.Text };
        if (!full.Succeeded || !immediate.Succeeded || Math.Max(a, b) == 0) return full;
        if ((long)Math.Max(a, b) < 3L * Math.Min(a, b)) return full with { LengthCheck = note + $" / 文字数比 {((double)Math.Max(a, b) / Math.Min(a, b)):F2}倍 < 3倍: 文字数差による不採用条件に該当しません。" };
        return full with { Text = "", RejectedText = full.Text,
            Error = "文字数差が3倍以上のため不採用: " + note + (Math.Min(a, b) == 0 ? "（片方が空）" : "") };
    }

    public static async Task<AsrResult> RunAsync(Func<Task<AsrResult>> full, Func<Task<AsrResult>> immediate, bool enabled = true)
    {
        if (!enabled) return (await full()) with { LengthCheck = "文字数差判定は設定で無効です。" };
        var watch = Stopwatch.StartNew();
        var a = Task.Run(full); var b = Task.Run(immediate);
        await Task.WhenAll(a, b);
        return Check(await a, await b) with { ProcessingSeconds = watch.Elapsed.TotalSeconds };
    }
}

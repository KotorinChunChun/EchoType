using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace EchoType.Core;

/// <summary>認識した暴言を伏字にし、確定文と後処理への入力を空にする。</summary>
public static class InputCensorship
{
    public const string Reason = "暴言を検知したため検閲・処理対象外";
    private static readonly Regex Words = new("死ね|しね|殺す|ころす|馬鹿|ばか(?!り)|バカ(?!ンス)|アホ(?!ウドリ)|あほ", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string Text)> Turns = new();
    private static string Key(string id, string session, AudioSourceKind kind) => session + ":" + kind + ":" + id;
    public static void Remember(Transcript row)
    {
        if (row.CensoredText.Length == 0) return;
        Turns[Key(row.Id, row.SessionId, row.AudioSource.Kind)] = (DateTimeOffset.UtcNow, row.CensoredText);
        if (Turns.Count > 2048) foreach (var key in Turns.OrderBy(t => t.Value.At).Take(Turns.Count - 2048).Select(t => t.Key)) Turns.TryRemove(key, out _);
    }
    public static AsrResult Filter(string id, string session, AudioSourceKind kind, AsrResult result)
    {
        var key = Key(id, session, kind);
        if (Turns.TryGetValue(key, out var turn)) result = result with { CensoredText = turn.Text };
        result = Filter(result);
        if (result.CensoredText.Length > 0) Remember(new(id, "", DateTimeOffset.UtcNow, "asr") { CensoredText = result.CensoredText, SessionId = session, AudioSource = new() { Kind = kind } });
        return result;
    }
    public static bool Contains(string text) => Words.IsMatch(text.Normalize(NormalizationForm.FormKC));
    public static string Allowed(string text) => Contains(text) ? throw new InvalidOperationException(Reason) : text;
    public static string Mask(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return Words.IsMatch(normalized) ? Words.Replace(normalized, "●●") : text;
    }
    public static AsrResult Filter(AsrResult result)
    {
        var blocked = result.CensoredText.Length > 0 || Contains(result.Text) || Contains(result.OriginalText) || Contains(result.RejectedText);
        if (!blocked) return result with { ImmediateFullText = Mask(result.ImmediateFullText) };
        var display = result.CensoredText.Length > 0 ? result.CensoredText : result.Text.Length > 0 ? result.Text : result.RejectedText.Length > 0 ? result.RejectedText : result.OriginalText;
        return result with { Text = "", OriginalText = "", RejectedText = "", ImmediateFullText = "", CensoredText = Mask(display), Error = Reason };
    }
    public static Transcript Filter(Transcript value)
    {
        if (value.CensoredText.Length == 0 && !Contains(value.Text) && !Contains(value.OriginalText) && !Contains(value.LocalDraft) && !Contains(value.InquiryText)) return value;
        var display = value.CensoredText.Length > 0 ? value.CensoredText : value.Text.Length > 0 ? value.Text : value.LocalDraft.Length > 0 ? value.LocalDraft : value.InquiryText.Length > 0 ? value.InquiryText : value.OriginalText;
        return value with { Text = "", CensoredText = Mask(display), OriginalText = "", LocalDraft = "", InquiryText = "", InquiryEvaluationJson = "", Response = "", IsFinal = true, Classification = "対象外", Reason = Reason, Handling = Reason, PostProcessingStatus = "", RecognitionError = Reason };
    }
}

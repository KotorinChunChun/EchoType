namespace EchoType.Core;

public enum CaptionTimelineCoverage { Unavailable, Partial, Complete }
public sealed record CaptionTextElement(string Id, string Text)
{
    public string[] Lines { get; init; } = [];
    public string Source { get; init; } = "";
}
public sealed record CaptionMissingRange(DateTimeOffset Start, DateTimeOffset End, string Reason);

/// <summary>範囲はUIAの観測時刻。音声の発話時刻や認識精度を保証するものではない。</summary>
public sealed record CaptionTimelineResult(string Text, string Status, CaptionTimelineCoverage Coverage,
    DateTimeOffset? ObservedFrom, DateTimeOffset? ObservedUntil)
{
    public CaptionMissingRange[] MissingRanges { get; init; } = [];
}

/// <summary>
/// OS字幕の累積・スクロール・短い末尾訂正を時刻付きの文字列へ変換する。
/// スレッド安全、Readは非破壊。初回表示と対応関係が曖昧な全文置換は採用しない。
/// </summary>
public sealed class CaptionTimeline
{
    private readonly object _sync = new();
    private readonly TimeSpan _retention, _maximumGap;
    private readonly int _maximumCharacters, _maximumSnapshotCharacters;
    private readonly List<Character> _history = [];
    private readonly List<UncertainRange> _uncertain = [];
    private List<Character> _current = [];
    private string _text = "", _session = "";
    private DateTimeOffset? _first, _last, _availableFrom;
    private readonly bool _elementCorrections;
    private sealed class Character(char value, DateTimeOffset? at)
    {
        public char Value { get; } = value;
        public DateTimeOffset? At { get; } = at;
        public bool Valid { get; set; } = true;
    }
    private sealed record UncertainRange(DateTimeOffset Start, DateTimeOffset End, string Reason);

    public CaptionTimeline(TimeSpan? retention = null, int maximumCharacters = 20000,
        int maximumSnapshotCharacters = 8000, TimeSpan? maximumObservationGap = null, bool elementCorrections = false)
    {
        _retention = retention ?? TimeSpan.FromMinutes(2);
        _maximumGap = maximumObservationGap ?? TimeSpan.FromSeconds(2);
        if (_retention <= TimeSpan.Zero || _maximumGap <= TimeSpan.Zero || maximumCharacters < 16 || maximumSnapshotCharacters < 16)
            throw new ArgumentOutOfRangeException(nameof(retention));
        _maximumCharacters = maximumCharacters;
        _maximumSnapshotCharacters = maximumSnapshotCharacters;
        _elementCorrections = elementCorrections;
    }

    public void Observe(DateTimeOffset at, string text, string session)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            if (_last is { } last && at <= last) return;
            if (_last is { } previous && at - previous > _maximumGap)
                _uncertain.Add(new(previous.AddTicks(1), at, "字幕の観測間隔が空きました。"));
            var first = _last is null;
            _first ??= at; _availableFrom ??= at; _last = at;
            if (first || session != _session)
            {
                if (!first) _uncertain.Add(new(at, at, "字幕プロセスが変わりました。"));
                _session = session;
                Baseline(text);
            }
            else if (text.Length > _maximumSnapshotCharacters)
            {
                _uncertain.Add(new(at, at, "字幕が読み取り文字数の上限を超えました。"));
                Baseline(text);
            }
            else if (text != _text)
            {
                if (text.Length == 0) { _text = ""; _current = []; }
                else if (_text.Length == 0) Append(text, at, []);
                else if (text.StartsWith(_text, StringComparison.Ordinal))
                    Append(text[_text.Length..], at, _current);
                else
                {
                    var overlap = SuffixPrefixOverlap(_text, text);
                    if (overlap >= 3 && overlap >= Math.Min(12, _text.Length / 2))
                        Append(text[overlap..], at, _current.TakeLast(overlap).ToList());
                    else
                    {
                        var common = CommonPrefix(_text, text);
                        var removed = _text.Length - common;
                        var added = text.Length - common;
                        // 短い末尾訂正に限定。別発話の冒頭がたまたま一致するだけの置換を採らない。
                        // 完了した前の行を訂正せず、同じUIA要素の末尾行だけ長い訂正を許す。
                        if (_elementCorrections && common >= _text.LastIndexOf('\n') + 1 && common >= 8 && common >= Math.Min(24, _text.Length / 2))
                            CorrectElementTail(text, common, at);
                        else if (common >= 2 && removed <= 12 && added <= 16 && common >= Math.Min(8, _text.Length / 2) &&
                            _current.Skip(common).Select(c => c.At).Distinct().Take(2).Count() <= 1)
                            CorrectTail(text, common);
                        else
                        {
                            var from = at;
                            // 要素追跡では判別不能な表示部分を残さず、表示から既に外れた確定部分だけを保持する。
                            if (_elementCorrections)
                            {
                                var uncertainLine = _current.Skip(_text.LastIndexOf('\n') + 1).ToArray();
                                foreach (var character in uncertainLine)
                                {
                                    character.Valid = false;
                                    if (character.At is { } observed && observed < from) from = observed;
                                }
                            }
                            _uncertain.Add(new(from, at, "字幕のリセットと全文訂正を区別できませんでした。"));
                            Baseline(text);
                        }
                    }
                }
            }
            Prune(at);
        }
    }

    public CaptionTimelineResult Read(DateTimeOffset start, DateTimeOffset end, bool preservePartialText = false)
    {
        if (end < start) throw new ArgumentException("字幕の終了時刻は開始時刻以降にしてください。");
        lock (_sync)
        {
            if (_first is null || _last is null || end < _availableFrom || start > _last)
                return new("", "指定時間帯の字幕観測がありません。", CaptionTimelineCoverage.Unavailable, _availableFrom, _last);
            var uncertain = _uncertain.FirstOrDefault(r => r.Start <= end && r.End >= start);
            var result = string.Concat(_history.Where(c => c.Valid && c.At >= start && c.At <= end).Select(c => c.Value)).Trim();
            if (uncertain is not null)
                return new(preservePartialText ? result : "", uncertain.Reason + " 不確かな部分は採用しません。",
                    CaptionTimelineCoverage.Partial, _availableFrom, _last)
                { MissingRanges = _uncertain.Where(r => r.Start <= end && r.End >= start).Select(r => new CaptionMissingRange(r.Start, r.End, r.Reason)).ToArray() };
            if (start < _availableFrom || end > _last)
                return new("", "指定時間帯全体を観測できていません。", CaptionTimelineCoverage.Partial, _availableFrom, _last);
            return new(result, result.Length == 0 ? "この観測区間で追加された字幕はありません。" : "UIA観測時刻で追加文字を取得しました。",
                CaptionTimelineCoverage.Complete, _availableFrom, _last);
        }
    }

    private void CorrectElementTail(string text, int common, DateTimeOffset at)
    {
        var old = _current.Skip(common).ToArray();
        foreach (var character in old) character.Valid = false;
        if (old.Any(character => character.At is null))
        {
            // 既存表示の訂正を新規発話として時刻付けしない。新規文字もまたぐ置換は
            // 対応を断定できないため欠測にし、次の明確な追記から取り直す。
            if (old.Any(character => character.At is not null))
                _uncertain.Add(new(old.Where(character => character.At is not null).Min(character => character.At)!.Value,
                    at, "既存字幕と新規字幕をまたぐ訂正を区別できませんでした。"));
            Baseline(text);
            return;
        }
        // 訂正時刻へ帰属させ、過去の短い発話へ新しい本文を移さない。
        var retained = _current.Take(common).ToList();
        Append(text[common..], at, retained);
    }

    private void Baseline(string text)
    {
        // 異常に長い本文を保持しない。次に正常長が返るまで全文置換として再評価する。
        _text = text.Length <= _maximumSnapshotCharacters ? text : text[^_maximumSnapshotCharacters..];
        _current = _text.Select(c => new Character(c, null)).ToList();
    }

    private void Append(string addition, DateTimeOffset at, List<Character> retained)
    {
        var stamp = retained.LastOrDefault()?.At;
        var meaningful = false;
        foreach (var value in addition)
        {
            meaningful |= !char.IsPunctuation(value) && !char.IsWhiteSpace(value);
            // 遅れて付いた句読点を次の発話に持ち込まない。新しい本文の後はその観測時刻を使う。
            var cell = new Character(value, meaningful || retained.Count == 0 ? at : stamp);
            retained.Add(cell);
            if (cell.At is not null) _history.Add(cell);
        }
        _current = retained;
        _text = string.Concat(retained.Select(c => c.Value));
    }

    private void CorrectTail(string text, int common)
    {
        var oldTail = _current.Skip(common).ToArray();
        // 置換部分が伸びても全体に元の最も古い時刻を使う。訂正を後の発話へ流さない。
        DateTimeOffset? stamp = oldTail.Length == 0 || oldTail.Any(c => c.At is null) ? null : oldTail.Min(c => c.At);
        foreach (var cell in oldTail) cell.Valid = false;
        var corrected = _current.Take(common).ToList();
        foreach (var value in text[common..])
        {
            var cell = new Character(value, stamp);
            corrected.Add(cell);
            if (stamp is not null) _history.Add(cell);
        }
        _current = corrected; _text = text;
    }

    private void Prune(DateTimeOffset at)
    {
        var cutoff = at - _retention;
        if (_availableFrom < cutoff) _availableFrom = cutoff;
        _history.RemoveAll(c => !c.Valid || c.At < cutoff);
        _uncertain.RemoveAll(r => r.End < cutoff);
        if (_history.Count > _maximumCharacters)
        {
            var removed = _history.Count - _maximumCharacters;
            var boundary = _history[removed - 1].At!.Value.AddTicks(1);
            if (_availableFrom < boundary) _availableFrom = boundary;
            _history.RemoveRange(0, removed);
        }
        if (_uncertain.Count > _maximumCharacters)
        {
            var removed = _uncertain.Count - _maximumCharacters;
            var boundary = _uncertain[removed - 1].End.AddTicks(1);
            if (_availableFrom < boundary) _availableFrom = boundary;
            _uncertain.RemoveRange(0, removed);
        }
    }

    private static int CommonPrefix(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length); var i = 0;
        while (i < length && left[i] == right[i]) i++;
        return i;
    }
    private static int SuffixPrefixOverlap(string left, string right)
    {
        // KMPで最長のsuffix-prefixを線形時間で求める。150msごとの長い字幕でも二乗探索しない。
        if (right.Length == 0) return 0;
        var prefix = new int[right.Length];
        for (var i = 1; i < right.Length; i++)
        {
            var matched = prefix[i - 1];
            while (matched > 0 && right[i] != right[matched]) matched = prefix[matched - 1];
            if (right[i] == right[matched]) matched++;
            prefix[i] = matched;
        }
        var count = 0;
        foreach (var c in left)
        {
            if (count == right.Length) count = prefix[count - 1];
            while (count > 0 && c != right[count]) count = prefix[count - 1];
            if (c == right[count]) count++;
        }
        return count;
    }
}

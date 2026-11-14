namespace EchoType.Core;

/// <summary>UIA要素の生存期間と本文内の行を使う。音源の一致は保証しない。</summary>
public sealed class CaptionElementTimeline
{
    private sealed class Entry(DateTimeOffset at, string session)
    {
        public readonly CaptionTimeline Timeline = new(elementCorrections: true);
        public readonly DateTimeOffset First = at;
        public DateTimeOffset Last = at;
        public readonly string Session = session;
    }
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly List<CaptionMissingRange> _missing = [];
    private string _session = "";
    private DateTimeOffset? _first, _last, _discarded;
    private int _epoch;

    public void Observe(DateTimeOffset at, string session, CaptionTextElement[] elements)
    {
        if (_last is { } last && at <= last) return;
        var baseline = _last is null || session != _session;
        if (_last is { } previous && at - previous > TimeSpan.FromSeconds(2))
        { _missing.Add(new(previous.AddTicks(1), at, "字幕の観測間隔が空きました。")); baseline = true; }
        if (_last is not null && session != _session)
            _missing.Add(new(at, at, "字幕プロセスまたは取得セッションが変わりました。"));
        if (baseline && _last is not null) _epoch++;
        _first ??= at; _last = at; _session = session;
        if (elements.Length > 32 || elements.Any(e => string.IsNullOrEmpty(e.Id) || e.Lines.Length > 0 && string.Join("\n", e.Lines) != e.Text) || elements.Select(e => e.Id).Distinct().Count() != elements.Length)
        { _missing.Add(new(at, at, "字幕要素の識別情報が不正です。")); return; }
        var active = elements.Select(e => _epoch + "|" + e.Id).ToHashSet();
        foreach (var element in elements)
        {
            var key = _epoch + "|" + element.Id;
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new(at, session); _entries.Add(key, entry);
                // 最初の既存表示は除外。それ以降の新規UIA要素は新しい表示として追跡する。
                entry.Timeline.Observe(at.AddTicks(-1), baseline ? element.Text : "", session);
            }
            entry.Last = at;
            entry.Timeline.Observe(at, element.Text, session);
        }
        // 消えた要素の本文を確保し、元の要素の訂正が別の要素へ波及しないようにする。
        foreach (var pair in _entries.Where(p => !active.Contains(p.Key)))
            pair.Value.Timeline.Observe(at, "", pair.Value.Session);
        var cutoff = at.AddMinutes(-2);
        foreach (var key in _entries.Where(p => p.Value.Last < cutoff).Select(p => p.Key).ToArray()) _entries.Remove(key);
        while (_entries.Count > 128)
        {
            var oldest = _entries.MinBy(p => p.Value.Last);
            _discarded = oldest.Value.Last; _entries.Remove(oldest.Key);
        }
        _missing.RemoveAll(r => r.End < cutoff);
        if (_missing.Count > 1024) { _discarded = _missing[^1024].Start; _missing.RemoveRange(0, _missing.Count - 1024); }
    }

    public CaptionTimelineResult Read(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start) throw new ArgumentException("字幕の終了時刻は開始時刻以降にしてください。");
        if (_first is null || _last is null || start > _last || end < _first)
            return new("", "指定時間帯の字幕観測がありません。", CaptionTimelineCoverage.Unavailable, _first, _last);
        var missing = _missing.Where(r => r.Start <= end && r.End >= start).ToList();
        if (start < _first || end > _last || start < _last.Value.AddMinutes(-2) || _discarded is { } discarded && start <= discarded)
            missing.Add(new(start, end, "指定時間帯全体を保持・観測できていません。"));
        var parts = new List<string>();
        foreach (var entry in _entries.Values.OrderBy(e => e.First))
        {
            var from = start > entry.First ? start : entry.First;
            if (from > end) continue;
            var result = entry.Timeline.Read(from, end, preservePartialText: true);
            if (result.Text.Length > 0) parts.Add(result.Text);
            missing.AddRange(result.MissingRanges);
        }
        var text = string.Join("\n", parts);
        return new(text, missing.Count == 0 ? "UIA要素・行の字幕を取得しました。" : "字幕の一部が不確定です。部分本文は比較用に保持し、採用しません。",
            missing.Count == 0 ? CaptionTimelineCoverage.Complete : CaptionTimelineCoverage.Partial, _first, _last)
        { MissingRanges = missing.ToArray() };
    }
}

namespace EchoType.Core;

public sealed record SpeakerProfile(string Name, string DiscordUserId, SpeakerExample[] Examples)
{
    public string DiscordDisplayName { get; init; } = "";
}

public sealed record SpeakerExample(string Id, DateTimeOffset RecordedAt, float[] Vector)
{
    public string Transcript { get; init; } = "";
    public bool Short { get; init; }
    public string SourceUtteranceId { get; init; } = "";
}

public sealed record SpeakerSimilarity(string Name, double? Score, string Reason);

public sealed record SpeakerMatch(string? Name, string Reason, SpeakerSimilarity[] Scores)
{
    public string Describe() => Scores.Length == 0 ? Reason : Reason + "\n話者\t類似度\t判定\n" + string.Join("\n", Scores.Select(s =>
        s.Score is { } score ? $"{s.Name}\t{score:F3}\t{s.Reason}" : $"{s.Name}\t—\t{s.Reason}"));
}

public static class SpeakerProfiles
{
    public const double MinimumSimilarity = .86;
    public const double MinimumLead = .04;
    public const int Dimensions = 512;
    public const string EmbeddingVersion = "microsoft/wavlm-base-plus-sv";
    public static SpeakerProfile[] Candidates(IEnumerable<SpeakerProfile> profiles, string[]? selected, IReadOnlySet<string>? participants = null)
    {
        var names = selected?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profiles.Where(p => names is not null ? names.Contains(p.Name) :
            participants is null || participants.Contains(p.DiscordUserId)).ToArray();
    }
    public static bool ValidVector(float[] vector) => vector.Length == Dimensions && vector.All(float.IsFinite) &&
        vector.Sum(x => (double)x * x) > 0;

    public static string? Match(float[] vector, IEnumerable<SpeakerProfile> profiles, IReadOnlySet<string>? participants = null) =>
        Evaluate(vector, profiles, participants: participants).Name;

    public static SpeakerMatch Evaluate(float[]? vector, IEnumerable<SpeakerProfile> profiles,
        double minimumSimilarity = MinimumSimilarity, double minimumLead = MinimumLead, bool shortAudio = false,
        IReadOnlySet<string>? participants = null, IReadOnlySet<string>? candidates = null, string? utteranceId = null)
    {
        var hasVector = vector is not null && ValidVector(vector);
        var scores = profiles.Select(profile =>
        {
            if (candidates is not null && !candidates.Contains(profile.Name))
                return new SpeakerSimilarity(profile.Name, null, "候補絞り込みの対象外");
            if (participants is not null && !participants.Contains(profile.DiscordUserId))
                return new SpeakerSimilarity(profile.Name, null, "Discord通話の参加者候補外");
            if (!hasVector) return new(profile.Name, null, "音声特徴なし");
            var score = profile.Examples.Where(e => e.Short == shortAudio && (utteranceId is null || e.SourceUtteranceId != utteranceId) && ValidVector(e.Vector))
                .Select(e => (double?)Cosine(vector!, e.Vector)).Max();
            return new(profile.Name, score, score is null ? "比較できる見本なし" : "");
        }).OrderByDescending(s => s.Score).ThenBy(s => s.Name, StringComparer.CurrentCulture).ToArray();
        var label = shortAudio ? "短文" : "通常";
        if (scores.FirstOrDefault()?.Score is not { } best) return new(null, $"{label} / 比較できる候補なし", scores);
        if (best < minimumSimilarity) return new(null, $"{label} / しきい値 {minimumSimilarity:F2} 未満", scores);
        if (scores.ElementAtOrDefault(1)?.Score is { } second && best - second < minimumLead)
            return new(null, $"{label} / 次点との差 {minimumLead:F2} 未満", scores);
        // 短文は実音声の誤照合率を測るまで参考表示にとどめる。
        return shortAudio ? new(null, $"短文 / 参考候補 {scores[0].Name}", scores) : new(scores[0].Name, $"通常 / {scores[0].Name} を採用", scores);
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i]; a += (double)left[i] * left[i]; b += (double)right[i] * right[i];
        }
        return dot / Math.Sqrt(a * b);
    }
}

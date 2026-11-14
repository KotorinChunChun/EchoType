using System.Text.Json;

namespace EchoType.Core;

public sealed record VoiceApplicationProfile(string ApplicationPath, string Name, VoiceInputSettings Settings)
{
    public VoiceInputSettings Apply(VoiceInputSettings common) => common with
    {
        Models = Settings.Models, LiveEngine = Settings.LiveEngine,
        AutomaticInput = Settings.AutomaticInput, AutomaticFormatting = Settings.AutomaticFormatting,
        AutomaticConfirm = Settings.AutomaticConfirm, FormattingModel = Settings.FormattingModel,
        FormattingEffort = Settings.FormattingEffort, FormattingPrompt = Settings.FormattingPrompt,
        SimpleFormattingPrompt = Settings.SimpleFormattingPrompt,
        Recognition = common.Recognition with { AsrVocabulary = Settings.Recognition.AsrVocabulary }
    };
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(ApplicationPath) || !ApplicationPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new InvalidOperationException("アプリの実行ファイルと名前を指定してください。");
        Settings.Validate();
    }
}

public sealed record PersonalWord(string Id, string Wrong, string Correct, string Reading, string ApplicationPath = "")
{
    public string Scope => ApplicationPath.Length == 0 ? "すべて" : Path.GetFileName(ApplicationPath);
    public bool AppliesTo(string application) => ApplicationPath.Length == 0 || string.Equals(ApplicationPath, application, StringComparison.OrdinalIgnoreCase);
    public void Validate()
    {
        if (!Guid.TryParse(Id, out _) || string.IsNullOrWhiteSpace(Correct) || Correct.Length > 100 || Wrong.Length > 100 || Reading.Length > 100 ||
            (Wrong + Correct + Reading).Any(char.IsControl) || ApplicationPath.Length > 0 && !Path.IsPathFullyQualified(ApplicationPath))
            throw new InvalidOperationException("単語は各100文字以内で指定してください。適用先は空欄または実行ファイルの絶対パスです。");
    }
}

public static class VoicePersonalization
{
    public static VoiceInputSettings Enrich(VoiceInputSettings settings, AppSettings shared, IEnumerable<PersonalWord> words, string application)
    {
        var selected = words.Where(w => w.AppliesTo(application)).Take(100).ToArray();
        var hint = string.Join("、", new[] { shared.AsrVocabulary, settings.Recognition.AsrVocabulary }
            .Concat(selected.SelectMany(w => new[] { w.Correct, w.Reading })).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        var result = settings.WithShared(shared);
        return result with { Recognition = result.Recognition with { AsrVocabulary = hint.Length > 1000 ? hint[..1000] : hint },
            PersonalCorrections = JsonSerializer.Serialize(selected.Select(w => new { w.Wrong, w.Correct, w.Reading })) };
    }
}

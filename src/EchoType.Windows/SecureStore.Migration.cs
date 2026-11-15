using System.Text.Json;
using EchoType.Core;
using Microsoft.Data.Sqlite;

namespace EchoType.Windows;

public sealed partial class SecureStore
{
    public string ImportDesktopAgent(string sourceRoot)
    {
        const string migration = "desktop-agent-voice-v1";
        string? AlreadyImported() => Read<string>("migration", 1).FirstOrDefault();
        if (AlreadyImported() is { } previous) return "移行済みです。" + previous;

        var source = Path.Combine(Path.GetFullPath(sourceRoot), "data", "agent.sqlite");
        AgentPaths.RequireLocalPath(source);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var select = db.CreateCommand();
        select.CommandText = """
            SELECT kind, payload FROM entries
            WHERE ((kind IN ('settings', 'voice-input-settings') AND id='current')
                OR kind IN ('voice-profile', 'personal-word'))
                AND (expires IS NULL OR expires > $now)
            """;
        select.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rows = select.ExecuteReader();
        lock (_sync)
        {
            using var transaction = _db.BeginTransaction();
            var counts = new int[4];
            var now = DateTimeOffset.UtcNow;
            while (rows.Read())
            {
                var plain = Unprotect((byte[])rows[1]);
                try
                {
                    switch (rows.GetString(0))
                    {
                        case "settings":
                            var shared = Decode<AppSettings>(plain);
                            shared.Performance.Validate();
                            if (PutEntry("settings", "current", new AppSettings
                                { AsrVocabulary = shared.AsrVocabulary, CodexExecutable = shared.CodexExecutable, Performance = shared.Performance },
                                now, insertOnly: true, transaction: transaction)) counts[0]++;
                            break;
                        case "voice-input-settings":
                            var voice = NormalizeVoiceSettings(Decode<VoiceInputSettings>(plain));
                            voice.Validate();
                            if (PutEntry("voice-input-settings", "current", voice, now, insertOnly: true, transaction: transaction)) counts[1]++;
                            break;
                        case "voice-profile":
                            var profile = Decode<VoiceApplicationProfile>(plain);
                            profile = profile with { Settings = NormalizeVoiceSettings(profile.Settings) };
                            profile.Validate();
                            if (PutEntry("voice-profile", profile.ApplicationPath.ToUpperInvariant(), profile, now, insertOnly: true, transaction: transaction)) counts[2]++;
                            break;
                        case "personal-word":
                            var word = Decode<PersonalWord>(plain);
                            word.Validate();
                            if (PutEntry("personal-word", word.Id, word, now, insertOnly: true, transaction: transaction)) counts[3]++;
                            break;
                    }
                }
                finally { Array.Clear(plain); }
            }
            var summary = $"共通認識設定 {counts[0]}件、音声入力設定 {counts[1]}件、アプリ別設定 {counts[2]}件、個人辞書 {counts[3]}件。";
            if (!PutEntry("migration", migration, summary, now, insertOnly: true, transaction: transaction))
                throw new InvalidOperationException("別の処理がすでに移行を完了しました。");
            transaction.Commit();
            return summary;
        }
    }

    private static VoiceInputSettings NormalizeVoiceSettings(VoiceInputSettings value)
    {
        var settings = value.MigrateSelections().LocalOnly();
        return settings with
        {
            ExternalSpeech = new(),
            FormattingPrompt = settings.FormattingPrompt == VoiceInputSettings.PreviousFormattingPrompt
                ? VoiceInputSettings.DefaultFormattingPrompt : settings.FormattingPrompt,
            Recognition = settings.Recognition with
            {
                AdditionalSettings = null,
                ApiEnvironment = new(), CodexHome = "", CodexExecutable = "",
                ScreenVocabularyEnabled = false, SkillVocabularyEnabled = false
            }
        };
    }

    private static T Decode<T>(byte[] plain) =>
        JsonSerializer.Deserialize<T>(plain, JsonFormat.Options) ?? throw new InvalidDataException("移行元に空のデータがあります。");
}

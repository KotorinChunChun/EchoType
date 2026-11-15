using EchoType.Core;
using EchoType.Windows;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class MigrationTests
{
    public static void Add(List<(string Name, Func<Task> Test)> tests, string root)
    {
        tests.Add(("読み取り専用移行で設定・辞書を保持し、旧DBと移行済みDBを再変更しない", () =>
        {
            var source = Path.Combine(root, "old");
            var voice = new VoiceInputSettings { TriggerKeys = [0xA3, 0xA1], Models = ["whisper-base", "", "windows"], FormattingPrompt = "試験用の整形" };
            var profile = new VoiceApplicationProfile(@"C:\test\fixture.exe", "試験", voice);
            var word = new PersonalWord(Guid.NewGuid().ToString(), "試割", "試験", "しけん");
            var savedVoice = JsonSerializer.SerializeToNode(voice, JsonFormat.Options)!.AsObject();
            savedVoice["recognition"] = LegacySettings(voice.Recognition);
            var file = Fixture(source, ("settings", "current", LegacySettings(new AppSettings { AsrVocabulary = "語彙", CodexHome = "試験用の旧パス", Performance = new() { CpuThreads = 3 } })),
                ("voice-input-settings", "current", savedVoice), ("voice-profile", "profile", profile),
                ("personal-word", word.Id, word), ("transcript", "recorded", "移行対象外"));
            var original = File.ReadAllBytes(file);
            var paths = new AgentPaths(Path.Combine(root, "new"));
            using var store = new SecureStore(paths);
            store.ImportDesktopAgent(source);
            var imported = store.LoadVoiceInputSettings();
            Require(imported.Models.SequenceEqual(voice.Models) && imported.TriggerKeys.SequenceEqual(voice.TriggerKeys)
                && imported.FormattingPrompt == voice.FormattingPrompt, "音声入力設定を保持");
            Require(!JsonSerializer.Serialize(imported, JsonFormat.Options).Contains("wakeAcknowledgementReplies"), "旧アプリ専用の追加設定は取り込まない");
            Require(store.GetVoiceProfiles().Single().Name == profile.Name && store.GetPersonalWords().Single() == word, "アプリ別設定と辞書を保持");
            var shared = store.LoadSettings();
            Require(shared.AsrVocabulary == "語彙" && shared.Performance.CpuThreads == 3 && shared.CodexHome.Length == 0, "音声入力に必要な共通設定だけを移行");
            store.DeletePersonalWord(word.Id);
            store.SaveVoiceInputSettings(imported with { MaximumSeconds = 12 });
            Require(store.ImportDesktopAgent(source).StartsWith("移行済み", StringComparison.Ordinal) &&
                store.GetPersonalWords().Length == 0 && store.LoadVoiceInputSettings().MaximumSeconds == 12, "2回目は削除済みの辞書や保存済みの設定を戻さない");
            using var destination = new SqliteConnection($"Data Source={Path.Combine(paths.Root, "data", "echo.sqlite")}");
            destination.Open();
            using var count = destination.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM entries WHERE kind='transcript'";
            Require(Convert.ToInt32(count.ExecuteScalar()) == 0, "会話の文字起こしは対象外");
            Require(File.ReadAllBytes(file).SequenceEqual(original) && !Directory.Exists(Path.Combine(paths.Root, "model")),
                "旧DBを変更せずEchoType専用のモデル領域を作らない");
            return Task.CompletedTask;
        }));

        tests.Add(("保存済みの旧設定で起動し、設定を読み書きしても未使用項目を保持する", () =>
        {
            var paths = SeedSavedLegacySettings(Path.Combine(root, "old-settings"));
            using var store = new SecureStore(paths);
            var loaded = store.LoadSettings();
            Require(loaded.AsrVocabulary == "保持する語彙", "既存の共通設定を読める");
            store.SaveSettings(loaded with { DeviceName = "試験マイク" });
            var saved = store.LoadSettings();
            var json = JsonSerializer.SerializeToNode(saved, JsonFormat.Options)!;
            Require(saved.DeviceName == "試験マイク" && (string?)json["wakeAcknowledgementReplies"] == "旧版専用の応答",
                "設定を保存しても既存の追加項目を保持する");
            return Task.CompletedTask;
        }));

        tests.Add(("移行中の不正データは全件ロールバックし再試行できる", () =>
        {
            var source = Path.Combine(root, "invalid");
            var word = new PersonalWord(Guid.NewGuid().ToString(), "", "合成", "");
            var file = Fixture(source, ("personal-word", word.Id, word),
                ("voice-input-settings", "current", new VoiceInputSettings { MaximumSeconds = 0 }));
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "rollback")));
            var rejected = false;
            try { store.ImportDesktopAgent(source); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.GetPersonalWords().Length == 0, "不正データで移行済みにせず部分書込みも残さない");
            SqliteConnection.ClearAllPools();
            File.Delete(file);
            Fixture(source, ("personal-word", word.Id, word));
            store.ImportDesktopAgent(source);
            Require(store.GetPersonalWords().Single() == word, "修正後は再試行できる");
            return Task.CompletedTask;
        }));
    }

    internal static AgentPaths SeedSavedLegacySettings(string root)
    {
        var paths = new AgentPaths(root);
        var file = Fixture(paths.Root, ("settings", "current", LegacySettings(new AppSettings { AsrVocabulary = "保持する語彙" })));
        File.Move(file, Path.Combine(paths.Root, "data", "echo.sqlite"));
        return paths;
    }

    private static JsonObject LegacySettings(AppSettings settings)
    {
        var json = JsonSerializer.SerializeToNode(settings, JsonFormat.Options)!.AsObject();
        json["wakeAcknowledgementReplies"] = "旧版専用の応答";
        return json;
    }

    private static string Fixture(string root, params (string Kind, string Id, object Value)[] values)
    {
        var data = Directory.CreateDirectory(Path.Combine(root, "data"));
        var file = Path.Combine(data.FullName, "agent.sqlite");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE entries(kind TEXT NOT NULL,id TEXT NOT NULL,created INTEGER NOT NULL,expires INTEGER,payload BLOB NOT NULL,PRIMARY KEY(kind,id))";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO entries(kind,id,created,payload) VALUES($k,$i,1,$p)";
        foreach (var (kind, id, value) in values)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$k", kind);
            command.Parameters.AddWithValue("$i", id);
            command.Parameters.AddWithValue("$p", SecureStore.Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonFormat.Options)));
            command.ExecuteNonQuery();
        }
        return file;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

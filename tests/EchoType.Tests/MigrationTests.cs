using EchoType.Core;
using EchoType.Windows;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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
            var file = Fixture(source, ("settings", "current", new AppSettings { AsrVocabulary = "語彙", CodexHome = "試験用の旧パス", Performance = new() { CpuThreads = 3 } }),
                ("voice-input-settings", "current", voice), ("voice-profile", "profile", profile),
                ("personal-word", word.Id, word), ("transcript", "recorded", "移行対象外"));
            var original = File.ReadAllBytes(file);
            var paths = new AgentPaths(Path.Combine(root, "new"), source);
            using var store = new SecureStore(paths);
            store.ImportDesktopAgent(source);
            var imported = store.LoadVoiceInputSettings();
            Require(imported.Models.SequenceEqual(voice.Models) && imported.TriggerKeys.SequenceEqual(voice.TriggerKeys)
                && imported.FormattingPrompt == voice.FormattingPrompt, "音声入力設定を保持");
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
            Require(File.ReadAllBytes(file).SequenceEqual(original) && paths.Model != paths.RecognitionModels, "旧DBとモデル参照先を分離");
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

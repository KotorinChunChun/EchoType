using System.Text.Json;
using EchoType.Core;
using Microsoft.Data.Sqlite;

namespace EchoType.Windows;

public sealed record CodexUsage(long InputTokens, long CachedInputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
    public static CodexUsage? ParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "turn.completed" || !root.TryGetProperty("usage", out var usage)) return null;
            if (!usage.TryGetProperty("input_tokens", out var input) || !input.TryGetInt64(out var i) || i < 0 ||
                !usage.TryGetProperty("output_tokens", out var output) || !output.TryGetInt64(out var o) || o < 0) return null;
            var c = usage.TryGetProperty("cached_input_tokens", out var cached) && cached.TryGetInt64(out var n) ? n : 0;
            return c < 0 || c > i ? null : new(i, c, o);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

public sealed record CodexSendLog(string Id, DateTimeOffset SentAt, string Purpose, string Model, string Prompt, string Schema, string Status, CodexUsage? Usage = null)
{
    public string JstTime => SentAt.ToOffset(TimeSpan.FromHours(9)).ToString("MM/dd HH:mm:ss");
    public string Tokens => Usage?.TotalTokens.ToString("N0") ?? "不明";
    public string InputTokens => Usage?.InputTokens.ToString("N0") ?? "未取得";
    public string OutputTokens => Usage?.OutputTokens.ToString("N0") ?? "未取得";
}
public sealed record CodexDailyUsage(string Day, long Attempts = 0, long Succeeded = 0, long Failed = 0, long UnknownUsage = 0, long InputTokens = 0, long CachedInputTokens = 0, long OutputTokens = 0)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>原文の削除と日次集計を同一トランザクションにして二重計上を防ぐ。</summary>
public sealed class CodexJournal
{
    private readonly string _connection;
    public CodexJournal(AgentPaths paths)
    {
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.Root, "data", "codex.sqlite"), Pooling = false }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS sends(id TEXT PRIMARY KEY,at INTEGER NOT NULL,payload BLOB NOT NULL); CREATE TABLE IF NOT EXISTS daily(day TEXT PRIMARY KEY,payload BLOB NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;"; cmd.ExecuteNonQuery(); return db;
    }
    private static byte[] Encode<T>(T value) => SecureStore.Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonFormat.Options));
    private static T Decode<T>(object value) => JsonSerializer.Deserialize<T>(SecureStore.Unprotect((byte[])value), JsonFormat.Options)!;
    public void Begin(CodexSendLog log)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sends(id,at,payload) VALUES($id,$at,$p)";
        cmd.Parameters.AddWithValue("$id", log.Id); cmd.Parameters.AddWithValue("$at", log.SentAt.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$p", Encode(log)); cmd.ExecuteNonQuery();
    }
    public void Complete(CodexSendLog log)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE sends SET payload=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", log.Id); cmd.Parameters.AddWithValue("$p", Encode(log)); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<CodexSendLog> Recent(int limit = 500)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM sends ORDER BY at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit); using var reader = cmd.ExecuteReader(); var rows = new List<CodexSendLog>();
        while (reader.Read()) rows.Add(Decode<CodexSendLog>(reader[0])); return rows;
    }
    public IReadOnlyList<CodexDailyUsage> Summaries()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM daily ORDER BY day DESC";
        using var reader = cmd.ExecuteReader(); var rows = new List<CodexDailyUsage>();
        while (reader.Read()) rows.Add(Decode<CodexDailyUsage>(reader[0])); return rows;
    }
    public int Rollup(DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.ToOffset(TimeSpan.FromHours(9)).Date, TimeSpan.FromHours(9));
        using var db = Open(); using var transaction = db.BeginTransaction();
        var expired = new List<CodexSendLog>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = transaction; cmd.CommandText = "SELECT payload FROM sends WHERE at < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", midnight.ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = Decode<CodexSendLog>(reader[0]);
                if (row.Status != "送信中" || row.SentAt < now.AddMinutes(-5)) expired.Add(row);
            }
        }
        foreach (var group in expired.GroupBy(r => r.SentAt.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd")))
        {
            CodexDailyUsage total;
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = transaction; cmd.CommandText = "SELECT payload FROM daily WHERE day=$day"; cmd.Parameters.AddWithValue("$day", group.Key);
                var old = cmd.ExecuteScalar(); total = old is null ? new(group.Key) : Decode<CodexDailyUsage>(old);
            }
            total = total with { Attempts = total.Attempts + group.LongCount(), Succeeded = total.Succeeded + group.LongCount(r => r.Status == "成功"), Failed = total.Failed + group.LongCount(r => r.Status != "成功"), UnknownUsage = total.UnknownUsage + group.LongCount(r => r.Usage is null), InputTokens = total.InputTokens + group.Sum(r => r.Usage?.InputTokens ?? 0), CachedInputTokens = total.CachedInputTokens + group.Sum(r => r.Usage?.CachedInputTokens ?? 0), OutputTokens = total.OutputTokens + group.Sum(r => r.Usage?.OutputTokens ?? 0) };
            using var save = db.CreateCommand(); save.Transaction = transaction;
            save.CommandText = "INSERT INTO daily(day,payload) VALUES($day,$p) ON CONFLICT(day) DO UPDATE SET payload=$p";
            save.Parameters.AddWithValue("$day", group.Key); save.Parameters.AddWithValue("$p", Encode(total)); save.ExecuteNonQuery();
            foreach (var row in group)
            {
                using var delete = db.CreateCommand(); delete.Transaction = transaction; delete.CommandText = "DELETE FROM sends WHERE id=$id";
                delete.Parameters.AddWithValue("$id", row.Id); delete.ExecuteNonQuery();
            }
        }
        transaction.Commit(); return expired.Count;
    }
}

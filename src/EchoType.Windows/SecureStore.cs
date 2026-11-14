using EchoType.Core;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace EchoType.Windows;

/// <summary>本文をDPAPIで保護し検索時だけ復号する。平文の全文索引を作らない。</summary>
public sealed partial class SecureStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _sync = new();
    public AgentPaths Paths { get; }
    public SecureStore(AgentPaths paths)
    {
        Paths = paths;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.Root, "data", "echo.sqlite"), Pooling = false }.ToString());
        _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON; PRAGMA busy_timeout=5000; " +
            "CREATE TABLE IF NOT EXISTS entries(kind TEXT NOT NULL,id TEXT NOT NULL,created INTEGER NOT NULL,expires INTEGER,payload BLOB NOT NULL,PRIMARY KEY(kind,id)); " +
            "CREATE INDEX IF NOT EXISTS ix_entries_date ON entries(kind,created DESC);";
        command.ExecuteNonQuery();

    }
    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    private bool Put<T>(string kind, string id, T value, DateTimeOffset created, DateTimeOffset? expires = null, bool insertOnly = false) =>
        PutEntry(kind, id, value, created, expires, insertOnly);

    private bool PutEntry<T>(string kind, string id, T value, DateTimeOffset created, DateTimeOffset? expires = null, bool insertOnly = false, SqliteTransaction? transaction = null)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonFormat.Options);
        byte[] payload;
        try { payload = Protect(plain); } finally { Array.Clear(plain); }
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO entries(kind,id,created,expires,payload) VALUES($k,$i,$c,$e,$p) " +
                (insertOnly ? "ON CONFLICT(kind,id) DO NOTHING" : "ON CONFLICT(kind,id) DO UPDATE SET created=$c,expires=$e,payload=$p");
            cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$c", created.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$e", (object?)expires?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", payload);
            return cmd.ExecuteNonQuery() == 1;
        }
    }
    private IReadOnlyList<T> Read<T>(string kind, int limit = 100, Func<T, bool>? predicate = null, DateTime? day = null, DateTimeOffset? from = null, DateTimeOffset? until = null)
    {
        var payloads = new List<byte[]>();
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM entries WHERE kind=$k AND (expires IS NULL OR expires>$now)" +
                (day is null ? "" : " AND created >= $from AND created < $until") +
                (from is null ? "" : " AND created >= $rangeFrom") + (until is null ? "" : " AND created < $rangeUntil") + " ORDER BY created DESC";
            cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (from is { } lower) cmd.Parameters.AddWithValue("$rangeFrom", lower.ToUnixTimeMilliseconds());
            if (until is { } upper) cmd.Parameters.AddWithValue("$rangeUntil", upper.ToUnixTimeMilliseconds());
            if (day is { } date)
            {
                cmd.Parameters.AddWithValue("$from", new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Local)).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$until", new DateTimeOffset(DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Local)).ToUnixTimeMilliseconds());
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && (predicate is not null || payloads.Count < limit)) payloads.Add((byte[])reader[0]);
        }
        // 一覧の復号・JSON処理中は、録音保存や他画面のDBアクセスを待たせない。
        var list = new List<T>();
        foreach (var payload in payloads)
        {
            if (list.Count >= limit) break;
            var plain = Unprotect(payload);
            try
            {
                var value = JsonSerializer.Deserialize<T>(plain, JsonFormat.Options)!;
                if (predicate is null || predicate(value)) list.Add(value);
            }
            finally { Array.Clear(plain); }
        }
        return list;
    }
    private void Delete(string kind, string id, SqliteTransaction? transaction = null)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = "DELETE FROM entries WHERE kind=$k AND id=$i";
            cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$i", id); cmd.ExecuteNonQuery();
        }
    }
    public AppSettings LoadSettings() => AsrEngines.MigrateSelections(Read<AppSettings>("settings", 1).FirstOrDefault() ?? new());
    public void SaveSettings(AppSettings settings) => Put("settings", "current", AsrEngines.MigrateSelections(settings), DateTimeOffset.UtcNow);
    public static void AtomicWrite(string path, byte[] contents)
    {
        var staging = path + ".tmp"; File.WriteAllBytes(staging, contents); File.Move(staging, path, true);
    }
    public void Dispose() { lock (_sync) _db.Dispose(); }
}

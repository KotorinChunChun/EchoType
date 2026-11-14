using EchoType.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoType.Windows;

public sealed record CaptionDllContext(Guid SpeechId, RecognitionPurpose Purpose, AudioSourceKind Source);
internal sealed record CaptionDllDrain(string Text, long Characters, bool Truncated);

/// <summary>発言・秘密値を保存せず、再発時の失敗段階を残す。</summary>
internal sealed class CaptionDllAttempt(string root, string command, CaptionDllContext? context = null) : IDisposable
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, object?> _details = [];
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Stage { get; set; } = "受付";
    public string Outcome { get; set; } = "失敗";
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public string FailureKind { get; private set; } = "未判定";
    private bool _disposed;

    public void Audio(byte[] wave)
    {
        _details["inputBytes"] = wave.Length;
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wave, false));
        _details["inputSeconds"] = reader.TotalTime.TotalSeconds;
        _details["sampleRate"] = reader.WaveFormat.SampleRate;
        _details["bitsPerSample"] = reader.WaveFormat.BitsPerSample;
        _details["channels"] = reader.WaveFormat.Channels;
    }
    public void Binaries(string host, string backend)
    {
        _details["host"] = CaptionDllJournal.Fingerprint(host);
        _details["backend"] = CaptionDllJournal.Fingerprint(backend);
        _details["core"] = CaptionDllJournal.Fingerprint(Path.Combine(Path.GetDirectoryName(backend)!, "LiveCaptions", "Microsoft.CognitiveServices.Speech.core.dll"));
    }
    public void Failed(Exception error)
    {
        if (Outcome != "時間切れ") Outcome = error is OperationCanceledException ? "取消" : "失敗";
        _details["exceptionType"] = error.GetType().Name;
        _details["exceptionHResult"] = $"0x{error.HResult:X8}";
    }
    public void Native(CaptionDllDrain stdout, CaptionDllDrain stderr)
    {
        _details["stdoutCharacters"] = stdout.Characters; _details["stderrCharacters"] = stderr.Characters;
        _details["stdoutTruncated"] = stdout.Truncated; _details["stderrTruncated"] = stderr.Truncated;
        var steps = new List<string>(); var events = 0; var finalEvents = 0; var unknown = 0; var malformed = 0;
        Dictionary<string, object?>? summary = null;
        foreach (var line in stdout.Text.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            // 名称を固定して、モデル設定・本文・パスを混入させない。
            if (Regex.IsMatch(value, @"^(RoInitialize|DllGetActivationFactory\(LiveCaptionsBackend\.CaptionBackend\)|ActivateInstance|GetRuntimeClassName|GetIids|QueryInterface\(actual IID\)|UpdateSpeechModelLocaleFromSettings)=0x[0-9a-fA-F]{1,8}$") ||
                value == "BackendConfig=created (contents not read)" || Regex.IsMatch(value, @"^LoadLibrary Win32=\d{1,10}$"))
            { if (steps.Count < 64) steps.Add(value); continue; }
            if (!value.StartsWith('{')) { if (value.Length > 0) unknown++; continue; }
            try
            {
                using var doc = JsonDocument.Parse(value); var item = doc.RootElement;
                if (!item.TryGetProperty("kind", out var kind)) { unknown++; continue; }
                if (kind.GetString() == "backend_recognition")
                {
                    events++;
                    if (item.TryGetProperty("is_final", out var final) && final.ValueKind == JsonValueKind.True) finalEvents++;
                    // textは読まない・保存しない。
                }
                else if (kind.GetString() is "summary" or "ready")
                {
                    summary = [];
                    foreach (var key in new[] { "success", "ready", "recognition_seconds", "final_count", "cancel_reason", "cancel_error" })
                    {
                        if (!item.TryGetProperty(key, out var property)) continue;
                        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False) summary[key] = property.GetBoolean();
                        else if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number) && double.IsFinite(number)) summary[key] = number;
                    }
                }
                else unknown++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { malformed++; }
        }
        var markers = new[] { "Backend ABI mismatch", "Unsupported Windows DLL version", "PCM file missing" }.Where(stderr.Text.Contains).ToArray();
        var codes = Regex.Matches(stderr.Text, @"(?i)(?:error code|HRESULT|Win32)[:=\s]+(0x[0-9a-f]{1,8}|\d{1,10})").Select(m => m.Value).Distinct().Take(16).ToArray();
        _details["nativeSteps"] = steps; _details["nativeSummary"] = summary;
        _details["recognitionEvents"] = events; _details["finalEvents"] = finalEvents;
        _details["unknownLines"] = unknown; _details["malformedLines"] = malformed;
        _details["stderrMarkers"] = markers; _details["stderrCodes"] = codes;
        FailureKind = ExitCode == 65 ? "未検証ABI" : markers.Length > 0 ? markers[0] :
            summary?.GetValueOrDefault("cancel_error") is double error && error != 0 ? "認識エラー" :
            summary?.GetValueOrDefault("final_count") is double count && count == 0 ? "確定文字なし" :
            ExitCode == 0 ? "正常終了" : "ホスト失敗";
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        CaptionDllJournal.Append(root, new
        {
            Id, StartedAt = _started, FinishedAt = DateTimeOffset.UtcNow, Seconds = _watch.Elapsed.TotalSeconds,
            Command = command, Purpose = context?.Purpose.ToString() ?? "Warmup", Source = context?.Source.ToString(),
            SpeechId = context?.SpeechId.ToString("N"), Stage, Outcome, FailureKind, ProcessId, ExitCode,
            AppVersion = typeof(WindowsCaptionDll).Assembly.GetName().Version?.ToString(),
            WindowsVersion = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Details = _details
        });
    }
}

internal static class CaptionDllJournal
{
    internal const int FileLimit = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions LogFormat = new(JsonFormat.Options) { WriteIndented = false };
    private static readonly Dictionary<string, (long Length, DateTime Modified, object Value)> Fingerprints = [];
    internal static object Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new { Exists = false };
            lock (Sync)
            {
                if (Fingerprints.TryGetValue(path, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc) return old.Value;
                using var input = File.OpenRead(path);
                var value = new { Exists = true, Bytes = info.Length, Version = FileVersionInfo.GetVersionInfo(path).FileVersion,
                    ModifiedAt = info.LastWriteTimeUtc, SHA256 = Convert.ToHexString(SHA256.HashData(input)) };
                Fingerprints[path] = (info.Length, info.LastWriteTimeUtc, value); return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new { ErrorType = ex.GetType().Name, HResult = $"0x{ex.HResult:X8}" }; }
    }
    public static bool Append<T>(string root, T record, DateTimeOffset? at = null)
    {
        try
        {
            var today = (at ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(9)).Date;
            var folder = Path.Combine(root, "data", "diagnostics", "caption-dll");
            var line = JsonSerializer.Serialize(record, LogFormat) + "\n";
            if (Encoding.UTF8.GetByteCount(line) > 65536) return false;
            lock (Sync)
            {
                Directory.CreateDirectory(folder);
                AgentPaths.RequireLocalPath(folder, true);
                foreach (var entry in Directory.EnumerateFiles(folder, "caption-dll-*.jsonl"))
                {
                    var match = Regex.Match(Path.GetFileName(entry), @"^caption-dll-(\d{8})(?:-previous)?\.jsonl$");
                    if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var day) && day < today.AddDays(-6)) File.Delete(entry);
                }
                var file = Path.Combine(folder, $"caption-dll-{today:yyyyMMdd}.jsonl");
                if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) > FileLimit)
                    File.Move(file, Path.Combine(folder, $"caption-dll-{today:yyyyMMdd}-previous.jsonl"), true);
                File.AppendAllText(file, line, new UTF8Encoding(false)); return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Trace.TraceWarning("Windows字幕DLLの診断ログを保存できませんでした: " + ex.GetType().Name); return false; }
    }
    internal static async Task<CaptionDllDrain> DrainAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; long total = 0; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        { total += count; if (text.Length < limit) text.Append(buffer, 0, Math.Min(count, limit - text.Length)); }
        return new(text.ToString(), total, total > limit);
    }
}

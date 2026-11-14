using EchoType.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoType.Windows;

/// <summary>許可リストの推論設定だけを保存。本文・例外原文・鍵・モデルパスは記録しない。</summary>
public static class EngineSettingsJournal
{
    private static readonly object Sync = new();
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions Format = new(JsonFormat.Options) { WriteIndented = false };
    public static string Folder(AgentPaths paths) => Path.Combine(paths.Root, "data", "diagnostics", "engine-settings");
    public static bool Change(AgentPaths paths, string stage, AppSettings previous, AppSettings saved, AppSettings effective, Exception? error = null, bool voice = false) => Write(paths, new
    {
        Stage = stage, Scope = voice ? "voice-input" : "main",
        Previous = RecognitionEngineSettings.Values(previous), Saved = RecognitionEngineSettings.Values(saved), Effective = RecognitionEngineSettings.Values(effective),
        SettingsChanged = typeof(AppSettings).GetProperties().Where(p => JsonSerializer.Serialize(p.GetValue(previous)) != JsonSerializer.Serialize(p.GetValue(saved))).Select(p => p.Name).ToArray(),
        Changed = RecognitionEngineSettings.Values(saved).Where(p => !Equals(p.Value, RecognitionEngineSettings.Values(previous)[p.Key])).Select(p => p.Key).ToArray(),
        Pending = RecognitionEngineSettings.Values(saved).Where(p => !Equals(p.Value, RecognitionEngineSettings.Values(effective)[p.Key])).Select(p => p.Key).ToArray(),
        Error = SafeError(error)
    });
    public static void Failure(AgentPaths paths, string stage, string engine, AppSettings effective, Exception error) => Write(paths, new
    {
        Stage = stage, Engine = AsrEngines.Get(engine).Id, Effective = RecognitionEngineSettings.Values(effective), Error = SafeError(error)
    });
    private static object? SafeError(Exception? error) => error is null ? null : new
    {
        Type = error.GetType().Name, HResult = $"0x{error.HResult:X8}",
        WorkerPid = error.Data["AsrWorkerPid"] as int?, NativeCode = error.Data["AsrNativeCode"] as string,
        NativeStage = error.Data["AsrNativeStage"] as string, Libraries = error.Data["AsrLibraries"] as string[],
        Markers = new[] { "CUDA", "cuDNN", "cuBLAS", "out of memory", "compute type", "DLL", "timeout", "補助プロセス", "時間切れ" }.Where(s => error.Message.Contains(s, StringComparison.OrdinalIgnoreCase)).ToArray()
    };
    private static bool Write<T>(AgentPaths paths, T details)
    {
        try
        {
            var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date;
            var line = JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Session, ProcessId = Environment.ProcessId, Version = typeof(EngineSettingsJournal).Assembly.GetName().Version?.ToString(), Details = details }, Format) + "\n";
            lock (Sync)
            {
                var folder = Folder(paths); Directory.CreateDirectory(folder); AgentPaths.RequireLocalPath(folder, true);
                foreach (var file in Directory.EnumerateFiles(folder, "engine-settings-*.jsonl"))
                {
                    var match = Regex.Match(Path.GetFileName(file), @"^engine-settings-(\d{8})(?:-previous)?\.jsonl$");
                    if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var day) && day < today.AddDays(-6)) File.Delete(file);
                }
                var current = Path.Combine(folder, $"engine-settings-{today:yyyyMMdd}.jsonl");
                AgentPaths.RequireLocalPath(folder, true);
                if (File.Exists(current)) AgentPaths.RequireLocalPath(current);
                if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) > 2 * 1024 * 1024)
                    File.Move(current, Path.Combine(folder, $"engine-settings-{today:yyyyMMdd}-previous.jsonl"), true);
                File.AppendAllText(current, line, new UTF8Encoding(false));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Trace.TraceWarning("推論設定の診断ログを保存できません: " + ex.GetType().Name); return false; }
    }
}

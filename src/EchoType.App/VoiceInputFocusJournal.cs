using EchoType.Windows;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EchoType.App;

/// <summary>入力文字列・画面名・要素名を含めない、前面化要求の照合記録。</summary>
internal static class VoiceInputFocusJournal
{
    private static readonly object Sync = new();
    internal static void Write(AgentPaths paths, string operation, string stage, string reason, nint target)
    {
        try
        {
            var root = target == 0 ? 0 : GetAncestor(target, 2);
            var targetThread = GetWindowThreadProcessId(target, out var targetPid);
            var foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out var foregroundPid);
            var line = JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
                ThreadId = GetCurrentThreadId(), Version = typeof(VoiceInputFocusJournal).Assembly.GetName().Version?.ToString(),
                OperationId = operation, Stage = stage, Reason = reason, Target = target.ToInt64(), Root = root.ToInt64(),
                TargetPid = targetPid, TargetThreadId = targetThread, Foreground = foreground.ToInt64(), ForegroundPid = foregroundPid }) + "\n";
            lock (Sync)
            {
                var folder = Path.Combine(paths.Root, "data", "diagnostics", "voice-input-focus"); Directory.CreateDirectory(folder);
                var day = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date;
                foreach (var old in Directory.EnumerateFiles(folder, "focus-*.jsonl"))
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(old)[6..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) && date < day.AddDays(-6)) File.Delete(old);
                var file = Path.Combine(folder, $"focus-{day:yyyyMMdd}.jsonl");
                if (!File.Exists(file) || new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) <= 2 * 1024 * 1024)
                    File.AppendAllText(file, line, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

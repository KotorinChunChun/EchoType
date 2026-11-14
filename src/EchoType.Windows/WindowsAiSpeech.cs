using EchoType.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EchoType.Windows;

/// <summary>Windows AI SDKは別プロセスへ隔離。同じ前処理済みWAVを渡し、モデル読込を認識時間から除く。</summary>
public static class WindowsAiSpeech
{
    public const string Engine = "windows-ai-speech";
    private static readonly SemaphoreSlim StatusLock = new(1, 1);
    private static string _status = "状態未確認。「導入する」でWindows AI音声モデルを確認・準備できます。";
    private static int _ready;
    private static string _prepareFailure = "";
    public static string Status => Volatile.Read(ref _status);
    public static bool Ready => Volatile.Read(ref _ready) == 1;
    public static event Action? StatusChanged;
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "WindowsSpeech", "EchoType.WindowsSpeech.exe");

    public static async Task RefreshStatusAsync(CancellationToken ct)
    {
        if (!await StatusLock.WaitAsync(0, ct)) return;
        try
        {
            var result = await RunAsync("--status", null, TimeSpan.FromSeconds(20), ct);
            SetStatus(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { SetStatus(new("Unavailable", "", ex.Message, null)); }
        finally { StatusLock.Release(); }
    }

    public static async Task InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        await StatusLock.WaitAsync(ct);
        try
        {
        Volatile.Write(ref _prepareFailure, "");
        progress.Report("Windows AI音声モデルを準備中です。");
        var result = await RunAsync("--install", null, TimeSpan.FromMinutes(20), ct);
        SetStatus(result);
        if (!Ready) throw new InvalidOperationException(Status);
        }
        finally { StatusLock.Release(); }
    }

    public static async Task<AsrResult> RecognizeAsync(AgentPaths paths, byte[] wave, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file = Path.Combine(paths.Root, "cache", "windows-ai-" + Guid.NewGuid().ToString("N") + ".wav");
        var timer = Stopwatch.StartNew();
        try
        {
            await File.WriteAllBytesAsync(file, wave, ct);
            var result = await RunAsync("--recognize", file, TimeSpan.FromMinutes(2), ct);
            SetStatus(result);
            var error = result.Stage == "Readiness" && Volatile.Read(ref _prepareFailure).Length > 0 ? Status : result.Error;
            return new(Engine, result.Text, timer.Elapsed.TotalSeconds, error)
                { RecognitionSeconds = result.RecognitionSeconds, ComputeProfile = "Windows AI / 言語自動判定",
                    ModelError = error.Length > 0 && result.Stage is "Initialize" or "Readiness" or "Create" };
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    internal static void SetStatus(Response result)
    {
        Volatile.Write(ref _ready, result.State == "Ready" ? 1 : 0);
        if (Ready) Volatile.Write(ref _prepareFailure, "");
        else if (result.Stage == "Prepare" && result.Error.Length > 0) Volatile.Write(ref _prepareFailure, result.Error);
        var priorFailure = Volatile.Read(ref _prepareFailure);
        var status = !Ready && priorFailure.Length > 0 && (result.Error.Length == 0 || result.Stage is "Readiness" or "Prepare") ? priorFailure : result.Error.Length > 0 ? result.Error : result.State switch
        {
            "Ready" => "導入済み",
            "NotCompatibleWithSystemHardware" or "NotSupportedOnCurrentSystem" => "この環境ではWindows AI音声モデルを利用できません。OS・SDK・ハードウェアの対応状況を確認してください。",
            _ => $"Windows AI: {result.State}。「導入する」でモデルを準備してください。"
        };
        if (Interlocked.Exchange(ref _status, status) != status) StatusChanged?.Invoke();
    }

    private static async Task<Response> RunAsync(string command, string? audioFile, TimeSpan limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new AsrModelException("Windows AI音声認識にはWindows 11 24H2以降が必要です。");
        if (!File.Exists(Executable)) throw new AsrModelException("Windows AI音声認識の補助プログラムがありません。最新版の配布フォルダーを使用してください。");
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(command); if (audioFile is not null) info.ArgumentList.Add(audioFile);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows AI音声認識を起動できません。");
        using var job = new ProcessJob(); job.Assign(process);
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var json = await output; await errors;
            if (json.Length > 1000000) throw new InvalidOperationException("Windows AI音声認識の応答が長すぎます。");
            var result = JsonSerializer.Deserialize<Response>(json) ?? throw new InvalidOperationException("Windows AI音声認識の応答がありません。");
            if (process.ExitCode != 0 && result.Error.Length == 0) throw new InvalidOperationException($"Windows AI音声認識が異常終了しました（{process.ExitCode}）。");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("Windows AI音声認識が時間切れになりました。"); }
        catch (JsonException) { throw new InvalidOperationException("Windows AI音声認識の応答を取得できません。SDKまたはWindowsの対応状況を確認してください。"); }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync(); await output; await errors;
        }
    }
    internal sealed record Response(string State, string Text, string Error, double? RecognitionSeconds, string? Stage = null);
}

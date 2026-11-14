using EchoType.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EchoType.Windows;

/// <summary>Backendの設定生成とPCM直接入力。UIA・OSの録音デバイスを使用しない。</summary>
public static class WindowsCaptionDll
{
    private static readonly SemaphoreSlim StatusLock = new(1, 1);
    private static string _status = "DLL方式の準備状態は未確認です。モデルの状態を確認してください。";
    public static string Status => Volatile.Read(ref _status);
    public static bool Ready => Status == "導入済み";
    public static event Action? StatusChanged;
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "CaptionDll", "CaptionDllHost.exe");
    private static string Backend => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "SxS",
        "MicrosoftWindows.61869721.Livtop_cw5n1h2txyewy", "LiveCaptionsBackendDll.dll");

    public static async Task RefreshStatusAsync(CancellationToken ct, AgentPaths? paths = null)
    {
        await StatusLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var attempt = new CaptionDllAttempt(paths?.Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoType"), "--status");
            try
            {
                var response = await RunAsync("--status", null, attempt, ct).ConfigureAwait(false);
                attempt.Outcome = "成功";
                SetStatus(response.Ready ? "導入済み" : "日本語のライブキャプションモデルをWindows側で準備してください。");
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { attempt.Failed(ex); throw; }
            catch (Exception ex) { attempt.Failed(ex); SetStatus(ex.Message + " 診断ID: " + attempt.Id); }
        }
        finally { StatusLock.Release(); }
    }
    private static void SetStatus(string value) { if (Interlocked.Exchange(ref _status, value) != value) StatusChanged?.Invoke(); }

    public static async Task<AsrResult> RecognizeAsync(AgentPaths paths, byte[] wave, CancellationToken ct, CaptionDllContext? context = null)
    {
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        using var attempt = new CaptionDllAttempt(paths.Root, "--file", context ?? new(Guid.Empty, RecognitionPurpose.Full, AudioSourceKind.Microphone));
        var directory = Path.Combine(paths.Root, "cache");
        var file = Path.Combine(directory, "caption-dll-" + Guid.NewGuid().ToString("N") + ".wav");
        byte[]? converted = null;
        try
        {
            attempt.Stage = "音声変換";
            attempt.Audio(wave);
            converted = ConvertAudio(wave);
            attempt.Stage = "一時音声保存";
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(file, converted, ct).ConfigureAwait(false);
            var result = await RunAsync("--file", file, attempt, ct).ConfigureAwait(false);
            attempt.Outcome = "成功";
            SetStatus("導入済み");
            return new(AsrEngines.WindowsCaptionsDll, result.Text, watch.Elapsed.TotalSeconds)
            {
                RecognitionSeconds = result.Seconds, InputVerified = true,
                ComputeProfile = "Windows字幕DLL / ja-JP / 16kHz PCM直接入力"
            };
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { attempt.Failed(ex); throw; }
        catch (Exception ex)
        {
            attempt.Failed(ex);
            if (ex is AsrModelException) throw new AsrModelException(ex.Message + " 診断ID: " + attempt.Id, ex);
            throw new InvalidOperationException(ex.Message + " 診断ID: " + attempt.Id, ex);
        }
        finally
        {
            if (converted is not null) Array.Clear(converted);
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { attempt.Stage = "一時音声削除"; attempt.Failed(ex); throw new InvalidOperationException("DLL認識の一時音声を削除できませんでした。診断ID: " + attempt.Id, ex); }
        }
    }

    internal static byte[] ConvertAudio(byte[] wave)
    {
        using var reader = new WaveFileReader(new MemoryStream(wave, false));
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 ||
            reader.WaveFormat.SampleRate is < 8000 or > 48000 || reader.Length > 48000L * 300 || reader.Length % 2 != 0)
            throw new InvalidOperationException("DLL認識には16bit・モノラル、300秒以内のPCM音声が必要です。");
        var provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000).ToWaveProvider16();
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(output), new WaveFormat(16000, 16, 1)))
        {
            var buffer = new byte[32000];
            try { int count; while ((count = provider.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, count); }
            finally { Array.Clear(buffer); }
        }
        return output.ToArray();
    }

    internal sealed record Response(bool Ready, string Text, double? Seconds);
    internal static Response ParseResponse(string output, int exitCode, bool status)
    {
        if (exitCode == 65) throw new AsrModelException("Windows字幕DLLが未検証の版です。Windows更新後のABI確認が必要です。");
        if (exitCode != 0) throw new InvalidOperationException($"Windows字幕DLLの認識に失敗しました（終了コード {exitCode}）。日本語モデルの準備・音声形式を確認してください。");
        if (output.Length > 8_000_000) throw new InvalidOperationException("Windows字幕DLLの応答が上限を超えました。");
        var text = new StringBuilder(); bool ready = false, success = false; double? seconds = null; int finalCount = -1, actualFinals = 0;
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith('{')) continue;
            using var json = JsonDocument.Parse(line); var item = json.RootElement;
            switch (item.GetProperty("kind").GetString())
            {
                case "ready": ready = item.GetProperty("ready").GetBoolean(); break;
                case "backend_recognition":
                    if (item.GetProperty("is_final").GetBoolean() && item.GetProperty("text").GetString() is { Length: > 0 } value)
                    { text.Append(value); actualFinals++; }
                    break;
                case "summary":
                    success = item.GetProperty("success").GetBoolean(); seconds = item.GetProperty("recognition_seconds").GetDouble(); finalCount = item.GetProperty("final_count").GetInt32(); break;
            }
        }
        if (status && ready) return new(true, "", null);
        if (status || !success || text.Length == 0 || finalCount != actualFinals || seconds is null || !double.IsFinite(seconds.Value) || seconds < 0)
            throw new InvalidOperationException("Windows字幕DLLの確定結果を取得できませんでした。");
        return new(true, text.ToString(), seconds);
    }

    private static async Task<Response> RunAsync(string command, string? file, CaptionDllAttempt attempt, CancellationToken ct)
    {
        attempt.Stage = "ホスト準備";
        ct.ThrowIfCancellationRequested();
        attempt.Binaries(Executable, Backend);
        if (!Environment.Is64BitProcess || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new AsrModelException("Windows字幕DLLは検証済みのx64環境が必要です。");
        if (!File.Exists(Executable)) throw new AsrModelException("Windows字幕DLLの補助プログラムがありません。配布フォルダー全体を更新してください。");
        if (!File.Exists(Backend)) throw new AsrModelException("検証対象のWindows字幕Backendがありません。このWindows版は未対応です。");
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(Backend); info.ArgumentList.Add(command); if (file is not null) info.ArgumentList.Add(file);
        attempt.Stage = "ホスト起動";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows字幕DLLの補助プログラムを起動できません。");
        attempt.ProcessId = process.Id;
        using var job = new ProcessJob(); job.Assign(process);
        var stdout = CaptionDllJournal.DrainAsync(process.StandardOutput, 8_000_001); var stderr = CaptionDllJournal.DrainAsync(process.StandardError, 64000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(command == "--status" ? 20 : 75));
        try
        {
            attempt.Stage = "ホスト実行";
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            attempt.Stage = "応答解析";
            return ParseResponse((await stdout.ConfigureAwait(false)).Text, process.ExitCode, command == "--status");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { attempt.Outcome = "時間切れ"; throw new InvalidOperationException("Windows字幕DLLの認識が時間切れになりました。"); }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync().ConfigureAwait(false);
            attempt.ExitCode = process.ExitCode;
            attempt.Native(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }
}

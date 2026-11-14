using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EchoType.Core;
using NAudio.Wave;

namespace EchoType.Windows;

/// <summary>speech.cppのWindows専用子プロセスを共有。音声は標準入力だけへ渡す。</summary>
public sealed class HaasNativeWorker : IDisposable
{
    public const string Root = "C:/AI/Models/Speech/Haas";
    public const string ModelFile = "Haas-616M-ja-v1.0-Q8_0.gguf";
    internal string Residency
    {
        get { lock (_sync) return _process is { HasExited: false } ? "ロード済み" : "未ロード"; }
    }
    internal async Task UnloadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { Reset(); }
        finally { _gate.Release(); }
    }
    public static string Executable => Path.Combine(Root, "speech-0.10.0", "speech.exe");
    public static string InstallationStatus
    {
        get
        {
            try
            {
                if (File.Exists(Executable) && File.Exists(Path.Combine(Root, "speech-0.10.0", "speech.dll")) &&
                    new FileInfo(Path.Combine(Root, ModelFile)).Length == 656381472)
                {
                    using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "ready.json")));
                    if (marker.RootElement.GetProperty("version").GetString() == "0.10.0") return "導入済み";
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { }
            return "HaasのWindows実行環境がありません。「導入する」で準備できます（約656MB）。";
        }
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Process? _process;
    private string _profile = "", _observed = "未確認";
    private bool _disposed;
    private readonly Func<string, int, ProcessStartInfo> _start;
    public HaasNativeWorker() : this(StartInfo) { }
    internal HaasNativeWorker(Func<string, int, ProcessStartInfo> start) => _start = start;
    public static ProcessStartInfo StartInfo(string device, int threads)
    {
        if (InstallationStatus != "導入済み") throw new InvalidOperationException(InstallationStatus);
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "worker", Path.Combine(Root, ModelFile), "--device", device == "cpu" ? "cpu" : "gpu", "--threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-warmup" }) info.ArgumentList.Add(arg);
        return info;
    }
    private static async Task<JsonDocument> ReadAsync(Process process, CancellationToken ct)
    {
        var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null) throw new AsrModelException("Haasワーカーが終了しました。モデル・Vulkanドライバーを確認してください。");
        if (line.Length > 2_000_000) throw new InvalidOperationException("Haasの応答が上限を超えました。");
        return JsonDocument.Parse(line);
    }
    private async Task<(Process Process, double LoadSeconds, bool Reused)> PrepareAsync(string device, int threads, CancellationToken ct)
    {
        var profile = device + "/" + threads;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false } current && _profile == profile) return (current, 0, true);
        }
        Reset(); var watch = Stopwatch.StartNew();
        var process = Process.Start(_start(device, threads)) ?? throw new InvalidOperationException("Haasを起動できませんでした。");
        lock (_sync) { if (_disposed) { process.Kill(true); process.Dispose(); throw new ObjectDisposedException(nameof(HaasNativeWorker)); } _process = process; }
        // エラーに本文やパスを含み得るためstderrを診断ログへ転記しない。
        _ = DrainAsync(process.StandardError);
        using var ready = await ReadAsync(process, ct);
        var value = ready.RootElement;
        if (value.GetProperty("type").GetString() != "ready" || value.GetProperty("protocol").GetInt32() != 4 ||
            value.GetProperty("version").GetString() != "0.10.0") throw new InvalidOperationException("Haasのモデル形式・ワーカーの版が一致しません。再導入してください。");
        var model = value.GetProperty("model");
        if (model.GetProperty("name").GetString() != "Haas-ja-v1.0" || model.GetProperty("layout").GetInt32() != 4 || model.GetProperty("weight_type").GetString() != "Q8_0")
            throw new InvalidOperationException("Haasのモデル識別が一致しません。");
        _observed = model.TryGetProperty("device", out var actual) ? actual.ToString() : "未確認";
        if (device == "cpu" ? _observed != "CPU" : !_observed.StartsWith("Vulkan", StringComparison.Ordinal))
            throw new InvalidOperationException("Haasが要求したCPU/Vulkanデバイスを報告しませんでした。");
        _profile = profile;
        return (process, watch.Elapsed.TotalSeconds, false);
    }
    private static async Task DrainAsync(StreamReader reader)
    { try { while (await reader.ReadLineAsync() is not null) { } } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
    public Task<AsrResult> WarmupAsync(string device, int threads, CancellationToken ct, Action<ModelPhase, string?>? progress = null) => ExecuteAsync(null, device, threads, ct, progress);
    public Task<AsrResult> RecognizeAsync(byte[] wave, string device, int threads, CancellationToken ct, Action<ModelPhase, string?>? progress = null) => ExecuteAsync(wave, device, threads, ct, progress);
    private async Task<AsrResult> ExecuteAsync(byte[]? wave, string device, int threads, CancellationToken ct, Action<ModelPhase, string?>? progress)
    {
        progress?.Invoke(ModelPhase.WorkerWaiting, null);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var watch = Stopwatch.StartNew();
        try
        {
            progress?.Invoke(ModelPhase.Loading, null);
            (Process Process, double LoadSeconds, bool Reused) session;
            try { session = await PrepareAsync(device, threads, timeout.Token); }
            catch (Exception ex) when (ex is not (OperationCanceledException or AsrModelException)) { throw new AsrModelException(ex.Message, ex); }
            progress?.Invoke(wave is null ? ModelPhase.Processing : ModelPhase.Inferencing, _observed);
            var text = new StringBuilder(); var recognition = Stopwatch.StartNew();
            if (wave is not null)
            {
                using var reader = new WaveFileReader(new MemoryStream(wave, false));
                if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 || reader.Length > 32_000_000)
                    throw new InvalidOperationException("Haasには16bitモノラルPCMのWAVが必要です。");
                var pcm = new byte[(int)reader.Length]; reader.ReadExactly(pcm);
                try
                {
                    foreach (var (offset, length) in Split(pcm, reader.WaveFormat.SampleRate))
                    {
                        var id = Guid.NewGuid().ToString("N");
                        await SendAsync(session.Process, new { type = "chunk", id, seq = 0, pcm = Convert.ToBase64String(pcm, offset, length) }, timeout.Token);
                        await SendAsync(session.Process, new { type = "transcribe", id, sample_rate = reader.WaveFormat.SampleRate }, timeout.Token);
                        while (true)
                        {
                            using var response = await ReadAsync(session.Process, timeout.Token); var result = response.RootElement;
                            if (result.GetProperty("id").GetString() != id) throw new InvalidOperationException("Haasの応答IDが一致しません。");
                            var type = result.GetProperty("type").GetString(); if (type == "progress") continue;
                            if (type != "end" || result.GetProperty("stop").GetString() != "complete") throw new InvalidOperationException("Haasの認識が正常完了しませんでした。");
                            text.Append(result.GetProperty("text").GetString()); break;
                        }
                    }
                }
                finally { Array.Clear(pcm); }
            }
            return new(AsrEngines.Haas, text.ToString(), watch.Elapsed.TotalSeconds) { RecognitionSeconds = wave is null ? 0 : recognition.Elapsed.TotalSeconds,
                ModelLoadSeconds = session.LoadSeconds, ModelRetained = session.Reused, ComputeProfile = "speech.cpp 0.10.0 / Q8_0 / " + _observed };
        }
        catch (OperationCanceledException) { Reset(); if (ct.IsCancellationRequested) throw; throw new InvalidOperationException("Haasの認識が時間切れになりました。"); }
        catch { Reset(); throw; }
        finally { _gate.Release(); }
    }
    internal static (int Offset, int Length)[] Split(byte[] pcm, int rate)
    {
        if (rate is < 8000 or > 192000 || pcm.Length % 2 != 0) throw new InvalidOperationException("Haasの音声形式が不正です。");
        var ranges = new List<(int, int)>(); var start = 0; var quiet = 0; var frame = rate / 50 * 2;
        for (var pos = 0; pos < pcm.Length; pos += frame)
        {
            var end = Math.Min(pcm.Length, pos + frame); var silent = true;
            for (var i = pos; i < end; i += 2) if (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, 2))) > 185) { silent = false; break; }
            quiet = silent ? quiet + 1 : 0;
            if (pcm.Length - end >= rate * 2 / 50 && (end - start >= rate * 2 * 25 || (quiet >= 15 && end - start >= rate * 2 * 2)))
            { ranges.Add((start, end - start)); start = end; quiet = 0; }
        }
        if (start < pcm.Length) ranges.Add((start, pcm.Length - start));
        return ranges.ToArray();
    }
    private static async Task SendAsync(Process process, object message, CancellationToken ct)
    { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct); await process.StandardInput.FlushAsync(ct); }
    private void Reset()
    {
        lock (_sync)
        {
            if (_process is null) return;
            try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(5000); } } catch (InvalidOperationException) { }
            finally { _process.Dispose(); _process = null; _profile = ""; }
        }
    }
    public void Dispose() { lock (_sync) _disposed = true; Reset(); }
}

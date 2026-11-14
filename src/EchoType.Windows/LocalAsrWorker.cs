using EchoType.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace EchoType.Windows;

public sealed record AsrWarmupResult(string EngineId, double ProcessingSeconds, double LoadSeconds, bool Reused);

public sealed class LocalAsrWorker(AgentPaths paths) : IDisposable
{
    private readonly Func<string, ProcessStartInfo>? _startOverride;
    internal LocalAsrWorker(AgentPaths paths, Func<string, ProcessStartInfo> start) : this(paths) => _startOverride = start;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _capacity = new(2, 2);
    private readonly object _sync = new();
    private sealed class Session(Process process)
    {
        public Process Process { get; } = process;
        public ConcurrentDictionary<string, TaskCompletionSource<string>> Pending { get; } = new();
        public ConcurrentDictionary<string, Action<ModelPhase, string?>> Progress { get; } = new();
    }
    private Session? _session;
    private readonly ConcurrentDictionary<Task<string>, byte> _running = new();
    internal int? ProcessId { get { lock (_sync) return _process is { HasExited: false } p ? p.Id : null; } }
    private bool _disposed;
    private Process? _process;
    private string _runtime = "";
    private Task? _stderr;
    public static string PythonPath(AgentPaths paths, string engine) => Path.Combine(paths.RecognitionModels,
        AsrEngines.IsFastLocal(engine) ? "fast-asr-venv" : AsrEngines.IsQwen(engine) ? "qwen-venv" : "venv", "Scripts", "python.exe");
    public static bool IsReady(AgentPaths paths, string engine) => InstallationStatus(paths, engine) == "導入済み";
    public static string InstallationStatus(AgentPaths paths, string engine)
    {
        if (AsrEngines.BaseId(engine) == AsrEngines.Haas) return HaasNativeWorker.InstallationStatus;
        if (AsrEngines.BaseId(engine) == AsrEngines.WindowsCaptionsDll) return WindowsCaptionDll.Status;
        if (AsrEngines.BaseId(engine) == WindowsAiSpeech.Engine) return WindowsAiSpeech.Status;
        if (!AsrEngines.IsLocal(engine)) return "ローカルモデルの導入対象外";
        if (AsrEngines.IsFastLocal(engine)) return FastInstallationStatus(paths, AsrEngines.ModelId(engine));
        if (!File.Exists(PythonPath(paths, engine))) return "音声認識用Python環境がありません。「導入する」で準備できます。";
        var directory = Path.Combine(paths.RecognitionModels, "asr", AsrEngines.ModelId(engine));
        if (!HasWeights(directory, AsrEngines.IsQwen(engine)) || !File.Exists(Path.Combine(directory, "config.json")))
            return "モデル本体がありません。「導入する」で取得できます。";
        if (!File.Exists(Path.Combine(directory, "ready.json"))) return "モデル導入が未完了です。「導入する」で再開できます。";
        return "導入済み";
    }
    private static string FastInstallationStatus(AgentPaths paths, string engine)
    {
        if (!HasContainedFiles(Path.Combine(paths.RecognitionModels, "fast-asr-venv"), [Path.Combine("Scripts", "python.exe")]))
            return "音声認識用Python環境がありません。「導入する」で準備できます。";
        var directory = Path.Combine(paths.RecognitionModels, "asr", engine);
        string[] files = engine switch
        {
            "reazon-k2-v2" => ["encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt"],
            "parakeet-ja-ctc" => ["model.int8.onnx", "tokens.txt"],
            "nemotron-3.5" => ["nemotron-3.5-asr-streaming-0.6b.q8_0.gguf"],
            _ => []
        };
        if (files.Length == 0 || !HasContainedFiles(directory, files))
            return "モデル本体がありません。「導入する」で取得できます。";
        if (engine == "nemotron-3.5" && !HasContainedFiles(Path.Combine(paths.RecognitionModels, "nemo-speech-0.2.0"),
            [Path.Combine("cpu", "bin", "nemo_speech_asr_c.dll"), Path.Combine("cuda", "bin", "nemo_speech_asr_c.dll")]))
            return "Nemotronの実行環境がありません。「導入する」で準備できます。";
        if (!HasContainedFiles(directory, ["ready.json"]))
            return "モデル導入が未完了です。「導入する」で再開できます。";
        return "導入済み";
    }
    private static bool HasContainedFiles(string directory, IEnumerable<string> names)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists) return false;
            var root = Path.TrimEndingDirectorySeparator((info.ResolveLinkTarget(true) ?? info).FullName);
            return names.All(name =>
            {
                if (Path.IsPathRooted(name)) return false;
                var parts = name.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None);
                if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':'))) return false;
                var parent = root;
                foreach (var part in parts[..^1])
                {
                    var child = new DirectoryInfo(Path.Combine(parent, part));
                    if (!child.Exists) return false;
                    parent = Path.TrimEndingDirectorySeparator((child.ResolveLinkTarget(true) ?? child).FullName);
                    if (!parent.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                        !parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                }
                var file = new FileInfo(Path.Combine(parent, parts[^1]));
                if (!file.Exists || file.Length == 0) return false;
                var resolved = file.ResolveLinkTarget(true) ?? file;
                return string.Equals(Path.GetDirectoryName(resolved.FullName), parent, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return false; }
    }
    private static bool HasWeights(string directory, bool qwen)
    {
        try
        {
            var modelDirectory = new DirectoryInfo(directory);
            if (!modelDirectory.Exists) return false;
            var resolvedDirectory = (modelDirectory.ResolveLinkTarget(true) ?? modelDirectory).FullName;
            bool Nonempty(string name)
            {
                var file = new FileInfo(Path.Combine(resolvedDirectory, name));
                var resolved = file.Exists ? file.ResolveLinkTarget(true) ?? file : file;
                return file.Exists && file.Length > 0 &&
                    string.Equals(Path.GetDirectoryName(resolved.FullName), resolvedDirectory, StringComparison.OrdinalIgnoreCase);
            }
            var index = Path.Combine(directory, "model.safetensors.index.json");
            if (!qwen || !File.Exists(index)) return Nonempty(qwen ? "model.safetensors" : "model.bin");
            if (!Nonempty("model.safetensors.index.json")) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(index));
            if (!document.RootElement.TryGetProperty("weight_map", out var map) || map.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var weight in map.EnumerateObject())
            {
                if (weight.Name.Length == 0 || weight.Value.ValueKind != JsonValueKind.String || weight.Value.GetString() is not { } name ||
                    !Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\.safetensors\z", RegexOptions.CultureInvariant)) return false;
                names.Add(name);
            }
            return names.Count > 0 && names.All(Nonempty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { return false; }
    }
    public bool Ready(string engine) => _startOverride is not null || IsReady(paths, engine);
    private void EnsureStarted(string engine)
    {
        var python = PythonPath(paths, engine);
        if (_process is { HasExited: false } && _runtime == python) return;
        Kill();
        var script = Path.Combine(AppContext.BaseDirectory, "Asr", "worker.py");
        if (_startOverride is null && (!File.Exists(python) || !File.Exists(script))) throw new AsrModelException("ローカル音声認識が未導入です。setup-asr.ps1を実行してください。");
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("-u"); info.ArgumentList.Add(script); info.ArgumentList.Add("--serve"); info.ArgumentList.Add(Path.Combine(paths.RecognitionModels, "asr"));
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        try { _process = Process.Start(_startOverride?.Invoke(engine) ?? info) ?? throw new AsrModelException("ローカル音声認識を起動できません。"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new AsrModelException("ローカル音声認識を起動できません。", ex); }
        _runtime = python;
        var process = _process;
        var session = _session = new(process);
        _ = ReadResponsesAsync(session);
        _stderr = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch (ObjectDisposedException) { } catch (IOException) { } });
    }
    public async Task<(string Text, double Seconds, string Hash, double WaitSeconds, double LoadSeconds, bool ModelRetained)> RecognizeAsync(string engine, byte[] wave, string prompt, string device, CancellationToken ct, bool prepared = false, int beamSize = 5, int cpuThreads = 2, string cudaCompute = "float16", int parallelWorkers = 2, Action<ModelPhase, string?>? progress = null)
    {
        using var input = prepared ? null : RecognitionAudio.Prepare(wave);
        wave = input?.Wave ?? wave;
        if (!AsrEngines.IsLocal(engine)) throw new InvalidOperationException("未対応のローカルモデルです。");
        if (!Ready(engine)) throw new AsrModelException(InstallationStatus(paths, engine));
        var waiting = Stopwatch.StartNew();
        using var result = await RequestAsync(engine, new { engine, wave = Convert.ToBase64String(wave), prompt, device, beamSize, cpuThreads, cudaCompute, parallelWorkers }, ct, progress).ConfigureAwait(false);
            var hash = result.RootElement.GetProperty("audioHash").GetString() ?? "";
            if (hash != Convert.ToHexString(SHA256.HashData(wave))) throw new InvalidOperationException("補助プロセスの入力音声が一致しません。");
            return (result.RootElement.GetProperty("text").GetString() ?? "", result.RootElement.GetProperty("recognitionSeconds").GetDouble(), hash,
                Math.Max(0, waiting.Elapsed.TotalSeconds - result.RootElement.GetProperty("seconds").GetDouble()),
                result.RootElement.GetProperty("loadSeconds").GetDouble(), result.RootElement.GetProperty("modelReused").GetBoolean());
    }
    public async Task<AsrWarmupResult> WarmupAsync(string engine, string device, RecognitionPerformance options, CancellationToken ct, Action<ModelPhase, string?>? progress = null)
    {
        options.Validate();
        if (!AsrEngines.IsLocal(engine)) throw new InvalidOperationException("未対応のローカルモデルです。");
        engine = AsrEngines.ModelId(engine);
        if (!Ready(engine)) throw new InvalidOperationException(InstallationStatus(paths, engine));
        var elapsed = Stopwatch.StartNew();
            using var result = await RequestAsync(engine, new { operation = "warmup", engine, device, cpuThreads = options.CpuThreads, cudaCompute = options.CudaCompute, parallelWorkers = Math.Min(2, options.MaxConcurrent) }, ct, progress).ConfigureAwait(false);
            var root = result.RootElement;
            if (!root.GetProperty("ready").GetBoolean() || root.GetProperty("engine").GetString() != engine)
                throw new InvalidOperationException("事前読み込みしたモデルが一致しません。");
            return new(engine, elapsed.Elapsed.TotalSeconds, root.GetProperty("loadSeconds").GetDouble(), root.GetProperty("reused").GetBoolean());
    }
    private async Task<JsonDocument> RequestAsync(string engine, object request, CancellationToken ct, Action<ModelPhase, string?>? progress = null)
    {
        await _capacity.WaitAsync(ct).ConfigureAwait(false);
        // 取消し後も実行枠と応答の受取りを保持する。他の要求を停止せず遅着結果を捨てる。
        var running = ExchangeAsync(engine, request, ct, progress);
        _running[running] = 0;
        _ = running.ContinueWith(t => { _ = t.Exception; _running.TryRemove(t, out _); _capacity.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var line = await running.WaitAsync(ct).ConfigureAwait(false);
        var result = JsonDocument.Parse(line);
        try
        {
            if (result.RootElement.GetProperty("error").GetString() is { Length: > 0 } error)
            {
                var failure = new InvalidOperationException(error);
                failure.Data["AsrWorkerPid"] = ProcessId;
                if (result.RootElement.TryGetProperty("diagnostic", out var diagnostic))
                {
                    if (diagnostic.TryGetProperty("code", out var code) && code.GetString() is { } value && new[] { "cuda-library-unavailable", "memory-exhausted", "compute-unsupported", "cuda-error", "engine-error" }.Contains(value)) failure.Data["AsrNativeCode"] = value;
                    if (diagnostic.TryGetProperty("stage", out var stage) && stage.GetString() is { } step && new[] { "request", "model-load", "warmup", "recognition" }.Contains(step)) failure.Data["AsrNativeStage"] = step;
                    if (diagnostic.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
                        failure.Data["AsrLibraries"] = libraries.EnumerateArray().Select(p => p.GetString() ?? "").Where(s => Regex.IsMatch(s, @"\A(?:cublas|cudnn|cudart)[a-zA-Z0-9_]*\.dll\z")).Take(8).ToArray();
                }
                if (failure.Data["AsrNativeStage"] is "model-load" ||
                    failure.Data["AsrNativeCode"] is "cuda-library-unavailable" or "memory-exhausted" or "compute-unsupported" or "cuda-error")
                {
                    var modelError = new AsrModelException(error, failure);
                    foreach (System.Collections.DictionaryEntry entry in failure.Data) modelError.Data.Add(entry.Key, entry.Value);
                    throw modelError;
                }
                throw failure;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal async Task DrainAsync()
    {
        try { await Task.WhenAll(_running.Keys).ConfigureAwait(false); }
        catch (Exception) { /* 取消し後の遅着応答・通信障害は採用しない。 */ }
    }
    private async Task<string> ExchangeAsync(string engine, object request, CancellationToken caller, Action<ModelPhase, string?>? progress)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(AsrEngines.IsQwen(engine) ? 15 : 3));
        var id = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Session? session = null;
        try
        {
            await _write.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                caller.ThrowIfCancellationRequested();
                lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); EnsureStarted(engine); session = _session!; session.Pending[id] = response; if (progress is not null) session.Progress[id] = progress; }
                var message = JsonSerializer.SerializeToElement(request).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
                message["requestId"] = id;
                if (progress is not null) message["notifyProgress"] = true;
                await session.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), deadline.Token).ConfigureAwait(false);
                await session.Process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            finally { _write.Release(); }
            return await response.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session is null && caller.IsCancellationRequested) { throw; }
        catch
        {
            // 要求ごとの認識エラーはRequestAsyncが処理する。通信断・応答期限だけプロセスを破棄。
            lock (_sync) { if (ReferenceEquals(_session, session) && session is not null) Kill(); }
            throw;
        }
        finally { if (session is not null) { session.Pending.TryRemove(id, out _); session.Progress.TryRemove(id, out _); } }
    }
    private async Task ReadResponsesAsync(Session session)
    {
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 100000) throw new IOException("認識応答が上限を超えました。");
                using var json = JsonDocument.Parse(line);
                var id = json.RootElement.GetProperty("requestId").GetString() ?? "";
                if (json.RootElement.TryGetProperty("progress", out var stage))
                {
                    if (stage.ValueKind == JsonValueKind.String && session.Progress.TryGetValue(id, out var observer))
                    {
                        var phase = stage.GetString() switch { "loading" => ModelPhase.Loading, "recognizing" => ModelPhase.Inferencing, "ready" => ModelPhase.Processing, _ => (ModelPhase?)null };
                        var observed = json.RootElement.TryGetProperty("observedDevice", out var observedValue) && observedValue.ValueKind == JsonValueKind.String
                            ? observedValue.GetString() switch { "cpu" => "認識器報告: CPU", "cuda" => "認識器報告: CUDA", _ => null } : null;
                        if (phase is { } value) try { observer(value, observed); } catch (Exception) { /* 表示失敗は認識に影響させない。 */ }
                    }
                    continue;
                }
                if (session.Pending.TryRemove(id, out var pending)) pending.TrySetResult(line);
                else throw new IOException("認識応答の要求IDが一致しません。");
            }
            throw new AsrModelException("音声認識の補助プロセスが停止しました。");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            foreach (var pending in session.Pending.Values) pending.TrySetException(ex);
            lock (_sync) { if (ReferenceEquals(_session, session)) Kill(); }
        }
    }
    private void Kill()
    {
        var process = _process; _process = null;
        var session = _session; _session = null;
        if (session is not null) foreach (var pending in session.Pending.Values) pending.TrySetException(new IOException("音声認識の補助プロセスを終了しました。"));
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
    public void Dispose() { lock (_sync) { _disposed = true; Kill(); } }
}

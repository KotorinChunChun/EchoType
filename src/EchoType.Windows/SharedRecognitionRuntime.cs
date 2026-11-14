using EchoType.Core;
using System.Diagnostics;
using System.Security.Cryptography;

namespace EchoType.Windows;

/// <summary>保存先単位の共有。音声配列は完了で消去し、成功した文字列だけ短時間保持する。</summary>
public sealed class SharedRecognitionRuntime : IDisposable
{
    private static readonly object RootsLock = new();
    private static readonly Dictionary<string, SharedRecognitionRuntime> Roots = new(StringComparer.OrdinalIgnoreCase);
    public static SharedRecognitionRuntime Acquire(AgentPaths paths)
    {
        lock (RootsLock)
        {
            if (!Roots.TryGetValue(paths.Root, out var runtime)) Roots[paths.Root] = runtime = new(paths);
            runtime._owners++; return runtime;
        }
    }
    private readonly AgentPaths _paths;
    public ModelActivityRegistry Activities { get; }
    public RecognitionEngineSettings? EngineSettings { get; private set; }
    public RecognitionEngineSettings? VoiceEngineSettings { get; private set; }
    public void ConfigureEngineSettings(AppSettings started, AppSettings voiceStarted)
    {
        lock (_sync)
        {
            EngineSettings ??= new(started);
            VoiceEngineSettings ??= new(voiceStarted with { Performance = started.Performance });
        }
    }
    public AppSettings EffectiveSettings(AppSettings desired, bool voice = false) =>
        (voice ? VoiceEngineSettings : EngineSettings)?.Apply(desired) ?? desired;
    private int _owners;
    private bool _closed;
    private readonly object _sync = new();
    private readonly RecognitionScheduler _scheduler = new();
    private readonly ResidentAsrPool _workers;
    private readonly HaasNativeWorker _haas = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly HashSet<CancellationTokenSource> _warmups = [];
    private sealed class Job
    {
        public readonly CancellationTokenSource Stop = new();
        public Task<AsrResult> Task = null!;
        public int Users;
        public DateTime Created = DateTime.UtcNow;
        public RecognitionPurpose Purpose;
        private int _retired;
        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 0) _ = ReleaseAsync();
        }
        private async Task ReleaseAsync()
        {
            try
            {
                await Stop.CancelAsync().ConfigureAwait(false);
                try { await Task.ConfigureAwait(false); } catch (Exception) { /* 認識エラーは各利用者へ伝播済み。 */ }
            }
            finally { Stop.Dispose(); }
        }
    }
    private SharedRecognitionRuntime(AgentPaths paths) { _paths = paths; _workers = new(paths); Activities = ModelActivityHub.For(paths); }
    public string ResidentStatus(string engine) => engine == AsrEngines.Haas ? _haas.Residency : _workers.Status(AsrEngines.ModelId(engine));
    public Task UnloadModelAsync(string engine, CancellationToken token) => engine == AsrEngines.Haas
        ? _haas.UnloadAsync(token) : _workers.UnloadAsync(AsrEngines.ModelId(engine), token);
    public async Task<AsrWarmupResult> WarmupAsync(string engine, string device, RecognitionPerformance options, CancellationToken ct, bool preferred = false)
    {
        options.Validate(); ct.ThrowIfCancellationRequested();
        if (engine == AsrEngines.Haas)
        {
            var watch = Stopwatch.StartNew();
            using var haasActivity = Activities.Begin("音声認識", engine, "事前読込み", device == "cpu" ? "CPU" : "Vulkan");
            AsrResult result;
            try
            {
                using var slot = await _scheduler.EnterAsync(RecognitionPurpose.Warmup, options.MaxConcurrent, ct).ConfigureAwait(false);
                result = await _haas.WarmupAsync(device, options.CpuThreads, ct, (phase, observed) => haasActivity.Update(phase, observed)).ConfigureAwait(false);
                haasActivity.Finish(ModelPhase.Completed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { haasActivity.Finish(ModelPhase.Cancelled); throw; }
            catch { haasActivity.Finish(ModelPhase.Failed); throw; }
            return new(engine, watch.Elapsed.TotalSeconds, result.ModelLoadSeconds ?? 0, result.ModelRetained ?? false);
        }
        using var activity = Activities.Begin("音声認識", engine, "事前読込み", AsrEngines.IsCpuOnly(engine) ? "CPU" : device.ToUpperInvariant());
        var cancelledByStop = false;
        try
        {
        if (AsrEngines.BaseId(engine) == AsrEngines.WindowsCaptionsDll)
        {
            activity.Update(ModelPhase.Processing);
            var watch = Stopwatch.StartNew(); await WindowsCaptionDll.RefreshStatusAsync(ct, _paths);
            if (!WindowsCaptionDll.Ready) throw new InvalidOperationException(WindowsCaptionDll.Status);
            activity.Finish(ModelPhase.Completed);
            return new(engine, watch.Elapsed.TotalSeconds, watch.Elapsed.TotalSeconds, false);
        }
        if (!AsrEngines.IsLocal(engine)) throw new InvalidOperationException("未対応のローカルモデルです。");
        device = AsrEngines.IsCpuOnly(engine) ? "cpu" : device;
        CancellationTokenSource stop;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct); _warmups.Add(stop);
        }
        try
        {
            using var slot = await _scheduler.EnterAsync(RecognitionPurpose.Warmup, options.MaxConcurrent, stop.Token).ConfigureAwait(false);
            activity.Update(ModelPhase.WorkerWaiting);
            using var resident = await _workers.RentAsync(WorkerKey(engine, device, options), options.MaxResidentModels, stop.Token, preferred).ConfigureAwait(false);
            try { var result = await resident.Worker.WarmupAsync(engine, device, options, stop.Token, (phase, observed) => activity.Update(phase, observed)).ConfigureAwait(false); activity.Finish(ModelPhase.Completed); return result; }
            catch (OperationCanceledException) { if (stop.IsCancellationRequested) activity.Update(ModelPhase.Cancelling); await resident.Worker.DrainAsync().ConfigureAwait(false); throw; }
        }
        finally { cancelledByStop = stop.IsCancellationRequested; lock (_sync) _warmups.Remove(stop); stop.Dispose(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || cancelledByStop) { activity.Finish(ModelPhase.Cancelled); throw; }
        catch { activity.Finish(ModelPhase.Failed); throw; }
    }
    private string WorkerKey(string engine, string device, RecognitionPerformance options)
    {
        var revision = File.GetLastWriteTimeUtc(Path.Combine(_paths.RecognitionModels, "asr", AsrEngines.ModelId(engine), "ready.json")).Ticks;
        var compute = AsrEngines.IsFastLocal(engine) ? ComputeProfile(engine, device, options, options.FullBeam) :
            $"{device}/{options.CpuThreads}/{(device == "cpu" ? "int8" : options.CudaCompute)}" + (AsrEngines.IsWhisper(engine) ? $"/workers={Math.Min(2, options.MaxConcurrent)}" : "");
        return $"{AsrEngines.ModelId(engine)}/{compute}/{revision}";
    }
    private static string ComputeProfile(string engine, string device, RecognitionPerformance options, int beam) => AsrEngines.BaseId(engine) switch
    {
        "reazon-k2-v2" => $"cpu/int8-fp32/greedy/threads={options.CpuThreads}",
        "parakeet-ja-ctc" => $"cpu/int8/ctc/threads={options.CpuThreads}",
        "nemotron-3.5" => $"{device}/q8_0/threads=runtime(max8)",
        "windows" => "Windows ja-JP",
        "windows-ai-speech" => "Windows AI / 言語自動判定",
        AsrEngines.WindowsCaptionsDll => "Windows字幕DLL / ja-JP / 16kHz PCM直接入力",
        AsrEngines.Haas => device == "cpu" ? "Haas / Q8_0 / CPU" : "Haas / Q8_0 / Vulkan",
        _ => $"{device}/{(AsrEngines.IsQwen(engine) ? (device == "cpu" ? "float32" : "float16") : device == "cpu" ? "int8" : options.CudaCompute)}/" +
            (AsrEngines.IsQwen(engine) ? "" : $"beam={beam}/") + $"threads={options.CpuThreads}"
    };
    public async Task<AsrResult> RecognizeAsync(string engine, byte[] wave, string prompt, string device, RecognitionPerformance options,
        RecognitionPurpose purpose, bool bypass, CancellationToken ct, Func<byte[], CancellationToken, Task<AsrResult>>? recognize = null)
    {
        options.Validate(); ct.ThrowIfCancellationRequested();
        device = AsrEngines.IsCpuOnly(engine) ? "cpu" : device;
        var beam = purpose == RecognitionPurpose.Immediate ? options.ImmediateBeam : options.FullBeam;
        var profile = ComputeProfile(engine, device, options, beam);
        var revision = AsrEngines.IsLocal(engine) ? File.GetLastWriteTimeUtc(Path.Combine(_paths.RecognitionModels, "asr", AsrEngines.ModelId(engine), "ready.json")).Ticks : 0;
        var hash = Convert.ToHexString(SHA256.HashData(wave));
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(engine + "\n" + profile + "\n" + revision + "\n" + hash + "\n" + prompt)));
        if (bypass || !options.ShareResults) key += Guid.NewGuid().ToString("N");
        Job job; bool reused;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            foreach (var old in _jobs.Where(p => p.Value.Users == 0 && p.Value.Task.IsCompleted && (DateTime.UtcNow - p.Value.Created > TimeSpan.FromMinutes(2) || _jobs.Count > 128)).Select(p => p.Key).ToArray())
            { _jobs[old].Retire(); _jobs.Remove(old); }
            reused = _jobs.TryGetValue(key, out job!);
            if (!reused)
            {
                job = new() { Purpose = purpose }; var copy = wave.ToArray();
                job.Task = Task.Run(async () => { try { return await RunAsync(engine, copy, prompt, device, options, job, beam, profile, job.Stop.Token, recognize); } finally { Array.Clear(copy); } });
                _jobs[key] = job;
            }
            else if (purpose < job.Purpose) { job.Purpose = purpose; _scheduler.Promote(job, purpose); }
            job.Users++;
        }
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = await job.Task.WaitAsync(ct).ConfigureAwait(false);
            return reused ? result with { Reused = true, QueueSeconds = elapsed.Elapsed.TotalSeconds, RecognitionSeconds = 0, ProcessingSeconds = elapsed.Elapsed.TotalSeconds } : result;
        }
        finally
        {
            lock (_sync)
            {
                job.Users--;
                if (job.Users == 0 && (bypass || !options.ShareResults || !job.Task.IsCompletedSuccessfully || !job.Task.Result.Succeeded))
                {
                    _jobs.Remove(key);
                    job.Retire();
                }
            }
        }
    }
    private async Task<AsrResult> RunAsync(string engine, byte[] wave, string prompt, string device, RecognitionPerformance options, Job job, int beam, string profile, CancellationToken ct, Func<byte[], CancellationToken, Task<AsrResult>>? recognize)
    {
        using var activity = Activities.Begin("音声認識", engine, ModelActivity.RecognitionPurposeLabel(job.Purpose), engine == AsrEngines.Haas && device != "cpu" ? "Vulkan" : device is "cpu" or "cuda" ? device.ToUpperInvariant() : "OS管理");
        try
        {
        var watch = Stopwatch.StartNew();
        Task<IDisposable> pending;
        lock (_sync) pending = _scheduler.EnterAsync(job.Purpose, options.MaxConcurrent, ct, job);
        using var slot = await pending.ConfigureAwait(false);
        if (engine == AsrEngines.Haas)
        {
            var waited = watch.Elapsed.TotalSeconds;
            var haas = await _haas.RecognizeAsync(wave, device, options.CpuThreads, ct, (phase, observed) => activity.Update(phase, observed));
            activity.Finish(ModelPhase.Completed);
            return AsrTextGuard.Filter(haas with { QueueSeconds = waited, ProcessingSeconds = watch.Elapsed.TotalSeconds });
        }
        if (recognize is not null)
        {
            var waited = watch.Elapsed.TotalSeconds;
            activity.Update(ModelPhase.Processing);
            var result = (await recognize(wave, ct).ConfigureAwait(false)) with { QueueSeconds = waited, ComputeProfile = profile };
            activity.Finish(result.Succeeded ? ModelPhase.Completed : ModelPhase.Failed);
            return result;
        }
        activity.Update(ModelPhase.WorkerWaiting);
        using var resident = await _workers.RentAsync(WorkerKey(engine, device, options), options.MaxResidentModels, ct,
            preferred: job.Purpose is RecognitionPurpose.Inquiry or RecognitionPurpose.Immediate or RecognitionPurpose.Full).ConfigureAwait(false);
        var queued = watch.Elapsed.TotalSeconds;
        (string Text, double Seconds, string Hash, double WaitSeconds, double LoadSeconds, bool ModelRetained) output;
        try { output = await resident.Worker.RecognizeAsync(engine, wave, prompt, device, ct, prepared: true, beamSize: beam, cpuThreads: options.CpuThreads, cudaCompute: options.CudaCompute, parallelWorkers: Math.Min(2, options.MaxConcurrent), progress: (phase, observed) => activity.Update(phase, observed)).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // 利用者には即座に取消しを返すが、ネイティブ処理中の実行枠・モデル貸出を維持する。
            if (ct.IsCancellationRequested) activity.Update(ModelPhase.Cancelling); await resident.Worker.DrainAsync().ConfigureAwait(false); throw;
        }
        var completed = AsrTextGuard.Filter(new(engine, output.Text, watch.Elapsed.TotalSeconds) { AudioHash = output.Hash, RecognitionSeconds = output.Seconds, QueueSeconds = queued + output.WaitSeconds, ComputeProfile = profile,
            ModelLoadSeconds = output.LoadSeconds, ModelRetained = output.ModelRetained });
        activity.Finish(completed.Succeeded ? ModelPhase.Completed : ModelPhase.Failed);
        return completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { activity.Finish(ModelPhase.Cancelled); throw; }
        catch { activity.Finish(ModelPhase.Failed); throw; }
    }
    public void Dispose()
    {
        lock (RootsLock)
        {
            if (--_owners != 0) return;
            Roots.Remove(_paths.Root);
            lock (_sync)
            {
                _closed = true;
                foreach (var warmup in _warmups) warmup.Cancel();
                foreach (var job in _jobs.Values) job.Retire();
                _jobs.Clear();
                _workers.Dispose();
                _haas.Dispose();
            }
        }
    }
}

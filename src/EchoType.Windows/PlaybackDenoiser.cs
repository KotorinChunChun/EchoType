using EchoType.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EchoType.Windows;

/// <summary>再生音の前処理を同一原音で共有。原音の永続保存には触れない。</summary>
public sealed class PlaybackDenoiser(AgentPaths paths) : IDisposable
{
    private readonly object _sync = new();
    private sealed class Entry
    {
        public readonly CancellationTokenSource Stop = new();
        public Task<byte[]> Task = null!;
        public int Users;
    }
    private readonly Dictionary<string, Entry> _cache = [];
    private readonly SemaphoreSlim _one = new(1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    public static bool Applies(AppSettings settings, SpeechSegment segment) => settings.PlaybackDenoiseEnabled && segment.AudioSource.Kind == AudioSourceKind.Playback;
    public static bool Ready(AgentPaths paths) => File.Exists(Path.Combine(paths.RecognitionModels, "denoise", "ready")) &&
        File.Exists(Path.Combine(paths.RecognitionModels, "denoise", "gtcrn_simple.onnx")) && File.Exists(Path.Combine(paths.RecognitionModels, "denoise", "venv", "Scripts", "python.exe"));
    public async Task<byte[]> PrepareAsync(byte[] wave, CancellationToken ct)
    {
        if (!Ready(paths)) throw new InvalidOperationException("再生音声の雑音抑制が未導入です。設定の「雑音抑制を導入する」を実行するか、雑音抑制をオフにしてください。");
        var hash = Convert.ToHexString(SHA256.HashData(wave));
        Entry entry;
        lock (_sync)
        {
            if (!_cache.TryGetValue(hash, out entry!))
            {
                foreach (var key in _cache.Where(p => p.Value.Task.IsCompleted && p.Value.Users == 0).Select(p => p.Key).Take(Math.Max(0, _cache.Count - 3)).ToArray())
                { _cache[key].Stop.Dispose(); _cache.Remove(key); }
                entry = new(); entry.Task = ProcessAsync(wave.ToArray(), entry.Stop.Token); _cache[hash] = entry;
            }
            entry.Users++;
        }
        try { return (await entry.Task.WaitAsync(ct)).ToArray(); }
        finally
        {
            lock (_sync)
            {
                entry.Users--;
                if (entry.Users == 0 && (!entry.Task.IsCompletedSuccessfully))
                {
                    _cache.Remove(hash);
                    entry.Stop.Cancel();
                    _ = entry.Task.ContinueWith(_ => entry.Stop.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
            }
        }
    }
    private async Task<byte[]> ProcessAsync(byte[] wave, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, ct);
        var acquired = false;
        try
        {
            await _one.WaitAsync(deadline.Token); acquired = true;
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if (_process is null || _process.HasExited)
            {
                _process?.Dispose();
                var info = new ProcessStartInfo(Path.Combine(paths.RecognitionModels, "denoise", "venv", "Scripts", "python.exe")) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                info.ArgumentList.Add("-u"); info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Asr", "denoise.py"));
                info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                info.ArgumentList.Add(Path.Combine(paths.RecognitionModels, "denoise", "gtcrn_simple.onnx"));
                _process = Process.Start(info) ?? throw new IOException("雑音抑制プロセスを起動できません。");
                _ = _process.StandardError.ReadToEndAsync();
            }
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { wave = Convert.ToBase64String(wave) }).AsMemory(), deadline.Token);
            await _process.StandardInput.FlushAsync(deadline.Token);
            var line = await _process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("雑音抑制プロセスが終了しました。");
            using var result = JsonDocument.Parse(line);
            var error = result.RootElement.GetProperty("error").GetString();
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException("雑音抑制に失敗: " + error);
            var output = Convert.FromBase64String(result.RootElement.GetProperty("wave").GetString()!);
            using var before = new NAudio.Wave.WaveFileReader(new MemoryStream(wave));
            using var after = new NAudio.Wave.WaveFileReader(new MemoryStream(output));
            if (before.Length != after.Length || !before.WaveFormat.Equals(after.WaveFormat)) throw new IOException("雑音抑制で音声の長さ・形式が変わりました。");
            return output;
        }
        catch { if (acquired) Kill(); throw; }
        finally { Array.Clear(wave); if (acquired) _one.Release(); }
    }
    private void Kill() { var p = _process; _process = null; if (p is null) return; try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } p.Dispose(); }
    public void Dispose() { _stop.Cancel(); Kill(); }
}

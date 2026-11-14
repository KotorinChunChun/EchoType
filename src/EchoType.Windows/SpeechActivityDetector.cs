using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Security.Cryptography;

namespace EchoType.Windows;

public sealed record SpeechActivity(bool HasSpeech, double SpeechSeconds, float MaximumProbability);

/// <summary>認識前に全方式へ同じ発話判定を適用。判定用に変換するだけで、送信するWAVは変更しない。</summary>
public sealed class SpeechActivityDetector
{
    public static SpeechActivityDetector Shared { get; } = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, SpeechActivity> _cache = [];
    private InferenceSession? _model;
    private const string ModelHash = "4CBF549B8326F60F80F2536D9EEFEB450A9ABE83365A098031C89719F1BE17D2";

    public SpeechActivity Analyze(byte[] wave)
    {
        if (wave.Length > 8_000_000) throw new InvalidOperationException("発話検出の音声が長すぎます。");
        var hash = Convert.ToHexString(SHA256.HashData(wave));
        lock (_sync)
        {
            if (_cache.TryGetValue(hash, out var previous)) return previous;
            try
            {
                using var reader = new WaveFileReader(new MemoryStream(wave, false));
                if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.SampleRate != 24000 ||
                    reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 || reader.Length > 48000 * 120)
                    throw new InvalidOperationException("発話検出は24kHz/16bit/monoの音声が必要です。");
                // ほぼゼロの入力だけを早期に除外。小声を音量の固定しきい値で切らない。
                var pcm = new byte[(int)reader.Length];
                try
                {
                    reader.ReadExactly(pcm);
                    var zero = true;
                    for (var i = 0; i < pcm.Length; i += 2) if (Math.Abs((int)BitConverter.ToInt16(pcm, i)) > 2) { zero = false; break; }
                    if (zero) return Remember(hash, new(false, 0, 0));
                }
                finally { Array.Clear(pcm); }
                reader.Position = 0;
                var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
                var samples = new List<float>(); var buffer = new float[4096]; int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read).ToArray());
                Array.Clear(buffer);
                var frames = (samples.Count + 511) / 512;
                if (frames == 0) return Remember(hash, new(false, 0, 0));
                var input = new DenseTensor<float>(new[] { frames, 576 });
                for (var f = 0; f < frames; f++)
                    for (var i = 0; i < 576; i++)
                    {
                        var index = f * 512 + i - 64;
                        if (index >= 0 && index < samples.Count) input[f, i] = samples[index];
                    }
                try
                {
                    var model = Model();
                    using var result = model.Run(new[] {
                        NamedOnnxValue.CreateFromTensor("input", input),
                        NamedOnnxValue.CreateFromTensor("h", new DenseTensor<float>(new[] { 1, 1, 128 })),
                        NamedOnnxValue.CreateFromTensor("c", new DenseTensor<float>(new[] { 1, 1, 128 })) });
                    var probabilities = result.First().AsTensor<float>().ToArray();
                    var consecutive = 0; var longest = 0; var speech = 0; float maximum = 0;
                    foreach (var probability in probabilities)
                    {
                        maximum = Math.Max(maximum, probability);
                        if (probability >= .5f) { consecutive++; speech++; longest = Math.Max(longest, consecutive); }
                        else consecutive = 0;
                    }
                    // 96ms連続の発話確率で単発クリックを除外し、「はい」等の短い発言は残す。
                    return Remember(hash, new(longest >= 3, speech * .032, maximum));
                }
                finally { input.Buffer.Span.Clear(); System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples).Clear(); }
            }
            catch (OnnxRuntimeException ex) { throw new InvalidOperationException("発話検出に失敗したため文字起こしへ送りません。", ex); }
        }
    }
    private SpeechActivity Remember(string hash, SpeechActivity value)
    {
        if (_cache.Count >= 128) _cache.Clear();
        _cache[hash] = value; return value;
    }
    private InferenceSession Model()
    {
        if (_model is not null) return _model;
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Asr", "silero_vad_v6.onnx"));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != ModelHash) throw new InvalidOperationException("発話検出モデルを確認できないため文字起こしへ送りません。配布物を確認してください。");
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1, EnableCpuMemArena = false };
        return _model = new InferenceSession(bytes, options);
    }
}

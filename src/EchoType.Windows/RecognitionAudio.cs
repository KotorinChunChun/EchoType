using System.Buffers.Binary;
using EchoType.Core;
using NAudio.Wave;

namespace EchoType.Windows;

/// <summary>認識用だけを短縮する。録音・発話区切りの原音は変更しない。</summary>
public static class RecognitionAudio
{
    public const int SampleRate = 24000, FrameSamples = 240, MinimumSilenceSamples = 4800, RetainedSilenceSamples = 2400;
    public const double RmsDb = -55, PeakDb = -35;
    private static readonly double RmsSquared = Math.Pow(10, RmsDb / 10) * 32768d * 32768;
    private static readonly double Peak = Math.Pow(10, PeakDb / 20) * 32768;
    internal static bool SilentWave(byte[] wave)
    {
        using var reader = new WaveFileReader(new MemoryStream(wave, false));
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16) return false;
        var buffer = new byte[4800];
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                if (count % 2 != 0 || !Silent(buffer.AsSpan(0, count))) return false;
            return true;
        }
        finally { Array.Clear(buffer); }
    }
    internal static bool Silent(ReadOnlySpan<byte> pcm)
    {
        long sum = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (int)BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i, 2));
            if (Math.Abs(sample) > Peak) return false;
            sum += (long)sample * sample;
        }
        return sum <= RmsSquared * (pcm.Length / 2);
    }
    public static PreparedRecognitionAudio Prepare(byte[] wave)
    {
        using var reader = new WaveFileReader(new MemoryStream(wave, false));
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.SampleRate != SampleRate ||
            reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 || reader.Length % 2 != 0 || reader.Length > 48000L * 300)
            throw new InvalidOperationException("認識前の無音短縮は24kHz/16bit/monoの音声が必要です。");
        var pcm = new byte[(int)reader.Length];
        byte[]? cut = null;
        try
        {
            reader.ReadExactly(pcm);
            using var shortener = new RecognitionPcmShortener();
            cut = shortener.Push(pcm, complete: true);
            return new(cut.Length == pcm.Length ? wave : RecordingDevices.ToWave(cut), pcm.Length / 48000d, cut.Length / 48000d, cut.Length != pcm.Length);
        }
        finally { Array.Clear(pcm); if (cut is not null) Array.Clear(cut); }
    }
    public static PreparedRecognitionAudio Prepare(byte[] wave, AppSettings settings, bool alreadyProcessed = false)
    {
        if (alreadyProcessed)
        {
            using var reader = new WaveFileReader(new MemoryStream(wave, false));
            var seconds = reader.TotalTime.TotalSeconds;
            return new(wave, seconds, seconds, false);
        }
        var edited = UtteranceSilence.Apply(new(Guid.NewGuid(), wave, DateTimeOffset.UnixEpoch, 0), settings);
        var changed = !ReferenceEquals(wave, edited.Wave);
        return new(edited.Wave, changed ? edited.OriginalSeconds : edited.Seconds, edited.Seconds, changed);
    }
}

public sealed record PreparedRecognitionAudio(byte[] Wave, double OriginalSeconds, double Seconds, bool Changed) : IDisposable
{
    public void Dispose() { if (Changed) Array.Clear(Wave); }
}

/// <summary>最大200ms未満の保留。連続入力とWAVで同じ規則を使い、圧縮前の時刻へ写像する。</summary>
public sealed class RecognitionPcmShortener(bool mapTime = false) : IDisposable
{
    private readonly object _sync = new();
    private readonly byte[] _frame = new byte[RecognitionAudio.FrameSamples * 2];
    private readonly byte[] _pending = new byte[RecognitionAudio.MinimumSilenceSamples * 2];
    private int _frameBytes, _pendingBytes;
    private long _inputSamples, _outputSamples, _silenceSamples, _pendingStart;
    private bool _complete;
    private sealed record Range(long Output, long Input, long Count);
    private readonly List<Range> _ranges = [];
    public (long OutputSamples, bool LongSilence) State { get { lock (_sync) return (_outputSamples, _silenceSamples >= RecognitionAudio.MinimumSilenceSamples); } }
    public byte[] Push(ReadOnlySpan<byte> pcm, bool complete = false)
    {
        lock (_sync)
        {
            if (_complete) return [];
            if (pcm.Length % 2 != 0) throw new InvalidOperationException("PCMの長さが不正です。");
            using var output = new MemoryStream(pcm.Length + _pendingBytes + _frameBytes);
            while (!pcm.IsEmpty)
            {
                var count = Math.Min(_frame.Length - _frameBytes, pcm.Length);
                pcm[..count].CopyTo(_frame.AsSpan(_frameBytes)); _frameBytes += count; pcm = pcm[count..];
                if (_frameBytes == _frame.Length) { Process(_frame, output); _frameBytes = 0; }
            }
            if (complete)
            {
                if (_frameBytes > 0) { Process(_frame.AsSpan(0, _frameBytes), output); _frameBytes = 0; }
                FlushPending(output); _complete = true;
            }
            var bytes = output.ToArray(); Array.Clear(output.GetBuffer()); return bytes;
        }
    }
    private void Process(ReadOnlySpan<byte> frame, MemoryStream output)
    {
        var start = _inputSamples; _inputSamples += frame.Length / 2;
        if (!RecognitionAudio.Silent(frame))
        {
            FlushPending(output); _silenceSamples = 0; Emit(frame, start, output); return;
        }
        var keep = (int)Math.Min(frame.Length / 2, Math.Max(0, RecognitionAudio.RetainedSilenceSamples - _silenceSamples));
        if (keep > 0) Emit(frame[..(keep * 2)], start, output);
        _silenceSamples += frame.Length / 2;
        if (_silenceSamples >= RecognitionAudio.MinimumSilenceSamples) { Array.Clear(_pending, 0, _pendingBytes); _pendingBytes = 0; return; }
        var rest = frame[(keep * 2)..];
        if (!rest.IsEmpty)
        {
            if (_pendingBytes == 0) _pendingStart = start + keep;
            rest.CopyTo(_pending.AsSpan(_pendingBytes)); _pendingBytes += rest.Length;
        }
    }
    private void FlushPending(MemoryStream output)
    {
        if (_pendingBytes > 0) Emit(_pending.AsSpan(0, _pendingBytes), _pendingStart, output);
        Array.Clear(_pending, 0, _pendingBytes); _pendingBytes = 0;
    }
    private void Emit(ReadOnlySpan<byte> pcm, long input, MemoryStream output)
    {
        var count = pcm.Length / 2;
        if (mapTime)
        {
            if (_ranges.Count > 0 && _ranges[^1] is var last && last.Input + last.Count == input)
                _ranges[^1] = last with { Count = last.Count + count };
            else _ranges.Add(new(_outputSamples, input, count));
            if (_ranges.Count > 4096) _ranges.RemoveRange(0, 1024);
        }
        output.Write(pcm); _outputSamples += count;
    }
    public TimeSpan? OriginalTime(TimeSpan time, bool end = false)
    {
        lock (_sync)
        {
            var sample = (long)Math.Round(time.TotalSeconds * RecognitionAudio.SampleRate);
            if (!mapTime || _ranges.Count == 0 || sample < _ranges[0].Output) return null;
            var low = 0; var high = _ranges.Count - 1;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (_ranges[mid].Output < sample || !end && _ranges[mid].Output == sample) low = mid; else high = mid - 1;
            }
            var range = _ranges[low];
            return TimeSpan.FromSeconds((range.Input + Math.Clamp(sample - range.Output, 0, range.Count)) / 24000d);
        }
    }
    public void Dispose() { lock (_sync) { _complete = true; Array.Clear(_frame); Array.Clear(_pending); _ranges.Clear(); } }
}

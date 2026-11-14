using EchoType.Core;
using NAudio.Wave;

namespace EchoType.Windows;

/// <summary>保存と全文認識の共通カット。範囲はカット前のPCM位置。</summary>
public static class UtteranceSilence
{
    public static SpeechSegment Apply(SpeechSegment segment, AppSettings settings)
    {
        AsrEngines.ValidateSilence(settings);
        if (segment.SilenceProcessed) return segment;
        using var reader = new WaveFileReader(new MemoryStream(segment.Wave, false));
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.SampleRate != 24000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 || reader.Length % 2 != 0 || reader.Length > 48000L * 300)
            throw new InvalidOperationException("無音カットには24kHz/16bit/monoの音声が必要です。");
        var pcm = new byte[checked((int)reader.Length)];
        try
        {
            reader.ReadExactly(pcm); var count = pcm.Length / 2;
            var sound = new List<RetainedAudioRange>();
            for (var start = 0; start < count; start += 240)
            {
                var length = Math.Min(240, count - start);
                if (RecognitionAudio.Silent(pcm.AsSpan(start * 2, length * 2))) continue;
                if (sound.Count > 0 && sound[^1].StartSample + sound[^1].SampleCount == start)
                    sound[^1] = sound[^1] with { SampleCount = sound[^1].SampleCount + length };
                else sound.Add(new(start, length));
            }
            // 全無音は既存の破棄判定へ渡し、空WAVを作らない。
            if (sound.Count == 0) return segment with { SilenceProcessed = true, Seconds = count / 24000d };
            var edge = (int)Math.Round(settings.EdgeSilenceSeconds * 24000);
            var middle = (int)Math.Round(settings.InteriorSilenceSeconds * 24000);
            var ranges = new List<RetainedAudioRange>();
            var begin = Math.Max(0, sound[0].StartSample - edge); var cursor = begin;
            foreach (var part in sound)
            {
                var gap = part.StartSample - cursor;
                if (settings.CutInteriorSilence && ranges.Count > 0 && gap > middle)
                {
                    if (middle > 0) ranges.Add(new(cursor, middle));
                    cursor = part.StartSample;
                }
                var end = part.StartSample + part.SampleCount;
                ranges.Add(new(cursor, end - cursor)); cursor = end;
            }
            var tail = Math.Min(edge, count - cursor);
            if (tail > 0) ranges.Add(new(cursor, tail));
            var cut = new byte[ranges.Sum(r => r.SampleCount) * 2];
            try
            {
                var offset = 0;
                foreach (var range in ranges) { pcm.AsSpan(range.StartSample * 2, range.SampleCount * 2).CopyTo(cut.AsSpan(offset)); offset += range.SampleCount * 2; }
                return segment with { Wave = RecordingDevices.ToWave(cut), StartedAt = segment.StartedAt.AddSeconds(begin / 24000d), Seconds = cut.Length / 48000d,
                    OriginalStartedAt = segment.StartedAt, OriginalSeconds = count / 24000d, RetainedRanges = ranges.ToArray(), SilenceProcessed = true };
            }
            finally { Array.Clear(cut); }
        }
        finally { Array.Clear(pcm); }
    }
}

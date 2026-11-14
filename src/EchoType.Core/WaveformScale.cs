namespace EchoType.Core;

/// <summary>小音量も見える表示専用倍率。PCM・録音・dBメーターは変更しない。</summary>
public static class WaveformScale
{
    /// <summary>両音源の同じ表示倍率。発話開始閾値を下限として無音付近を拡大しすぎない。</summary>
    public static double SharedDisplayGain(float[] input, float[] playback, double minimumReference)
    {
        var peak = input.Concat(playback).Where(float.IsFinite).Select(x => Math.Abs((double)x)).DefaultIfEmpty().Max();
        if (peak == 0) return 1;
        return Math.Clamp(.8 / Math.Max(peak, Math.Max(.0001, minimumReference)), 1, 100);
    }

    public static double DisplayGain(float[] waveform)
    {
        var peak = waveform.Select(x => Math.Abs((double)x)).DefaultIfEmpty().Max();
        return peak < .0001 ? 1 : Math.Clamp(.8 / peak, 1, 100);
    }
}

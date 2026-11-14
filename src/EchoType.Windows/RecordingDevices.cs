using EchoType.Core;
using NAudio.Wave;
namespace EchoType.Windows;

public sealed record SpeechSegment(Guid Id, byte[] Wave, DateTimeOffset StartedAt, double Seconds)
{
    public bool SilenceProcessed { get; init; }
    public DateTimeOffset? OriginalStartedAt { get; init; }
    public double OriginalSeconds { get; init; }
    public RetainedAudioRange[] RetainedRanges { get; init; } = [];
    public double VoicedSeconds { get; init; }
    public DateTimeOffset? LastSoundAt { get; init; }
    public DateTimeOffset? SegmentedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AppSettings? CaptureSettings { get; init; }
    public AudioSourceInfo AudioSource { get; init; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public string SpeakerMatchingDetail { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public bool CaptionReplay { get; init; }
    public string CaptionUse { get; init; } = "";
    public bool CaptionObservationOnly { get; init; }
    public AsrResult? CaptionSnapshot { get; init; }
}
public sealed record InputDevice(int Number, string Name)
{
    public static InputDevice[] Defaults => [new(-1, "既定の録音デバイス"), new(-2, "既定の通信録音デバイス")];
    public IWaveIn Open(int bufferMilliseconds = 50) => Number is -1 or -2 ? new DefaultRecordingInput(Number == -2) :
        new WaveInEvent { DeviceNumber = Number, WaveFormat = new WaveFormat(24000, 16, 1), BufferMilliseconds = bufferMilliseconds };
    public override string ToString() => $"{Number}: {Name}";
    public static InputDevice Resolve(IReadOnlyList<InputDevice> devices, int number, string name, bool explicitSelection = false)
    {
        if (string.IsNullOrEmpty(name)) return devices.FirstOrDefault(d => d.Number == number) ?? throw new InvalidOperationException("録音デバイスを選択してください。");
        var matching = devices.Where(d => d.Name == name).ToArray();
        if (explicitSelection && matching.FirstOrDefault(d => d.Number == number) is { } chosen) return chosen;
        if (matching.Length != 1) throw new InvalidOperationException("保存したマイクがないか、同名のマイクがあります。一覧を更新して選び直してください。");
        return matching[0];
    }
}

public sealed record AudioMeter(double Rms, double Peak, float[] Waveform)
{
    public double Db => Rms <= 0 ? -96 : Math.Max(-96, 20 * Math.Log10(Rms));
    public static AudioMeter Measure(byte[] pcm)
    {
        var count = pcm.Length / 2; double sum = 0, peak = 0; var wave = new float[Math.Min(48, count)];
        for (var i = 0; i < count; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            sum += sample * sample; peak = Math.Max(peak, Math.Abs(sample));
            var bin = i * wave.Length / count;
            if (Math.Abs(sample) > Math.Abs(wave[bin])) wave[bin] = sample;
        }
        return new(count == 0 ? 0 : Math.Sqrt(sum / count), peak, wave);
    }
}

public static class RecordingDevices
{
    public static IReadOnlyList<InputDevice> InputDevices() => [.. InputDevice.Defaults, .. Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => new InputDevice(i, WaveInEvent.GetCapabilities(i).ProductName))];
    public static byte[] ToWave(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), new WaveFormat(24000, 16, 1))) writer.Write(pcm);
        return stream.ToArray();
    }
}

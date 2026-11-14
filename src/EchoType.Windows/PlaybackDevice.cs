using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EchoType.Windows;

public sealed record PlaybackDevice(string Id, string Name)
{
    public override string ToString() => Name;
    public static IReadOnlyList<PlaybackDevice> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<PlaybackDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        return result;
    }
    public static PlaybackDevice Resolve(string id) => List().FirstOrDefault(d => d.Id == id)
        ?? throw new InvalidOperationException("選択した再生デバイスがありません。一覧を更新して選び直してください。");
    public IWaveIn Open() => new LoopbackInput(Id);

    public static IWavePlayer OpenReviewOutput(string id)
    {
        if (string.IsNullOrEmpty(id)) return new WaveOutEvent();
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(candidate =>
            {
                if (candidate.ID == id) return candidate;
                candidate.Dispose();
                return null;
            }).FirstOrDefault(candidate => candidate is not null);
        if (device is null)
            throw new InvalidOperationException("確認用の再生デバイスが接続されていません。設定で一覧を更新し、出力先を選び直してください。");
        try { return new ReviewOutput(device); }
        catch { device.Dispose(); throw; }
    }

    private sealed class ReviewOutput(MMDevice device) : IWavePlayer
    {
        private readonly WasapiOut _output = new(device, AudioClientShareMode.Shared, false, 100);
        public PlaybackState PlaybackState => _output.PlaybackState;
        public WaveFormat OutputWaveFormat => _output.OutputWaveFormat;
        public float Volume { get => _output.Volume; set => _output.Volume = value; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add => _output.PlaybackStopped += value; remove => _output.PlaybackStopped -= value; }
        public void Init(IWaveProvider waveProvider) => _output.Init(waveProvider);
        public void Play() => _output.Play();
        public void Pause() => _output.Pause();
        public void Stop() => _output.Stop();
        public void Dispose()
        {
            try { _output.Dispose(); }
            finally { device.Dispose(); }
        }
    }

    private sealed class LoopbackInput : IWaveIn
    {
        private readonly MMDevice _device;
        private readonly WasapiLoopbackCapture _capture;
        public LoopbackInput(string id)
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(id);
            try
            {
                if (_device.State != DeviceState.Active || _device.DataFlow != DataFlow.Render)
                    throw new InvalidOperationException("選択した再生デバイスは使用できません。");
                _capture = new(_device) { WaveFormat = new WaveFormat(24000, 16, 1) };
            }
            catch { _device.Dispose(); throw; }
        }
        public WaveFormat WaveFormat { get => _capture.WaveFormat; set => _capture.WaveFormat = value; }
        public event EventHandler<WaveInEventArgs>? DataAvailable { add => _capture.DataAvailable += value; remove => _capture.DataAvailable -= value; }
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add => _capture.RecordingStopped += value; remove => _capture.RecordingStopped -= value; }
        public void StartRecording() => _capture.StartRecording();
        public void StopRecording() => _capture.StopRecording();
        public void Dispose() { _capture.Dispose(); _device.Dispose(); }
    }
}

/// <summary>空のloopback通知で失われる無音を補う。OSバッファの揺らぎは200msまで許容する。</summary>
public static class LoopbackTimeline
{
    public static int PaddingSamples(double elapsedSeconds, long samplesWritten, int incomingBytes)
    {
        var missing = elapsedSeconds * 24000 - samplesWritten - incomingBytes / 2;
        return missing > 4800 ? (int)Math.Min(int.MaxValue, Math.Max(0, missing - 2400)) : 0;
    }
}

using NAudio.Wave;

namespace EchoType.Windows;

/// <summary>押下中の音声だけを保持する。通常録音の保存・発話区切りには接続しない。</summary>
public sealed class VoiceInputCapture : IDisposable
{
    private readonly IWaveIn _input;
    private readonly MemoryStream _pcm = new();
    private readonly object _sync = new();
    private readonly int _maximumBytes;
    private readonly bool _monitorOnly;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepting = true;
    private Exception? _error;
    private int _disposed;
    private AudioMeter _meter = new(0, 0, []);
    private readonly Queue<float[]> _wave = new();
    public AudioMeter Meter() { lock (_sync) return _meter with { Waveform = _wave.SelectMany(frame => frame).ToArray() }; }
    public event Action? LimitReached;
    public event Action? Interrupted;
    public event Action<byte[]>? Frame;
    public VoiceInputCapture(IWaveIn input, int maximumSeconds, bool monitorOnly = false)
    {
        if (maximumSeconds is < 2 or > 300) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        _input = input; _maximumBytes = maximumSeconds * 48000; _monitorOnly = monitorOnly;
        _input.DataAvailable += DataAvailable;
        _input.RecordingStopped += (_, e) =>
        {
            bool unexpected;
            lock (_sync) { unexpected = _accepting; _accepting = false; _error = e.Exception; ResetMeter(); }
            _stopped.TrySetResult();
            if (unexpected) Interrupted?.Invoke();
        };
    }
    public void Start() => _input.StartRecording();
    private void DataAvailable(object? sender, WaveInEventArgs e)
    {
        var reached = false;
        lock (_sync)
        {
            if (!_accepting) return;
            var count = _monitorOnly ? e.BytesRecorded : Math.Min(e.BytesRecorded, _maximumBytes - (int)_pcm.Length);
            if (!_monitorOnly) _pcm.Write(e.Buffer, 0, count);
            var samples = e.Buffer.AsSpan(0, count).ToArray();
            try { _meter = AudioMeter.Measure(samples); }
            finally { Array.Clear(samples); }
            _wave.Enqueue(_meter.Waveform); while (_wave.Count > 100) _wave.Dequeue();
            if (!_monitorOnly && count > 0) Frame?.Invoke(e.Buffer.AsSpan(0, count).ToArray());
            if (_pcm.Length >= _maximumBytes) { _accepting = false; reached = true; }
        }
        if (reached) LimitReached?.Invoke();
    }
    public async Task<SpeechSegment?> StopAsync(bool keep)
    {
        lock (_sync) { _accepting = false; ResetMeter(); }
        _input.StopRecording();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (_sync)
        {
            if (_error is not null) throw new InvalidOperationException("マイクの録音が中断されました。", _error);
            if (!keep || _pcm.Length == 0) return null;
            var pcm = _pcm.ToArray();
            try { return new(Guid.NewGuid(), RecordingDevices.ToWave(pcm), _started, pcm.Length / 48000d); }
            finally { Array.Clear(pcm); }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sync) { _accepting = false; ResetMeter(); }
        _input.Dispose();
        lock (_sync) { Array.Clear(_pcm.GetBuffer()); _pcm.Dispose(); }
    }
    private void ResetMeter() { _meter = new(0, 0, []); _wave.Clear(); }
}

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EchoType.Windows;

/// <summary>録音を開くたびにWindowsの役割別既定入力を解決する。</summary>
internal sealed class DefaultRecordingInput : IWaveIn
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    public DefaultRecordingInput(bool communications)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, communications ? Role.Communications : Role.Console);
        try { _capture = new WasapiCapture(_device) { WaveFormat = new WaveFormat(24000, 16, 1) }; }
        catch { _device.Dispose(); throw; }
    }
    public WaveFormat WaveFormat { get => _capture.WaveFormat; set => _capture.WaveFormat = value; }
    public event EventHandler<WaveInEventArgs>? DataAvailable { add => _capture.DataAvailable += value; remove => _capture.DataAvailable -= value; }
    public event EventHandler<StoppedEventArgs>? RecordingStopped { add => _capture.RecordingStopped += value; remove => _capture.RecordingStopped -= value; }
    public void StartRecording() => _capture.StartRecording();
    public void StopRecording() => _capture.StopRecording();
    public void Dispose() { _capture.Dispose(); _device.Dispose(); }
}

using EchoType.Core;
using EchoType.Windows;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private CancellationTokenSource? _voiceCaptionStop;
    private string _voiceCaptionKey = "";
    private WindowsCaptionSnapshot? _voiceCaptionSnapshot;
    private VoiceInputCaptionPreview? _voiceCaptionPreview;
    private bool UsesVoiceCaptions() => Settings.LiveEngine == AsrEngines.WindowsCaptions || Settings.RecognitionModels().Contains(AsrEngines.WindowsCaptions);
    private AppSettings VoiceCaptionSettings()
    {
        var device = ResolveDevice();
        return Effective(Settings).Recognition with { DeviceNumber = device.Number, DeviceName = device.Name };
    }
    private void UpdateVoiceCaptions()
    {
        if (_stopping || _noAudio || _window?.IsVisible != true || !UsesVoiceCaptions()) { StopVoiceCaptions(); return; }
        AppSettings settings;
        try { settings = VoiceCaptionSettings(); }
        catch (Exception ex) { StopVoiceCaptions(); Say(ex.Message); return; }
        var key = settings.DeviceNumber + ":" + settings.DeviceName;
        if (_voiceCaptionKey == key && _voiceCaptionStop is not null) return;
        StopVoiceCaptions(); _voiceCaptionKey = key;
        var stop = _voiceCaptionStop = new CancellationTokenSource();
        Track(ObserveAsync());
        async Task ObserveAsync()
        {
            try
            {
                await Task.Run(() => WindowsCaptionComparison.ObserveLiveAsync(AudioSourceKind.Microphone, settings, snapshot =>
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (stop.IsCancellationRequested || _voiceCaptionStop != stop) return;
                        _voiceCaptionSnapshot = snapshot;
                        _voiceCaptionPreview?.Observe(snapshot);
                    }), stop.Token, "専用音声入力"));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { if (!stop.IsCancellationRequested) Say("Windows字幕: " + ex.Message); }
            finally { if (_voiceCaptionStop == stop) _voiceCaptionStop = null; stop.Dispose(); }
        }
    }
    private void StopVoiceCaptions()
    {
        var stop = _voiceCaptionStop; _voiceCaptionStop = null; _voiceCaptionKey = ""; _voiceCaptionSnapshot = null;
        try { stop?.Cancel(); } catch (ObjectDisposedException) { }
        _voiceCaptionPreview = null;
    }
}

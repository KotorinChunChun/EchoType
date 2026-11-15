namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private bool _paused;
    private int _pauseGeneration;

    public bool IsPaused => _paused;

    internal string? ManagementStopBlockReason()
    {
        if (InitialWhisperSetup.IsInstalling) return "音声認識モデルの準備完了または中止後に再試行してください。";
        return ConfigurationLocked || _automaticProcessing || _automaticSending || _traySettings is not null || _window?.SettingsOpen == true
            ? "録音・認識・入力または設定の終了後に再試行してください。"
            : null;
    }

    public void SetPaused(bool paused)
    {
        if (_stopping || _paused == paused) return;

        _paused = paused;
        _keyboard?.SetPaused(paused);
        Interlocked.Increment(ref _pauseGeneration);
        if (paused)
        {
            _warmupCancellation?.Cancel();
            _window?.CloseSettings();
            Dismiss();
            _lastExternal = null;
            _lastExternalFocus = default;
            return;
        }

        TrackTarget();
        StartWhisperWarmup();
    }
}

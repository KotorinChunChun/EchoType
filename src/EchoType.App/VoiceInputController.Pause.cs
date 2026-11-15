namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private bool _paused;
    private int _pauseGeneration;

    public bool IsPaused => _paused;

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

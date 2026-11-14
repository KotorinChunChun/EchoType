using EchoType.Windows;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private sealed class NoPreview : IVoiceInputLive
    {
        public event Action<string>? Updated { add { } remove { } }
        public void Feed(byte[] pcm) { }
        public Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct) => Task.FromResult(new VoiceInputLiveResult(""));
        public void Dispose() { }
    }

    private void StopSecondaryRecognition()
    {
        _warmupCancellation?.Cancel();
        foreach (var recording in _previewRecordings.ToArray())
        {
            var preview = recording.Live;
            recording.Capture.Frame -= preview.Feed;
            recording.Live = new NoPreview();
            recording.Entry.LiveEngine = "";
            preview.Dispose(); Track(preview.Stopped);
        }
        if (_session is not { } session) return;
        foreach (var (key, cancellation) in session.RecognitionJobs.ToArray())
        {
            var entry = session.Entries.FirstOrDefault(item => item.Number == key.Number);
            var primary = entry?.RecognitionSettings?.Models.FirstOrDefault(m => m.Length > 0);
            if (key.Engine == primary) continue;
            session.Buffer.ConsumeModel(key.Number, key.Engine);
            cancellation.Cancel();
        }
        RenderResults(session);
    }
}

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

}

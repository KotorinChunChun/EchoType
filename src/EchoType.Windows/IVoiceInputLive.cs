namespace EchoType.Windows;

public sealed record VoiceInputLiveResult(string Text, string Error = "");
public interface IVoiceInputLive : IDisposable
{
    Task Stopped => Task.CompletedTask;
    event Action<string>? Updated;
    void Feed(byte[] pcm);
    Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct);
}

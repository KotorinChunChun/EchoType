using System.Text.Json.Serialization;

namespace EchoType.Core;

public enum AudioSourceKind { Microphone, Playback }

/// <summary>録音開始時点の入力元。後で設定を変更しても過去の発言者を変更しない。</summary>
public sealed record AudioSourceInfo
{
    public AudioSourceKind Kind { get; init; }
    public string DeviceId { get; init; } = "";
    public string SpeakerName { get; init; } = "";
    public Guid? DiarizationParentId { get; init; }
    public bool OverlappingSpeech { get; init; }
    public bool SpeakerNameManual { get; init; }
    [JsonIgnore] public string KindLabel => Kind == AudioSourceKind.Playback ? "再生" : "マイク";
    [JsonIgnore] public string SpeakerLabel => string.IsNullOrWhiteSpace(SpeakerName)
        ? Kind == AudioSourceKind.Playback ? "相手（未設定）" : "あなた" : SpeakerName;
}

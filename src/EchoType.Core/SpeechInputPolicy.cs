namespace EchoType.Core;

/// <summary>読み上げによる認識抑制は音源ごとの明示設定に従う。旧設定は既定OFF。</summary>
public static class SpeechInputPolicy
{
    public static bool Suppresses(AppSettings settings, AudioSourceKind source, bool reading) => reading &&
        (source == AudioSourceKind.Playback ? settings.SuppressPlaybackDuringSpeech : settings.SuppressMicrophoneDuringSpeech);
}

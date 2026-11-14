namespace EchoType.Core;

/// <summary>OS字幕の単一観測を全文へ優先配分する。</summary>
public static class AudioModelRouting
{
    public static string ImmediateDeviceEngine(string engine, bool captionDeviceMatches) => engine == AsrEngines.WindowsCaptions && !captionDeviceMatches ? "windows" : engine;
    public static AppSettings ForSource(AppSettings settings, AudioSourceKind kind) => kind == AudioSourceKind.Playback ? settings with
    {
        ImmediateAsrEngine = settings.PlaybackImmediateAsrEngine ?? settings.ImmediateAsrEngine,
        PrimaryAsrEngine = settings.PlaybackPrimaryAsrEngine ?? (settings.PrimaryAsrEngine == AsrEngines.WindowsCaptions ? "windows" : settings.PrimaryAsrEngine)
    } : settings;
    private static bool Full(AppSettings s) => s.PrimaryAsrEngine == AsrEngines.WindowsCaptions;
    public static AudioSourceKind? CaptionOwner(AppSettings s, bool microphone, bool playback)
    {
        var p = ForSource(s, AudioSourceKind.Playback);
        if (microphone && Full(s)) return AudioSourceKind.Microphone;
        if (playback && Full(p)) return AudioSourceKind.Playback;
        if (microphone && s.ImmediateAsrEngine == AsrEngines.WindowsCaptions) return AudioSourceKind.Microphone;
        if (playback && p.ImmediateAsrEngine == AsrEngines.WindowsCaptions) return AudioSourceKind.Playback;
        return null;
    }
    public static AppSettings Effective(AppSettings s, AudioSourceKind kind, bool microphone, bool playback, bool captionDeviceMatches = true)
    {
        var selected = ForSource(s, kind);
        var owner = CaptionOwner(s, microphone, playback);
        var reserved = microphone && Full(s) || playback && Full(ForSource(s, AudioSourceKind.Playback));
        return selected with { ImmediateAsrEngine = ImmediateDeviceEngine(selected.ImmediateAsrEngine, captionDeviceMatches && !reserved && owner == kind) };
    }
    public static void Validate(AppSettings s)
    {
        var p = ForSource(s, AudioSourceKind.Playback);
        if (!AsrEngines.Immediate.Any(e => e.Id == p.ImmediateAsrEngine)) throw new InvalidOperationException("再生側の即時認識モデルを選んでください。");
        if (!AsrEngines.IsSelectable(p.PrimaryAsrEngine)) throw new InvalidOperationException("再生側の全文認識モデルを選んでください。");
        if (Full(s) && Full(p)) throw new InvalidOperationException("Windowsライブキャプションの全文は入力か再生の一方だけに設定してください。もう一方は別のモデルを選んでください。");
    }

    public static string[] CaptionAssignments(AppSettings settings)
    {
        var playback = ForSource(settings, AudioSourceKind.Playback);
        var assignments = new List<string>();
        if (Full(settings)) assignments.Add("入力の全文認識");
        if (Full(playback)) assignments.Add("再生の全文認識");
        if (settings.LocalVoiceCommands && settings.ImmediateAsrEngine == AsrEngines.WindowsCaptions) assignments.Add("入力の即時認識");
        if (settings.LocalVoiceCommands && settings.PlaybackImmediateAsrEngine == AsrEngines.WindowsCaptions) assignments.Add("再生の即時認識");
        if (settings.WindowsLiveCaptions) assignments.Add("上部のOS字幕");
        return assignments.ToArray();
    }

    public static void ValidateCaptionChange(AppSettings current, AppSettings desired)
    {
        var assignments = CaptionAssignments(desired);
        if (assignments.Length > 1 && !assignments.SequenceEqual(CaptionAssignments(current)))
            throw new InvalidOperationException("Win LiveCaptions UIAは同時に1箇所のみ使用できます。選択中の箇所 " + string.Join("、", assignments));
    }
}

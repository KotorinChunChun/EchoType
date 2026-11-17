namespace EchoType.Core;

public sealed record SpeechConnection(Guid Id, string Name, ExternalSpeechSettings Settings)
{
    public static SpeechConnection WindowsDebug { get; } =
        new(new Guid("53a7ee84-6df7-4af8-973a-2dc793227218"), ExternalSpeechSettings.WindowsDebugDefault.Model,
            ExternalSpeechSettings.WindowsDebugDefault);

    public void Validate()
    {
        ValidateIdentity();
        Settings.ValidateConnection();
    }

    internal void ValidateIdentity()
    {
        if (Id == Guid.Empty || Id == WindowsDebug.Id || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl))
            throw new InvalidOperationException("接続名は100文字以内で指定してください。");
    }
}

public sealed record SpeechSelection(Guid ConnectionId, string Model);

public sealed partial record VoiceInputSettings
{
    public SpeechConnection[] SpeechConnections { get; init; } = [];
    public SpeechSelection? FinalSelection { get; init; }
    public SpeechSelection? PreviewSelection { get; init; }

    public ExternalSpeechSettings FinalSpeech() => ResolveSpeech(FinalSelection, ExternalSpeech);
    public ExternalSpeechSettings PreviewSpeech() => ResolveSpeech(PreviewSelection, LiveSpeech);

    private ExternalSpeechSettings ResolveSpeech(SpeechSelection? selection, ExternalSpeechSettings legacy)
    {
        if (selection is null) return legacy.Enabled ? legacy : ExternalSpeechSettings.SpeechCppDefault;
        if (selection.ConnectionId == SpeechConnection.WindowsDebug.Id) return ExternalSpeechSettings.WindowsDebugDefault;
        var connection = SpeechConnections.FirstOrDefault(c => c.Id == selection.ConnectionId)
            ?? throw new InvalidOperationException("選択した音声認識の接続が登録されていません。");
        return connection.Settings with { Model = selection.Model };
    }

    public VoiceInputSettings MigrateSpeechConnections()
    {
        var connections = SpeechConnections.ToList();
        var final = FinalSelection ?? Import(ExternalSpeech, "確定の接続");
        var preview = PreviewSelection ?? Import(LiveSpeech, "即時表示の接続");
        return this with { SpeechConnections = connections.ToArray(), FinalSelection = final, PreviewSelection = preview,
            ExternalSpeech = new(), LiveSpeech = new() };

        SpeechSelection? Import(ExternalSpeechSettings legacy, string name)
        {
            if (!legacy.Enabled || legacy == ExternalSpeechSettings.SpeechCppDefault) return null;
            if (legacy.Mode == ExternalSpeechMode.WindowsDebug) return new(SpeechConnection.WindowsDebug.Id, "");
            var config = legacy with { Model = "" };
            var match = connections.Find(c => c.Settings == config);
            if (match is null)
            {
                match = new(Guid.NewGuid(), name, config);
                connections.Add(match);
            }
            return new(match.Id, legacy.Model);
        }
    }

    public VoiceInputSettings WithoutSpeechConnections() => this with
    {
        ExternalSpeech = new(), LiveSpeech = new(), SpeechConnections = [],
        FinalSelection = null, PreviewSelection = null, LiveDisplayEnabled = false
    };

    private void ValidateSpeechConnections()
    {
        var ids = new HashSet<Guid>();
        foreach (var connection in SpeechConnections)
        {
            connection.ValidateIdentity();
            if (!ids.Add(connection.Id)) throw new InvalidOperationException("音声認識の接続IDが重複しています。");
        }
        var final = FinalSpeech();
        if (FinalSelection is not null || ExternalSpeech.Enabled)
        {
            if (final.Mode == ExternalSpeechMode.TranscriptionServer && final.Model.Length == 0) final.ValidateConnection();
            else final.ValidateForRecognition();
        }
        if (LiveDisplayEnabled) PreviewSpeech().ValidateForRecognition();
        else if (PreviewSelection is not null) _ = PreviewSpeech();
    }
}

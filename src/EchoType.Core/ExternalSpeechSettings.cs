namespace EchoType.Core;

public enum ExternalSpeechMode { Local, Server, ApiKey, SpeechCpp, TranscriptionServer, WindowsDebug }

public sealed record ExternalSpeechSettings
{
    public ExternalSpeechMode Mode { get; init; }
    public string Endpoint { get; init; } = "";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Enabled => Mode != ExternalSpeechMode.Local;

    public static ExternalSpeechSettings SpeechCppDefault { get; } = new()
    {
        Mode = ExternalSpeechMode.SpeechCpp,
        Endpoint = "http://127.0.0.1:8080/v1/audio/transcriptions"
    };
    public static ExternalSpeechSettings TranscriptionServerDefault { get; } = new()
    {
        Mode = ExternalSpeechMode.TranscriptionServer,
        Endpoint = "http://127.0.0.1:8769/v1/audio/transcriptions"
    };
    public static ExternalSpeechSettings WindowsDebugDefault { get; } = new()
    {
        Mode = ExternalSpeechMode.WindowsDebug,
        Model = "Windows標準（デバッグ）"
    };

    public Uri ValidateAddress()
    {
        if (Mode is not (ExternalSpeechMode.Server or ExternalSpeechMode.ApiKey or ExternalSpeechMode.SpeechCpp or ExternalSpeechMode.TranscriptionServer) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("外部認識のURLにはHTTPSを指定してください。このPC内の接続にはHTTPも指定できます。");
        if (Mode == ExternalSpeechMode.SpeechCpp &&
            (!uri.IsLoopback || uri.AbsolutePath != "/v1/audio/transcriptions" || uri.Query.Length != 0))
            throw new InvalidOperationException("speech.cppには、このPC内の /v1/audio/transcriptions のURLを指定してください。");
        if (Mode == ExternalSpeechMode.TranscriptionServer &&
            (uri.AbsolutePath != "/v1/audio/transcriptions" || uri.Query.Length != 0))
            throw new InvalidOperationException("TranscriptionServerの /v1/audio/transcriptions のURLを指定してください。");
        return uri;
    }

    public Uri ValidateConnection()
    {
        var uri = ValidateAddress();
        if (Mode == ExternalSpeechMode.ApiKey && (string.IsNullOrEmpty(ApiKey) || ApiKey.Length > 4096 || ApiKey.Any(c => c is < '!' or > '~')))
            throw new InvalidOperationException("外部認識のAPIキーを確認してください。");
        return uri;
    }

    public Uri ValidateEndpoint()
    {
        var uri = ValidateConnection();
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsControl))
            throw new InvalidOperationException("外部認識のモデル名を指定してください。");
        return uri;
    }

    public void ValidateForRecognition()
    {
        if (Mode != ExternalSpeechMode.WindowsDebug) ValidateEndpoint();
    }
}

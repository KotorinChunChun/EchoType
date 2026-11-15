namespace EchoType.Core;

public enum ExternalSpeechMode { Local, Server, ApiKey }

public sealed record ExternalSpeechSettings
{
    public ExternalSpeechMode Mode { get; init; }
    public string Endpoint { get; init; } = "";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Enabled => Mode != ExternalSpeechMode.Local;

    public Uri ValidateEndpoint()
    {
        if (Mode is not (ExternalSpeechMode.Server or ExternalSpeechMode.ApiKey) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("外部認識のURLにはHTTPSを指定してください。このPC内の接続にはHTTPも指定できます。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsControl))
            throw new InvalidOperationException("外部認識のモデル名を指定してください。");
        if (Mode == ExternalSpeechMode.ApiKey && (string.IsNullOrEmpty(ApiKey) || ApiKey.Length > 4096 || ApiKey.Any(c => c is < '!' or > '~')))
            throw new InvalidOperationException("外部認識のAPIキーを確認してください。");
        return uri;
    }
}

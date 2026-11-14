namespace EchoType.Core;

public sealed record ApiConnection(Guid Id, string Name, string Purpose, string Service, string Scope = "app", string ExistingFile = "");

public sealed record ApiEnvSettings
{
    public const string Tts = "tts", Stt = "stt", Decision = "decision", AppDefault = "app", Legacy = "legacy";
    public ApiConnection[] Connections { get; init; } = [];
    public Dictionary<string, string> SelectedConnections { get; init; } = [];
    public bool RegistrationsOnly { get; init; }
    public string Directory { get; init; } = "";
    public string Voisona { get; init; } = "";
    public string OpenAi { get; init; } = "";
    public string Gemini { get; init; } = "";
    public string Cloudflare { get; init; } = "";
    public string Fds { get; init; } = "";
    public string File(string service, string defaultDirectory)
    {
        var selected = service switch { "voisona" => Voisona, "openai" => OpenAi, "gemini" => Gemini, "cloudflare" => Cloudflare, "fds" => Fds, _ => throw new ArgumentException("未対応の接続設定です。") };
        var file = selected.Length > 0 ? selected : Path.Combine(Directory.Length > 0 ? Directory : defaultDirectory, service + ".env");
        return LocalFile(file);
    }
    public static string[] ForPurpose(string purpose) => purpose switch
    {
        Tts => ["voisona", "openai", "gemini"], Stt => ["openai", "gemini"], Decision => ["fds", "cloudflare", "openai"],
        _ => throw new ArgumentException("未対応の接続用途です。")
    };
    public static string Slot(string purpose, string service) => purpose + "/" + service;
    public string BaseDirectory(string defaultDirectory) => Directory.Length == 0 ? defaultDirectory : Directory;
    public string Selection(string purpose, string service, string defaultDirectory)
    {
        if (SelectedConnections.TryGetValue(Slot(purpose, service), out var selection)) return selection;
        if (RegistrationsOnly) return "";
        if (purpose == Decision && service == "openai") return AppDefault;
        return System.IO.File.Exists(File(service, defaultDirectory)) || HasLegacyOverride(service) ? Legacy : AppDefault;
    }
    private bool HasLegacyOverride(string service) => service switch
    {
        "voisona" => Voisona.Length > 0, "openai" => OpenAi.Length > 0, "gemini" => Gemini.Length > 0,
        "cloudflare" => Cloudflare.Length > 0, "fds" => Fds.Length > 0, _ => false
    };
    public string Resolve(string purpose, string service, string defaultDirectory, string applicationId)
    {
        if (!ForPurpose(purpose).Contains(service)) throw new InvalidOperationException("接続の用途とサービスが一致しません。");
        var selection = Selection(purpose, service, defaultDirectory);
        if (selection == Legacy) return File(service, defaultDirectory);
        if (selection is AppDefault or "") return LocalFile(Path.Combine(AppDirectory(defaultDirectory, applicationId), purpose, service + ".env"));
        var connection = Connections.SingleOrDefault(c => c.Id.ToString("N") == selection && c.Purpose == purpose && c.Service == service)
            ?? throw new InvalidOperationException("選択した接続が見つかりません。");
        return ConnectionFile(connection, defaultDirectory, applicationId);
    }
    public string ConnectionFile(ApiConnection connection, string defaultDirectory, string applicationId) => LocalFile(connection.Scope switch
    {
        "app" => Path.Combine(AppDirectory(defaultDirectory, applicationId), connection.Purpose, FileName(connection)),
        "shared" when connection.ExistingFile.Length == 0 => Path.Combine(BaseDirectory(defaultDirectory), "shared", connection.Purpose, FileName(connection)),
        "shared" or "existing" => connection.ExistingFile,
        _ => throw new InvalidOperationException("接続の保存区分が不正です。")
    });
    private static string FileName(ApiConnection connection) => $"{connection.Service}-{connection.Id:N}.env";
    private string AppDirectory(string defaultDirectory, string applicationId)
    {
        if (applicationId.Length == 0 || applicationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidOperationException("アプリ識別子が不正です。");
        return Path.Combine(BaseDirectory(defaultDirectory), "apps", applicationId);
    }
    private static string LocalFile(string file)
    {
        if (!Path.IsPathFullyQualified(file) || file.StartsWith(@"\\") || file.IndexOf(':', 2) >= 0 || file.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("envはローカルの絶対パスで指定してください。");
        return Path.GetFullPath(file);
    }
    public ApiEnvSettings Validate(string defaultDirectory, string applicationId = "EchoType")
    {
        _ = LocalFile(Path.Combine(AppDirectory(defaultDirectory, applicationId), "validate.env"));
        foreach (var service in Services) _ = File(service, defaultDirectory);
        if (Connections.Select(c => c.Id).Distinct().Count() != Connections.Length) throw new InvalidOperationException("接続IDが重複しています。");
        foreach (var c in Connections)
        {
            if (c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100 || c.Name.Any(char.IsControl) || !ForPurpose(c.Purpose).Contains(c.Service))
                throw new InvalidOperationException("接続の名前・用途・サービスが不正です。");
            if (c.Scope == AppDefault && c.ExistingFile.Length > 0)
                throw new InvalidOperationException("アプリ専用の接続はアプリのフォルダーへ保存してください。");
            _ = ConnectionFile(c, defaultDirectory, applicationId);
        }
        foreach (var purpose in new[] { Tts, Stt, Decision })
            foreach (var service in ForPurpose(purpose)) _ = Resolve(purpose, service, defaultDirectory, applicationId);
        return this;
    }
    public static readonly string[] Services = ["voisona", "openai", "gemini", "cloudflare", "fds"];
    public bool Registered(string purpose, string service) => SelectedConnections.TryGetValue(Slot(purpose, service), out var value) && value.Length > 0;

    public static string ServiceName(string service) => service switch
    {
        "voisona" => "VoiSona Talk", "openai" => "OpenAI", "gemini" => "Gemini", "cloudflare" => "Cloudflare Clef", "fds" => "fds", "windows" => "Windows音声", _ => service
    };
}

public sealed record TtsSettings
{
    public string Provider { get; init; } = "voisona";
    public bool CloudAllowed { get; init; }
    public bool WindowsFallback { get; init; } = true;
    public string OpenAiModel { get; init; } = "gpt-4o-mini-tts";
    public string OpenAiVoice { get; init; } = "coral";
    public string GeminiModel { get; init; } = "gemini-3.8-flash-lite-tts";
    public string GeminiVoice { get; init; } = "Kore";
    public string VoisonaVoice { get; init; } = "ui_ja_JP";
    public string VoisonaVoiceVersion { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 120;
    public TtsSettings Validate()
    {
        if (Provider is not ("voisona" or "openai" or "gemini" or "windows") || TimeoutSeconds is < 10 or > 600 ||
            new[] { OpenAiModel, OpenAiVoice, GeminiModel, GeminiVoice, VoisonaVoice }.Any(s => s.Length is < 1 or > 120 || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) ||
            VoisonaVoiceVersion.Length > 120 || VoisonaVoiceVersion.Any(char.IsControl))
            throw new InvalidOperationException("TTSのサービス・モデル・声・待ち時間が不正です。");
        return this;
    }
}

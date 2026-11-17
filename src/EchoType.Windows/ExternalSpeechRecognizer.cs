using EchoType.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EchoType.Windows;

public sealed record TranscriptionModel(string Id, string Label);

public static class ExternalSpeechRecognizer
{
    private static readonly HttpClient Connection = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private static readonly HttpClient LoopbackConnection = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });

    public static Task<AsrResult> RecognizeAsync(byte[] wave, ExternalSpeechSettings settings, CancellationToken cancellation) =>
        settings.Mode == ExternalSpeechMode.WindowsDebug
            ? WindowsSpeechRecognizer.RecognizeAsync(wave, cancellation)
            : RecognizeAsync(SelectConnection(settings), wave, settings, cancellation);

    private static HttpClient SelectConnection(ExternalSpeechSettings settings) =>
        settings.Mode is ExternalSpeechMode.SpeechCpp or ExternalSpeechMode.TranscriptionServer &&
        Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback ? LoopbackConnection : Connection;

    internal static async Task<AsrResult> RecognizeAsync(HttpClient connection, byte[] wave, ExternalSpeechSettings settings, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        const string engine = AsrEngines.ExternalTranscription;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ValidateEndpoint());
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wave);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "recording.wav");
        form.Add(new StringContent(settings.Model), "model");
        form.Add(new StringContent("ja"), "language");
        form.Add(new StringContent("json"), "response_format");
        request.Content = form;
        if (settings.Mode == ExternalSpeechMode.ApiKey)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        try
        {
            if (settings.Mode == ExternalSpeechMode.TranscriptionServer)
            {
                var session = await GetTranscriptionServerSessionAsync(connection, request.RequestUri!, timeout.Token).ConfigureAwait(false);
                if (!session.AcceptingJobs) return new(engine, "", watch.Elapsed.TotalSeconds, PreparationMessage);
                request.Headers.Add("X-Local-Token", session.Token);
            }
            using var response = await connection.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(engine, "", watch.Elapsed.TotalSeconds, StatusError((int)response.StatusCode, settings.Mode));
            await response.Content.LoadIntoBufferAsync(1_000_000, timeout.Token).ConfigureAwait(false);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return new(engine, "", watch.Elapsed.TotalSeconds, "外部音声認識の応答形式を確認してください。");
            return new(engine, text.GetString()!.Trim(), watch.Elapsed.TotalSeconds) { RecognitionSeconds = watch.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new(engine, "", watch.Elapsed.TotalSeconds, "外部音声認識の応答がタイムアウトしました。"); }
        catch (JsonException)
        { return new(engine, "", watch.Elapsed.TotalSeconds, "音声認識サーバーの応答が有効なJSONではありません。"); }
        catch (InvalidOperationException error) when (settings.Mode == ExternalSpeechMode.TranscriptionServer)
        { return new(engine, "", watch.Elapsed.TotalSeconds, error.Message); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { return new(engine, "", watch.Elapsed.TotalSeconds, "音声認識サーバーと通信できません。起動状態とURLを確認してください。"); }
    }

    private const string PreparationMessage = "TranscriptionServerは文字起こしの受付準備中です。管理画面で状態を確認してください。";

    public static Task<TranscriptionModel[]> ListTranscriptionModelsAsync(ExternalSpeechSettings settings, CancellationToken cancellation) =>
        ListTranscriptionModelsAsync(SelectConnection(settings), settings, cancellation);

    internal static async Task<TranscriptionModel[]> ListTranscriptionModelsAsync(HttpClient connection,
        ExternalSpeechSettings settings, CancellationToken cancellation)
    {
        var endpoint = settings.ValidateAddress();
        if (settings.Mode != ExternalSpeechMode.TranscriptionServer)
            throw new InvalidOperationException("TranscriptionServerの接続方式を選んでください。");
        var session = await GetTranscriptionServerSessionAsync(connection, endpoint, cancellation).ConfigureAwait(false);
        if (!session.AcceptingJobs) throw new InvalidOperationException(PreparationMessage);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/v1/models"));
        request.Headers.Add("X-Local-Token", session.Token);
        using var body = await ReadTranscriptionServerJsonAsync(connection, request, cancellation).ConfigureAwait(false);
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("TranscriptionServerのモデル一覧の応答形式を確認してください。");
        var models = new Dictionary<string, TranscriptionModel>(StringComparer.Ordinal);
        foreach (var row in data.EnumerateArray())
        {
            var id = ReadString(row, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl) ||
                id.StartsWith("diarization/", StringComparison.Ordinal)) continue;
            if (!row.TryGetProperty("x_meeting", out var metadata) || metadata.ValueKind != JsonValueKind.Object) continue;
            var task = ReadString(metadata, "task");
            if (task is not (null or "transcription")) continue;
            var availability = ReadString(metadata, "availability");
            if (metadata.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False &&
                availability != "download_on_first_use") continue;
            var label = ReadString(metadata, "label");
            var display = string.IsNullOrWhiteSpace(label) ? id : $"{label} ({id})";
            if (availability == "download_on_first_use") display += "・初回取得";
            if (availability == "provider") display += "・サーバー経由の外部API";
            models.TryAdd(id, new(id, display));
        }
        return [.. models.Values];
    }

    private static async Task<(string Token, bool AcceptingJobs)> GetTranscriptionServerSessionAsync(
        HttpClient connection, Uri endpoint, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/api/health"));
        using var body = await ReadTranscriptionServerJsonAsync(connection, request, cancellation).ConfigureAwait(false);
        var root = body.RootElement;
        var auth = ReadString(root, "authMode");
        if (ReadString(root, "appId") != "meeting-transcriber" ||
            !root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var protocol) || protocol != 3)
            throw new InvalidOperationException("TranscriptionServerの接続先とプロトコルを確認してください。");
        if (auth != "lan" && !(auth == "local" && endpoint.IsLoopback))
            throw new InvalidOperationException("TranscriptionServerのlocalまたはlan認証で接続してください。");
        var token = ReadString(root, "token");
        if (string.IsNullOrEmpty(token) || token.Length > 4096 || token.Any(c => c is < '!' or > '~'))
            throw new InvalidOperationException("TranscriptionServerの接続トークンを取得できませんでした。");
        return (token, root.TryGetProperty("acceptingJobs", out var accepting) && accepting.ValueKind == JsonValueKind.True);
    }

    private static async Task<JsonDocument> ReadTranscriptionServerJsonAsync(HttpClient connection,
        HttpRequestMessage request, CancellationToken cancellation)
    {
        try
        {
            using var response = await connection.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(StatusError((int)response.StatusCode, ExternalSpeechMode.TranscriptionServer));
            await response.Content.LoadIntoBufferAsync(1_000_000, cancellation).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false),
                cancellationToken: cancellation).ConfigureAwait(false);
        }
        catch (JsonException) { throw new InvalidOperationException("TranscriptionServerから有効なJSONを取得できませんでした。"); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new InvalidOperationException("TranscriptionServerと通信できません。起動状態とURLを確認してください。"); }
    }

    private static string? ReadString(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static async Task<string> CheckAsync(ExternalSpeechSettings settings, CancellationToken cancellation)
    {
        if (settings.Mode == ExternalSpeechMode.WindowsDebug)
            return await Task.Run(() =>
            {
                using var engine = WindowsSpeechRecognizer.CreateEngine();
                return "Windowsの日本語音声認識を使用できます";
            }, cancellation).ConfigureAwait(false);
        // 実際の録音を使わず、文字起こしAPIが指定モデルで音声を処理できることを確認する。
        var sample = RecordingDevices.ToWave(new byte[48000]);
        var result = await RecognizeAsync(sample, settings, cancellation).ConfigureAwait(false);
        if (result.Error is { Length: > 0 } error) throw new InvalidOperationException(error);
        return "認識モデル応答あり";
    }

    private static string StatusError(int status, ExternalSpeechMode mode) => status switch
    {
        401 or 403 => $"外部音声認識の接続権限を確認してください（HTTP {status}）。",
        404 when mode == ExternalSpeechMode.SpeechCpp => "speech.cppに指定した認識モデルが読み込まれていません（HTTP 404）。",
        503 when mode == ExternalSpeechMode.SpeechCpp => "speech.cppがモデルを準備しています（HTTP 503）。",
        503 when mode == ExternalSpeechMode.TranscriptionServer => "TranscriptionServerが受付できません。管理画面で準備状態を確認してください（HTTP 503）。",
        _ => $"外部音声認識に失敗しました（HTTP {status}）。"
    };
}

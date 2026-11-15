using EchoType.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EchoType.Windows;

public static class ExternalSpeechRecognizer
{
    private static readonly HttpClient Connection = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private static readonly HttpClient LoopbackConnection = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });

    public static Task<AsrResult> RecognizeAsync(byte[] wave, ExternalSpeechSettings settings, CancellationToken cancellation) =>
        RecognizeAsync(settings.Mode == ExternalSpeechMode.SpeechCpp ? LoopbackConnection : Connection, wave, settings, cancellation);

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
        catch (Exception error) when (error is HttpRequestException or IOException)
        { return new(engine, "", watch.Elapsed.TotalSeconds, "音声認識サーバーと通信できません。起動状態とURLを確認してください。"); }
    }

    public static async Task<string> CheckAsync(ExternalSpeechSettings settings, CancellationToken cancellation)
    {
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
        _ => $"外部音声認識に失敗しました（HTTP {status}）。"
    };
}

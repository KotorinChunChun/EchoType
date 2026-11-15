using EchoType.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EchoType.Windows;

public static class ExternalSpeechRecognizer
{
    private static readonly HttpClient Connection = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    public static Task<AsrResult> RecognizeAsync(byte[] wave, ExternalSpeechSettings settings, CancellationToken cancellation) =>
        RecognizeAsync(Connection, wave, settings, cancellation);

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
            if (!response.IsSuccessStatusCode) return new(engine, "", watch.Elapsed.TotalSeconds, $"外部音声認識に失敗しました（HTTP {(int)response.StatusCode}）。");
            await response.Content.LoadIntoBufferAsync(1_000_000, timeout.Token).ConfigureAwait(false);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return new(engine, "", watch.Elapsed.TotalSeconds, "外部音声認識の応答形式を確認してください。");
            return new(engine, text.GetString()!.Trim(), watch.Elapsed.TotalSeconds) { RecognitionSeconds = watch.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new(engine, "", watch.Elapsed.TotalSeconds, "外部音声認識の応答がタイムアウトしました。"); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { return new(engine, "", watch.Elapsed.TotalSeconds, "外部音声認識に失敗しました。接続と応答形式を確認してください。"); }
    }
}

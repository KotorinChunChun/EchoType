using EchoType.Core;
using EchoType.Windows;
using System.Net;
using System.Net.Http;

internal static class ExternalSpeechTests
{
    public static async Task RunAsync(string root)
    {
        var options = new ExternalSpeechSettings
        {
            Mode = ExternalSpeechMode.ApiKey, Endpoint = "https://speech.example.test/v1/audio/transcriptions",
            Model = "fixture-model", ApiKey = "fixture-key"
        };
        var settings = new VoiceInputSettings { ExternalSpeech = options, Models = ["whisper-base"], LiveEngine = "whisper-turbo" };
        using (var store = new SecureStore(new AgentPaths(Path.Combine(root, "external-speech"))))
        {
            store.SaveVoiceInputSettings(settings);
            var restored = store.LoadVoiceInputSettings();
            Require(restored.ExternalSpeech == options && restored.Models.SequenceEqual(settings.Models) &&
                restored.RecognitionModels().SequenceEqual([AsrEngines.ExternalTranscription]) && restored.WhisperWarmupModels().Length == 0,
                "外部認識時はローカル選択を保持し、使用する認識先だけを切り替える");
            store.SaveVoiceProfile(new(@"C:\test\fixture.exe", "試験", settings));
            Require(!store.GetVoiceProfiles().Single().Settings.ExternalSpeech.Enabled, "共通の接続資格情報をアプリ別設定に複製しない");
        }
        byte[] wave = [1, 2, 3];
        foreach (var mode in new[] { ExternalSpeechMode.Server, ExternalSpeechMode.ApiKey })
        {
            using var http = new HttpClient(new TestHandler(async request =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == options.Endpoint, "選択したURLへPOSTする");
                Require(request.Headers.Authorization?.Parameter == (mode == ExternalSpeechMode.ApiKey ? options.ApiKey : null), "APIキー付き方式だけで認証する");
                var form = (MultipartFormDataContent)request.Content!;
                var fields = form.ToDictionary(x => x.Headers.ContentDisposition!.Name!.Trim('"'));
                Require(fields.Count == 4 && (await fields["file"].ReadAsByteArrayAsync()).SequenceEqual(wave) &&
                    await fields["model"].ReadAsStringAsync() == options.Model, "認識に必要な音声と指定モデルだけを送る");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"合成音声の試験\"}") };
            }));
            var result = await ExternalSpeechRecognizer.RecognizeAsync(http, wave, options with { Mode = mode }, CancellationToken.None);
            Require(result.Succeeded && result.Text == "合成音声の試験", "外部の文字起こし結果を読み込む");
        }
        using var rejected = new HttpClient(new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Content = new StringContent("fixture-key") })));
        var failure = await ExternalSpeechRecognizer.RecognizeAsync(rejected, wave, options, CancellationToken.None);
        Require(!failure.Succeeded && !failure.Error.Contains(options.ApiKey, StringComparison.Ordinal), "転送を認識結果として扱わず、応答本文を表示しない");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

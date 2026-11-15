using EchoType.Core;
using EchoType.Windows;
using System.Net;
using System.Net.Http;

internal static class ExternalSpeechTests
{
    internal static async Task<int> LoopbackAsync(string endpoint)
    {
        var settings = ExternalSpeechSettings.SpeechCppDefault with { Endpoint = endpoint, Model = "fixture" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Require(await ExternalSpeechRecognizer.CheckAsync(settings, deadline.Token) == "認識モデル応答あり", "ループバックで接続テスト");
        var result = await ExternalSpeechRecognizer.RecognizeAsync(RecordingDevices.ToWave(new byte[48000]), settings, deadline.Token);
        Require(result.Succeeded && result.Text == "speech.cpp互換の試験", "ループバックのWAV送信とJSON結果取得");
        var missing = await ExternalSpeechRecognizer.RecognizeAsync(RecordingDevices.ToWave(new byte[48000]), settings with { Model = "missing" }, deadline.Token);
        Require(!missing.Succeeded && missing.Error.Contains("読み込まれていません", StringComparison.Ordinal), "ループバックでモデル未読込を通知");
        Console.WriteLine("PASS ループバックHTTPの接続テスト、文字起こし応答、モデル未読込");
        return 0;
    }

    public static async Task RunAsync(string root)
    {
        var options = new ExternalSpeechSettings
        {
            Mode = ExternalSpeechMode.ApiKey, Endpoint = "https://speech.example.test/v1/audio/transcriptions",
            Model = "fixture-model", ApiKey = "fixture-key"
        };
        var preview = ExternalSpeechSettings.SpeechCppDefault with { Endpoint = "http://127.0.0.1:8081/v1/audio/transcriptions", Model = "fixture-live" };
        var settings = new VoiceInputSettings { ExternalSpeech = options, LiveSpeech = preview, LiveDisplayEnabled = true,
            Models = ["whisper-base"], LiveEngine = "whisper-turbo" };
        using (var store = new SecureStore(new AgentPaths(Path.Combine(root, "external-speech"))))
        {
            store.SaveVoiceInputSettings(settings);
            var restored = store.LoadVoiceInputSettings();
            Require(restored.FinalSpeech() == options && restored.PreviewSpeech() == preview && restored.LiveDisplayEnabled &&
                restored.Models.SequenceEqual(settings.Models), "接続先とモデル名を用途別に保存し、旧設定も読み取れる");
            store.SaveVoiceProfile(new(@"C:\test\fixture.exe", "試験", settings));
            var profile = store.GetVoiceProfiles().Single();
            Require(!profile.Settings.ExternalSpeech.Enabled && !profile.Settings.LiveSpeech.Enabled &&
                profile.Apply(restored).PreviewSpeech() == preview, "外部接続は共通設定を使い、アプリ別に複製しない");
        }
        byte[] wave = [1, 2, 3];
        foreach (var connection in new[] { options with { Mode = ExternalSpeechMode.Server }, options, preview })
        {
            using var http = new HttpClient(new TestHandler(async request =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == connection.Endpoint, "選択したURLへPOSTする");
                Require(request.Headers.Authorization?.Parameter == (connection.Mode == ExternalSpeechMode.ApiKey ? connection.ApiKey : null), "APIキー付き方式だけで認証する");
                var form = (MultipartFormDataContent)request.Content!;
                var fields = form.ToDictionary(x => x.Headers.ContentDisposition!.Name!.Trim('"'));
                Require(fields.Count == 4 && (await fields["file"].ReadAsByteArrayAsync()).SequenceEqual(wave) &&
                    await fields["model"].ReadAsStringAsync() == connection.Model, "認識に必要な音声と指定モデルだけを送る");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"合成音声の試験\"}") };
            }));
            var result = await ExternalSpeechRecognizer.RecognizeAsync(http, wave, connection, CancellationToken.None);
            Require(result.Succeeded && result.Text == "合成音声の試験", "外部の文字起こし結果を読み込む");
        }
        using var rejected = new HttpClient(new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Content = new StringContent("fixture-key") })));
        var failure = await ExternalSpeechRecognizer.RecognizeAsync(rejected, wave, options, CancellationToken.None);
        Require(!failure.Succeeded && !failure.Error.Contains(options.ApiKey, StringComparison.Ordinal), "転送を認識結果として扱わず、応答本文を表示しない");
        using var missing = new HttpClient(new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var notLoaded = await ExternalSpeechRecognizer.RecognizeAsync(missing, wave, preview, CancellationToken.None);
        Require(!notLoaded.Succeeded && notLoaded.Error.Contains("読み込まれていません", StringComparison.Ordinal), "speech.cppの認識モデル未読込を表示");
        var refused = false;
        try { (preview with { Endpoint = options.Endpoint }).ValidateEndpoint(); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "speech.cppのローカル接続に外部ホストを使用しない");
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

using EchoType.Core;
using EchoType.Windows;
using System.Net;
using System.Net.Http;

internal static class ExternalSpeechTests
{
    internal static async Task<int> WindowsSpeechAsync(string path)
    {
        var settings = ExternalSpeechSettings.WindowsDebugDefault;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var status = await ExternalSpeechRecognizer.CheckAsync(settings, deadline.Token);
        Console.WriteLine(status);
        var result = await ExternalSpeechRecognizer.RecognizeAsync(await File.ReadAllBytesAsync(path, deadline.Token), settings, deadline.Token);
        Require(result.Succeeded && result.Text.Contains("音声認識", StringComparison.Ordinal),
            result.Error ?? "合成WAVをWindows標準の日本語認識で文字起こし");
        Console.WriteLine("PASS Windows標準の日本語認識で合成WAVを文字起こし");
        return 0;
    }
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

            var sameEndpoint = settings with { LiveSpeech = options with { Model = "other-model" } };
            var migrated = sameEndpoint.MigrateSpeechConnections();
            Require(migrated.SpeechConnections.Length == 1 && migrated.FinalSpeech() == options &&
                migrated.PreviewSpeech() == sameEndpoint.LiveSpeech, "同じ接続でも確定と即時表示で異なるモデルを保持");
            var extra = Enumerable.Range(1, 6).Select(i => new SpeechConnection(Guid.NewGuid(), $"テスト接続{i}",
                preview with { Model = "" }));
            var next = migrated with
            {
                SpeechConnections = [.. migrated.SpeechConnections, .. extra],
                PreviewSelection = new(SpeechConnection.WindowsDebug.Id, "")
            };
            store.SaveVoiceInputSettings(next);
            var restoredCatalog = store.LoadVoiceInputSettings().MigrateSpeechConnections();
            Require(restoredCatalog.SpeechConnections.SequenceEqual(next.SpeechConnections) &&
                restoredCatalog.FinalSpeech() == options && restoredCatalog.PreviewSpeech() == ExternalSpeechSettings.WindowsDebugDefault,
                "登録済み接続とWindows標準の選択を保存・復元");
            store.SaveVoiceProfile(new(@"C:\test\fixture.exe", "試験", next));
            var savedProfile = store.GetVoiceProfiles().Single().Settings;
            Require(savedProfile.SpeechConnections.Length == 0 && savedProfile.FinalSelection is null,
                "アプリ別設定へ接続資格情報を保存しない");
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
        await TranscriptionServerAsync();
    }

    private static async Task TranscriptionServerAsync()
    {
        static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
        var server = ExternalSpeechSettings.TranscriptionServerDefault;
        var tokenVersion = 0;
        using var http = new HttpClient(new TestHandler(async request =>
        {
            Require(request.RequestUri?.Authority == "127.0.0.1:8769", "録音と操作トークンの送信先を同じサーバーに限定");
            if (request.RequestUri!.AbsolutePath == "/api/health")
            {
                Require(request.Method == HttpMethod.Get && !request.Headers.Contains("X-Local-Token"), "healthは認証情報なしで照会");
                return Json($$"""{"appId":"meeting-transcriber","protocolVersion":3,"authMode":"local","acceptingJobs":true,"token":"fixture-{{++tokenVersion}}"}""");
            }
            Require(request.Headers.GetValues("X-Local-Token").Single() == $"fixture-{tokenVersion}" &&
                request.Headers.Authorization is null, "操作トークンを取得して当該リクエストにだけ付与");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v1/models")
                return Json("""
                    {"data":[
                        {"id":"whisper/base","x_meeting":{"label":"Whisper Base","task":"transcription","availability":"download_on_first_use","available":false}},
                        {"id":"haas/ja","x_meeting":{"label":"Haas","task":"transcription","availability":"installed","available":true}},
                        {"id":"diarization/diar","x_meeting":{"task":"diarization","available":true}},
                        {"id":"haas/missing","x_meeting":{"task":"transcription","available":false}},
                        {"id":"haas/ja","x_meeting":{"task":"transcription","available":true}}
                    ]}
                    """);
            Require(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/v1/audio/transcriptions", "文字起こしへPOST");
            var fields = ((MultipartFormDataContent)request.Content!).ToDictionary(x => x.Headers.ContentDisposition!.Name!.Trim('"'));
            Require(await fields["model"].ReadAsStringAsync() == "haas/ja", "サーバー一覧のモデルIDをそのまま送信");
            return Json("""{"text":"試験の文字起こし"}""");
        }));
        var models = await ExternalSpeechRecognizer.ListTranscriptionModelsAsync(http, server, CancellationToken.None);
        Require(models.Select(model => model.Id).SequenceEqual(["whisper/base", "haas/ja"]) &&
            models[0].Label.Contains("初回取得", StringComparison.Ordinal), "利用可能なASRと初回取得可の候補だけを表示");
        var chosen = server with { Model = models[1].Id };
        var result = await ExternalSpeechRecognizer.RecognizeAsync(http, [1, 2, 3], chosen, CancellationToken.None);
        Require(result.Succeeded && result.Text == "試験の文字起こし" && tokenVersion == 2, "再取得したトークンで文字起こし結果を取得");
        using var notReady = new HttpClient(new TestHandler(request =>
        {
            Require(request.RequestUri?.AbsolutePath == "/api/health", "受付準備中は録音を送らない");
            return Task.FromResult(Json("""{"appId":"meeting-transcriber","protocolVersion":3,"authMode":"local","acceptingJobs":false,"token":"fixture"}"""));
        }));
        var preparing = await ExternalSpeechRecognizer.RecognizeAsync(notReady, [1, 2, 3], chosen, CancellationToken.None);
        Require(!preparing.Succeeded && preparing.Error.Contains("受付準備中", StringComparison.Ordinal), "サーバーの準備中を認識失敗の理由に表示");
        var lan = chosen with { Endpoint = "https://transcription.example.test/v1/audio/transcriptions" };
        using var lanHttp = new HttpClient(new TestHandler(request =>
        {
            Require(request.RequestUri?.Host == "transcription.example.test", "LAN用HTTPSで同じ接続先を使う");
            if (request.RequestUri!.AbsolutePath == "/api/health")
                return Task.FromResult(Json("""{"appId":"meeting-transcriber","serverVersion":"0.1.17","protocolVersion":3,"authMode":"lan","acceptingJobs":true,"token":"lan-fixture"}"""));
            Require(request.Headers.GetValues("X-Local-Token").Single() == "lan-fixture", "LANの一時トークンを認識に使用");
            return Task.FromResult(Json("""{"text":"LANの試験"}"""));
        }));
        Require((await ExternalSpeechRecognizer.RecognizeAsync(lanHttp, [1, 2, 3], lan, CancellationToken.None)).Text == "LANの試験",
            "既存のHTTPS制約でLAN認証を使える");
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

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args is ["--voice-focus-native-fixture"]) return VoiceInputNativeFocusDiagnostics.Fixture();
if (args is ["--isolated", var mode]) return IsolatedDesktopDiagnostics.Run(mode);
if (args is ["--isolated-app", var app]) return IsolatedDesktopDiagnostics.Run("app", app);
if (args is ["--isolated-app-legacy-settings", var legacyApp]) return IsolatedDesktopDiagnostics.Run("app-legacy", legacyApp);
if (args is ["--queue-child"]) return VoiceInputQueueDiagnostics.Run();
if (args is ["--focus-child"]) return VoiceInputMonitorDiagnostics.Run(20);
if (args is ["--focus-interactive"]) return VoiceInputMonitorDiagnostics.Run(1, interactive: true);
if (args is ["--speech-loopback", var endpoint]) return await ExternalSpeechTests.LoopbackAsync(endpoint);
if (args.Length != 0) return 2;

var root = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
var tests = new List<(string Name, Func<Task> Test)>();
VoiceInputTests.Add(tests, root);
tests.Add(("外部認識の選択・保存と要求形式、APIキーと失敗メッセージを確認", () => ExternalSpeechTests.RunAsync(root)));
MigrationTests.Add(tests, root);
var failures = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("成功: " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("失敗: " + name + " — " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count}件成功");
return failures == 0 ? 0 : 1;

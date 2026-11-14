using EchoType.App;
using EchoType.Core;
using EchoType.Windows;
using System.Reflection;
using System.Text.Json;
using System.Windows;

internal static class VoiceInputMonitorDiagnostics
{
    internal static int Run(int seconds, bool interactive = false)
    {
        var result = 1;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/EchoType.App;component/AppStyles.xaml", UriKind.Relative) });
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await VoiceInputNativeFocusDiagnostics.VerifyAsync(app, seconds, interactive); await VerifyAsync(app); result = 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { app.Shutdown(); }
            });
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
    internal static async Task VerifyAsync(Application app, int seconds = 2)
    {
        using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "focus-monitor-" + Guid.NewGuid().ToString("N"))));
        store.SaveVoiceInputSettings(new() { Models = ["windows"], Enabled = false });
        var reads = 0; var captures = 0; var focus = 242; var failCapture = false;
        using var release = new ManualResetEventSlim();
        var controller = new VoiceInputController(store.Paths, store, app.Dispatcher, true, true,
            keyState: _ => false,
            readTargetFocus: () => { reads++; return new(new nint(100), new nint(focus), default) { ProcessId = 123, ThreadId = 456 }; },
            captureOpenTarget: _ =>
            {
                Interlocked.Increment(ref captures); release.Wait();
                if (failCapture) throw new InvalidOperationException("記録してはいけない入力文字列");
                // 不採用分岐に入れば、対象のプロパティ・UIAには一切触れないはず。
                return (VoiceInputTarget)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VoiceInputTarget));
            });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var track = typeof(VoiceInputController).GetMethod("TrackTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var next in new[] { 240, 241, 242 }) { focus = next; track.Invoke(controller, null); }
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Require(reads >= 4 && captures == 0, $"待機{watch.Elapsed.TotalSeconds:F2}秒・監視{reads}回でUIA取得0回");
            controller.Open();
            for (var i = 0; i < 100 && captures == 0; i++) await Task.Delay(20);
            Require(captures == 1, "明示表示時だけ入力先取得を1回要求");
            controller.Dismiss(); release.Set(); await Task.Delay(150);
            var target = typeof(VoiceInputController).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller);
            Require(target is null && !app.Windows.OfType<VoiceInputWindow>().Single().IsVisible, "閉じた後の取得結果で入力先・画面を復活させない");
            failCapture = true; controller.Open(); await Task.Delay(150); controller.Dismiss();
            Require(captures == 2, "次の明示表示で新しい取得要求を開始");
            var logs = Directory.GetFiles(Path.Combine(store.Paths.Root, "data", "diagnostics", "voice-input-focus"), "*.jsonl");
            var lines = logs.SelectMany(File.ReadAllLines).ToArray();
            Require(lines.Length > 0 && !lines.Any(l => l.Contains("記録してはいけない")), "フォーカス診断に入力文字列・例外本文を保存しない");
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                Require(root.TryGetProperty("ThreadId", out _) && root.TryGetProperty("TargetPid", out _) && root.TryGetProperty("Root", out _) && root.TryGetProperty("ForegroundPid", out _), "要求元・対象・前景の診断項目");
            }
        }
        finally { release.Set(); await controller.StopAsync(); }
    }
    private static void Require(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS " + text); }
}

using EchoType.App;
using EchoType.Core;
using EchoType.Windows;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;

internal static class VoiceInputQueueDiagnostics
{
    public static int Run(string? serverEndpoint = null, string? model = null, string? wave = null)
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var appAssembly = typeof(VoiceInputController).Assembly.GetName().Name;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{appAssembly};component/AppStyles.xaml", UriKind.Relative) });
            app.Dispatcher.BeginInvoke(async () =>
            {
                VoiceInputController? controller = null;
                var keys = new ConcurrentDictionary<int, bool>();
                var gates = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                try
                {
                    if (serverEndpoint is not null)
                    {
                        await VerifyServerAsync(app, serverEndpoint, model!, wave!);
                        code = 0;
                        return;
                    }
                    using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "voice-queue-" + Guid.NewGuid().ToString("N"))));
                    var final = ExternalSpeechSettings.SpeechCppDefault with { Model = "fixture-final" };
                    var preview = final with { Endpoint = "http://127.0.0.1:8081/v1/audio/transcriptions", Model = "fixture-live" };
                    store.SaveVoiceInputSettings(new() { ExternalSpeech = final, LiveSpeech = preview });
                    var inputs = new List<Input>();
                    var calls = new ConcurrentDictionary<int, int>();
                    var liveStarts = 0;
                    var idlePolls = 0;
                    var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    controller = new(store.Paths, store, app.Dispatcher, false, true,
                        openInput: _ => { var input = new Input(); inputs.Add(input); return input; },
                        keyState: key =>
                        {
                            // 起動時に押しっぱなしのキーは受け付けない。一巡目の解放を監視した後に押す。
                            if (key == 0xFE && Interlocked.Increment(ref idlePolls) == 2) armed.TrySetResult();
                            return keys.GetValueOrDefault(key);
                        },
                        liveFactory: () => { liveStarts++; return new Live(); }, recognizer: async (engine, segment, _, ct) =>
                        {
                            Require(engine == AsrEngines.ExternalTranscription, "確定モデルの経路だけを使用");
                            using var reader = new WaveFileReader(new MemoryStream(segment.Wave));
                            var sample = new byte[2]; reader.ReadExactly(sample);
                            var number = sample[0];
                            var count = calls.AddOrUpdate(number, 1, (_, n) => n + 1);
                            await gates[number].Task.WaitAsync(ct);
                            return number == 3 && count == 1 ? new(engine, "", .1, "検証用の認識失敗") : new(engine, $"確定{number}", .1);
                        });
                    controller.Status += message => Console.WriteLine(message);
                    await armed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    async Task Record(int number, bool previewVisible)
                    {
                        keys[0xA2] = keys[0xA0] = true;
                        var window = app.Windows.OfType<VoiceInputWindow>().SingleOrDefault();
                        await Until(() => (window = app.Windows.OfType<VoiceInputWindow>().SingleOrDefault())?.IsVisible == true &&
                            ((FrameworkElement)window.FindName("RecordingTint")).Visibility == Visibility.Visible);
                        inputs.Last().Feed(Enumerable.Repeat((byte)number, 48000).ToArray());
                        await Task.Delay(125);
                        var live = (FrameworkElement)window!.FindName("LiveGroup");
                        Require(live.IsVisible == previewVisible, "即時表示の設定が録音中にも適用される");
                        if (previewVisible)
                        {
                            var label = (TextBlock)window.FindName("LiveLabel");
                            var text = (TextBox)window.FindName("LiveText");
                            Require(label.Text.Contains("fixture-live") && text.Text == $"途中{number}", "即時表示には独立したモデル名と途中の文字が出る");
                        }
                        await Task.Delay(950);
                        keys[0xA2] = keys[0xA0] = false;
                        await Until(() => ((FrameworkElement)window.FindName("RecordingTint")).Visibility == Visibility.Collapsed);
                    }
                    await Record(1, false);
                    var voiceWindow = app.Windows.OfType<VoiceInputWindow>().Single();
                    var editor = (TextBox)voiceWindow.FindName("Editor");
                    var status = (TextBlock)voiceWindow.FindName("RecognitionText");
                    var grid = (DataGrid)voiceWindow.FindName("QueueLog");
                    Require(liveStarts == 0 && voiceWindow.FindName("Cards") is null, "既定では待機画面から録音を開始し、即時認識を起動せず候補欄も表示しない");
                    await Until(() => calls.GetValueOrDefault(1) == 1);
                    await Record(2, false);
                    await Until(() => calls.GetValueOrDefault(2) == 1);
                    gates[2].SetResult();
                    await Until(() => grid.Items.Cast<VoiceInputLogEntry>().Any(e => e.Number == 2 && e.Finished));
                    Require(editor.Text.Length == 0, "後続の認識が先に終わっても録音順を保持");
                    gates[1].SetResult();
                    await Until(() => editor.Text == "確定1確定2");
                    Require(status.Text.Contains("fixture-final"), "確定結果と確定モデル名を表示");
                    controller.SaveSettings(controller.Settings with { LiveDisplayEnabled = true });
                    voiceWindow.Close();
                    Require(!voiceWindow.IsVisible, "閉じる操作で格納");
                    controller.Open();
                    Require(editor.Text == "確定1確定2", "格納から再表示しても編集内容を保持");
                    await Record(3, true);
                    gates[3].SetResult();
                    await Until(() => status.Text.Contains("検証用の認識失敗"));
                    var failed = grid.Items.Cast<VoiceInputLogEntry>().Last();
                    Require(failed.Details.Contains("検証用の認識失敗"), "認識エラーの理由を画面と録音キューに表示");
                    editor.Text = "途中\r\n最終行";
                    editor.Select(2, 0);
                    await controller.RetryRecognitionAsync(failed);
                    Require(editor.Text == "途中確定3\r\n最終行" && editor.CaretIndex == "途中確定3".Length &&
                        calls[3] == 2 && liveStarts == 1, "再試行した認識結果を途中に入れ、カーソルは挿入直後に残す");
                    var liveText = (TextBox)voiceWindow.FindName("LiveText");
                    var liveLabel = (TextBlock)voiceWindow.FindName("LiveLabel");
                    voiceWindow.ShowLive("更新前");
                    await Task.Delay(3000);
                    voiceWindow.ShowLive("更新後");
                    await Task.Delay(2300);
                    Require(liveText.Text == "更新後", "即時表示の文字が変わると表示時間を数え直す");
                    await Until(() => liveText.Text.Length == 0);
                    voiceWindow.ShowLive("更新後");
                    Require(liveText.Text.Length == 0 && liveLabel.Text.Contains("fixture-live") &&
                        editor.Text == "途中確定3\r\n最終行" && failed.LiveText == "途中3",
                        "5秒で即時表示の文字だけを消し、同じ結果は再表示しない");
                    voiceWindow.ResetLive();
                    voiceWindow.ShowLive("更新後");
                    Require(liveText.Text == "更新後", "新しい録音では同じ認識結果も表示する");
                    await Record(4, true);
                    await Until(() => calls.GetValueOrDefault(4) == 1);
                    var files = grid.Items.Cast<VoiceInputLogEntry>().Select(e => e.CachePath).ToArray();
                    controller.Dismiss();
                    Require(files.All(f => !File.Exists(f)), "キャンセルで一時音声を消去");
                    controller.Open(); gates[4].SetResult();
                    await Task.Delay(250);
                    Require(grid.Items.Count == 0 && editor.Text == "", "中止した認識の遅延結果を次回に混ぜない");
                    await controller.StopAsync(); controller = null;
                    await CheckTemporaryPinAsync(app);
                    code = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    keys.Clear(); foreach (var gate in gates) gate.TrySetResult();
                    if (controller is not null) await controller.StopAsync(); app.Shutdown();
                }
            }); app.Run();
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return code;
    }
    private static async Task CheckTemporaryPinAsync(Application app)
    {
        using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "voice-pin-" + Guid.NewGuid().ToString("N"))));
        store.SaveVoiceInputSettings(new() { AutomaticInput = true, AutomaticFormatting = true, AutomaticConfirm = true });
        var formats = 0;
        TaskCompletionSource<string>? formattingGate = null;
        var controller = new VoiceInputController(store.Paths, store, app.Dispatcher, true, true,
            formatter: (text, _, ct) => { formats++; return formattingGate?.Task.WaitAsync(ct) ?? Task.FromResult("整形:" + text); });
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            controller.Open();
            var window = app.Windows.OfType<VoiceInputWindow>().Single(w => w.IsVisible);
            void Message(int message, nint hit = 0) => typeof(VoiceInputWindow).GetMethod("HandleUserMoveMessage", flags)!.Invoke(window, [message, hit]);
            window.Left += 5; Message(0x216);
            Require(!controller.TemporarilyPinned, "自動配置・マウス開始のない移動ではピン留めしない");
            Message(0xA1, 2); Message(0x232);
            Require(!controller.TemporarilyPinned, "タイトルバーのクリックだけではピン留めしない");
            Message(0xA1, 2); Message(0x216); Message(0x232);
            Require(controller.TemporarilyPinned && controller.Settings.AutomaticConfirm && store.LoadVoiceInputSettings().AutomaticConfirm,
                "マウス移動で一時ピン留めし設定の自動決定は保存変更しない");
            var confirm = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("AutomaticConfirmToggle");
            Require(confirm.IsChecked == false && !confirm.IsEnabled, "現在画面だけ自動決定を解除");
            void CloseEmpty() => typeof(VoiceInputController).GetMethod("MaybeCloseEmpty", flags)!.Invoke(controller,
                [typeof(VoiceInputController).GetField("_session", flags)!.GetValue(controller)]);
            CloseEmpty();
            Require(window.IsVisible && window.DraftText.Length == 0, "一時ピン留め中は無音・空本文でも自動で閉じない");
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("AutomaticFormattingToggle")).IsChecked = false;
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("AutomaticFormattingToggle")).IsChecked = true;
            Require(store.LoadVoiceInputSettings().AutomaticConfirm, "ピン留め中の別設定操作でも一時解除を保存しない");
            window.DraftText = "試験本文";
            typeof(VoiceInputController).GetField("_automaticSession", flags)!.SetValue(controller, typeof(VoiceInputController).GetField("_session", flags)!.GetValue(controller));
            typeof(VoiceInputController).GetField("_automaticDraftRequested", flags)!.SetValue(controller, true);
            typeof(VoiceInputController).GetMethod("TryAutomaticInput", flags)!.Invoke(controller, null);
            await Until(() => formats == 1 && window.DraftText == "整形:試験本文");
            Require(window.IsVisible && controller.TemporarilyPinned, "ピン留め中も自動整形し本文を保持して決定待ちにする");
            var simple = (Button)window.FindName("SimpleFormatButton");
            var prompt = (Button)window.FindName("FormatButton");
            foreach (var button in new[] { simple, prompt })
            {
                var label = (string)button.Content;
                var other = button == simple ? prompt : simple;
                var otherLabel = other.Content;
                var width = button.ActualWidth;
                formattingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require((string)button.Content == label + "…" && Equals(other.Content, otherLabel) &&
                    !button.IsEnabled && button.ActualWidth == width, "押した整形ボタンだけ処理中表示にする");
                formattingGate.SetResult(window.DraftText);
                await Until(() => Equals(button.Content, label) && button.IsEnabled);
            }
            controller.Dismiss();
            Require(!window.IsVisible, "一時ピン留め中でも明示的に閉じられる");
            controller.Open();
            Require(!controller.TemporarilyPinned && confirm.IsChecked == true && confirm.IsEnabled && store.LoadVoiceInputSettings().AutomaticConfirm,
                "閉じて再表示すると一時ピン留めを解除し保存された自動決定に戻る");
            window.DraftText = ""; CloseEmpty();
            Require(!window.IsVisible, "再表示後の無音・空本文は通常どおり自動で閉じる");
            controller.Open();
            // 実ウィンドウ・UIA参照を持たない対象。書込み経路へ入ると失敗するため、非同期後のガードを検出できる。
            var target = (VoiceInputTarget)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VoiceInputTarget));
            typeof(VoiceInputController).GetField("_target", flags)!.SetValue(controller, target);
            window.DraftText = "整形待ち本文";
            var formatsBefore = formats;
            formattingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(VoiceInputController).GetField("_automaticSession", flags)!.SetValue(controller, typeof(VoiceInputController).GetField("_session", flags)!.GetValue(controller));
            typeof(VoiceInputController).GetField("_automaticDraftRequested", flags)!.SetValue(controller, true);
            typeof(VoiceInputController).GetMethod("TryAutomaticInput", flags)!.Invoke(controller, null);
            Require(formats == formatsBefore + 1, "自動決定オンで非同期整形を開始");
            Message(0xA1, 2); Message(0x216); Message(0x232);
            formattingGate.SetResult("整形:整形待ち本文");
            await Until(() => !(bool)typeof(VoiceInputController).GetField("_automaticProcessing", flags)!.GetValue(controller)!);
            Require(window.IsVisible && window.DraftText == "整形:整形待ち本文" && !target.Consumed &&
                !(bool)typeof(VoiceInputController).GetField("_automaticFaulted", flags)!.GetValue(controller)!,
                "整形開始後のマウス移動でも書込みせず決定待ちにする");
        }
        finally { await controller.StopAsync(); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 120 && !predicate(); i++) await Task.Delay(50);
        Require(predicate(), "期限内に処理完了");
    }
    private static async Task VerifyServerAsync(Application app, string endpoint, string model, string file)
    {
        using var audio = new WaveFileReader(file);
        if (audio.WaveFormat.Encoding != WaveFormatEncoding.Pcm || audio.WaveFormat.SampleRate != 24000 ||
            audio.WaveFormat.BitsPerSample != 16 || audio.WaveFormat.Channels != 1 || audio.TotalTime.TotalSeconds is < 1 or > 10)
            throw new ArgumentException("試験用WAVは1〜10秒の24kHz・16bitモノラルPCMを指定してください。");
        var pcm = new byte[checked((int)audio.Length)];
        audio.ReadExactly(pcm);
        using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "server-voice-" + Guid.NewGuid().ToString("N"))));
        store.SaveVoiceInputSettings(new()
        {
            ExternalSpeech = new() { Mode = ExternalSpeechMode.TranscriptionServer, Endpoint = endpoint, Model = model }
        });
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        var held = 0;
        Input? input = null;
        var controller = new VoiceInputController(store.Paths, store, app.Dispatcher, false, true,
            openInput: _ => input = new Input(), keyState: key =>
            {
                if (key == 0xFE && Interlocked.Increment(ref polls) == 2) armed.TrySetResult();
                return (key is 0xA2 or 0xA0) && Volatile.Read(ref held) == 1;
            });
        try
        {
            await armed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref held, 1);
            await Until(() => input is not null && app.Windows.OfType<VoiceInputWindow>().Any(w => w.IsVisible));
            input!.Feed(pcm);
            await Task.Delay(1100);
            Volatile.Write(ref held, 0);
            var window = app.Windows.OfType<VoiceInputWindow>().Single();
            var grid = (DataGrid)window.FindName("QueueLog");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            VoiceInputLogEntry? entry = null;
            while ((entry = grid.Items.Cast<VoiceInputLogEntry>().SingleOrDefault()) is not { Finished: true })
                await Task.Delay(50, deadline.Token);
            var result = entry.Results.Single();
            if (result.Error is { Length: > 0 } error)
            {
                var status = (TextBlock)window.FindName("RecognitionText");
                Require(status.Text.Contains(error, StringComparison.Ordinal), "確定欄に失敗理由を表示");
                throw new InvalidOperationException(error);
            }
            Require(!string.IsNullOrWhiteSpace(result.Text), "サーバーの確定応答を取得");
            var editor = (TextBox)window.FindName("Editor");
            Require(editor.Text == controller.Settings.CompleteCandidate(result.Text), "TranscriptionServerの確定結果がEchoTypeの送信予定欄に反映された");
        }
        finally { await controller.StopAsync(); }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class Input : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(24000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Feed(byte[] bytes) => DataAvailable?.Invoke(this, new(bytes, bytes.Length));
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new());
        public void Dispose() { }
    }
    private sealed class Live : IVoiceInputLive
    {
        private string _text = "";
        public event Action<string>? Updated;
        public void Feed(byte[] pcm) { _text = pcm[0] == 0 ? "" : $"途中{pcm[0]}"; Updated?.Invoke(_text); }
        public Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct) => Task.FromResult(new VoiceInputLiveResult(_text));
        public void Dispose() { }
    }
}

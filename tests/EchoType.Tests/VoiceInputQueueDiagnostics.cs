using EchoType.App;
using EchoType.Core;
using EchoType.Windows;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;

internal static class VoiceInputQueueDiagnostics
{
    public static int Run()
    {
        var code = 1;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/EchoType.App;component/AppStyles.xaml", UriKind.Relative) });
            app.Dispatcher.BeginInvoke(async () =>
            {
                VoiceInputController? controller = null;
                var keys = new ConcurrentDictionary<int, bool>();
                var gates = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                var slowGates = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                try
                {
                    using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "voice-queue-" + Guid.NewGuid().ToString("N"))));
                    store.SaveVoiceInputSettings(new() { Models = ["windows", "whisper-turbo"] });
                    var canceled = new ConcurrentDictionary<string, bool>();
                    var inputs = new List<Input>(); var calls = new ConcurrentDictionary<int, int>();
                    controller = new(store.Paths, store, app.Dispatcher, false, true,
                        openInput: _ => { var input = new Input(); inputs.Add(input); return input; }, keyState: k => keys.GetValueOrDefault(k),
                        liveFactory: () => new Live(), recognizer: async (engine, segment, settings, ct) =>
                        {
                            using var reader = new WaveFileReader(new MemoryStream(segment.Wave)); var sample = new byte[2]; reader.ReadExactly(sample); var number = sample[0];
                            calls.AddOrUpdate(number, 1, (_, n) => n + 1);
                            using var registration = ct.Register(() => canceled[engine + number] = true);
                            await (engine == "windows" ? gates[number] : slowGates[number]).Task; // 中止に遅れて返るモデルも模擬する。
                            if (number == 6 && engine == "windows" && calls[number] <= 2) return new(engine, "", .1, "検証用の認識失敗");
                            return new(engine, number == 0 ? "" : $"{engine}:{number}", .1);
                        });
                    controller.Open(); var window = app.Windows.OfType<VoiceInputWindow>().Single(); window.Height = 900;
                    var editor = (TextBox)window.FindName("Editor"); editor.Text = "前後"; editor.CaretIndex = 1;
                    var grid = (DataGrid)window.FindName("QueueLog");
                    await Task.Delay(100);
                    ((Expander)window.FindName("QueuePanel")).IsExpanded = true; window.UpdateLayout();
                    Require(grid.ActualHeight > 100 && ((ScrollViewer)window.FindName("ComparisonViewport")).ViewportHeight > 150 &&
                        editor.TranslatePoint(new Point(0, editor.ActualHeight), window).Y < window.ActualHeight - 50,
                        "ログ展開時も比較のスクロール領域と下の編集欄が画面内に表示");
                    async Task Record(int number)
                    {
                        keys[0x5B] = true; keys[0xA2] = true; keys[0xA0] = true; await Task.Delay(150);
                        Require(((FrameworkElement)window.FindName("RecordingTint")).Visibility == Visibility.Visible, "録音中は外周余白に薄赤表示");
                        var tint = (Border)window.FindName("RecordingTint");
                        Require(((System.Windows.Media.SolidColorBrush)window.Background).Color == System.Windows.Media.Color.FromRgb(245, 248, 248) && tint.Background is null && tint.BorderThickness == new Thickness(10) && !tint.IsHitTestVisible, "赤色は外周10pxだけで内容を覆わない");
                        var input = inputs.Last(); input.Feed(Enumerable.Repeat((byte)number, 48000).ToArray());
                        await Task.Delay(100);
                        Require(((TextBox)window.FindName("LiveText")).Text == (number == 0 ? "" : $"途中{number}"), "押下中のPCMがWindows途中表示へ反映");
                        await Task.Delay(850); keys[0xA0] = false; keys[0xA2] = false; keys[0x5B] = false; await Task.Delay(150);
                        Require(((FrameworkElement)window.FindName("RecordingTint")).Visibility == Visibility.Collapsed, "録音終了で薄赤表示を解除");
                    }
                    await Record(1); await Until(() => calls.GetValueOrDefault(1) == 2);
                    Require(window.PreviewCards.Take(2).All(c => c.IsProcessing), "未完了モデルだけ処理中");
                    window.UpdateLayout();
                    var spinner = Descendants(window).OfType<Border>().First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "VoiceProcessingSpinner");
                    Require(spinner.IsVisible && spinner.HorizontalAlignment == HorizontalAlignment.Center && spinner.VerticalAlignment == VerticalAlignment.Center, "実WPFの候補欄中央に回転表示");
                    var rotation = (System.Windows.Media.RotateTransform)((System.Windows.Shapes.Path)spinner.Child).RenderTransform; var angle = rotation.Angle; await Task.Delay(150);
                    Require(rotation.HasAnimatedProperties && rotation.Angle != angle, "回転アニメーションが進行");
                    await Record(2); await Until(() => calls.GetValueOrDefault(2) == 2);
                    Require(calls[1] == 2 && calls[2] == 2, "先行認識の完了前に追加録音と並行認識を受付");
                    var files = grid.Items.Cast<VoiceInputLogEntry>().Select(e => e.CachePath).ToArray();
                    Require(files.Length == 2 && files.All(File.Exists), "認識中の各録音を一時ファイルに保持");
                    await Record(3);
                    Require(calls.GetValueOrDefault(3) == 0 && grid.Items.Cast<VoiceInputLogEntry>().Last().CanReplay, "2件の並行認識中も3件目を録音しキャッシュで待機");
                    gates[2].SetResult(); await Until(() => window.PreviewCards[0].Text == "windows:2");
                    Require(window.PreviewCards[1].Text == "" && window.PreviewCards[0].Summary.Contains("3件中 1件完了"), "先行録音・重いモデルを待たず完了分と件数を表示");
                    await Until(() => calls.GetValueOrDefault(3) == 1);
                    Require(calls[3] == 1, "重いモデル2件が処理中でも速いモデルは3件目を開始");
                    gates[1].SetResult(); await Until(() => window.PreviewCards[0].Text.Contains("windows:1"));
                    Require(window.PreviewCards[0].Text == "windows:1windows:2", "追加録音を改行なしで録音順に連結");
                    gates[3].SetResult(); await Until(() => !window.PreviewCards[0].IsProcessing);
                    Require(window.PreviewCards[1].IsProcessing && !spinner.IsVisible, "速いモデル全件完了で回転解除、遅いモデルは継続");
                    var adopted = window.PreviewCards[0].Text; window.Append(window.PreviewCards[0]);
                    Require(editor.Text == "前" + adopted + "。後" && window.PreviewCards.All(c => c.Text == ""), "追記は編集カーソルに挿入して全比較欄をクリア");
                    Require(window.PreviewCards.All(c => c.Seconds == 0) && grid.Items.Count == 3 && files.All(File.Exists), "追記で完了累計だけ消去し未完了キューと音声を保持");
                    Require(((FrameworkElement)window.FindName("CaretMarker")).Visibility == Visibility.Visible, "フォーカス外でも挿入位置を表示");
                    Require(canceled.IsEmpty && window.PreviewCards[1].IsProcessing, "追記は未完了モデルを中止せず回転継続");
                    slowGates[1].SetResult(); slowGates[2].SetResult(); slowGates[3].SetResult(); await Task.Delay(300);
                    await Until(() => !window.PreviewCards[1].IsProcessing);
                    Require(window.PreviewCards[0].Text == "" && window.PreviewCards[1].Text == "whisper-turbo:1whisper-turbo:2whisper-turbo:3" && calls.GetValueOrDefault(3) == 2, "追記時に未完了だった処理と待機結果を欠落なく表示");
                    await Record(5); await Until(() => calls.GetValueOrDefault(5) == 2);
                    var beforeDelete = editor.Text; files = grid.Items.Cast<VoiceInputLogEntry>().Select(e => e.CachePath).ToArray();
                    controller.ClearCandidates(); Require(canceled.IsEmpty && window.PreviewCards.Take(2).All(c => c.IsProcessing), "削除も未完了モデルを保持"); gates[5].SetResult(); slowGates[5].SetResult(); await Task.Delay(250);
                    Require(grid.Items.Count == 1 && editor.Text == beforeDelete && window.PreviewCards.Take(2).All(c => c.Text.EndsWith(":5")), "削除前の未完了結果が後から表示され送信本文も保持");
                    controller.ClearCandidates(); Require(grid.Items.Count == 0 && window.PreviewCards.All(c => c.Text == "" && c.Seconds == 0), "全件完了後の削除は候補と累計とログを消去");
                    var liveBefore = ((TextBox)window.FindName("LiveText")).Text;
                    inputs.Last().Feed(Enumerable.Repeat((byte)7, 48000).ToArray()); await Task.Delay(100);
                    Require(((TextBox)window.FindName("LiveText")).Text == liveBefore, "キー解放後のモニター音声は文字起こししない");
                    await Record(6); await Until(() => calls.GetValueOrDefault(6) == 2);
                    gates[6].SetResult(); slowGates[6].SetResult();
                    await Until(() => grid.Items.Cast<VoiceInputLogEntry>().Last().Finished);
                    var failedEntry = grid.Items.Cast<VoiceInputLogEntry>().Last();
                    await controller.RetryRecognitionAsync(failedEntry);
                    Require(calls[6] == 3 && grid.Items.Cast<VoiceInputLogEntry>().Last().Results.All(r => r.Succeeded), "失敗したモデルだけを元音声で再試行");
                    Require(failedEntry.Details.Contains("検証用の認識失敗") && controller.Timings.Any(t => t.Result.Contains("検証用の認識失敗")), "元の失敗理由と処理時間を保持");
                    controller.ClearCandidates();
                    await Record(4); await Until(() => calls.GetValueOrDefault(4) == 2);
                    files = grid.Items.Cast<VoiceInputLogEntry>().Select(e => e.CachePath).ToArray();
                    controller.Dismiss(); Require(files.All(f => !File.Exists(f)), "ウィンドウを閉じると処理中を含む音声キャッシュを削除");
                    controller.Open(); gates[4].SetResult(); slowGates[4].SetResult(); await Task.Delay(250);
                    Require(grid.Items.Count == 0 && window.PreviewCards.All(c => c.Text == ""), "閉じたセッションの遅延結果を次回に混ぜない");
                    editor.Text = ""; gates[0].SetResult(); slowGates[0].SetResult(); await Record(0); await Until(() => !window.IsVisible);
                    Require(!window.IsVisible, "有効文字のない録音だけなら自動的に閉じる");
                    await controller.StopAsync(); controller = null;
                    await CheckAutomaticRetryAsync(app);
                    code = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    keys.Clear(); foreach (var gate in gates.Concat(slowGates)) gate.TrySetResult();
                    if (controller is not null) await controller.StopAsync(); app.Shutdown();
                }
            }); app.Run();
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return code;
    }
    private static async Task CheckAutomaticRetryAsync(Application app)
    {
        await CheckTemporaryPinAsync(app);
        using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "voice-queue-retry-" + Guid.NewGuid().ToString("N"))));
        store.SaveVoiceInputSettings(new() { Models = ["windows", "whisper-turbo"], LiveEngine = "windows",
            AutomaticInput = true, AutomaticFormatting = false, AutomaticConfirm = false });
        var inputs = new List<Input>();
        var gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var attempts = new ConcurrentDictionary<(int Number, string Engine), int>();
        var calls = new ConcurrentQueue<(int Number, string Engine, int Attempt)>();
        var formats = 0; var liveStarts = 0;
        var controller = new VoiceInputController(store.Paths, store, app.Dispatcher, false, false,
            formatter: (text, _, _) => { formats++; return Task.FromResult("整形:" + text); },
            openInput: _ => { var input = new Input(); inputs.Add(input); return input; },
            liveFactory: () => { liveStarts++; return new Live(); },
            recognizer: async (engine, segment, _, ct) =>
            {
                using var reader = new WaveFileReader(new MemoryStream(segment.Wave));
                var sample = new byte[2]; reader.ReadExactly(sample); var number = (int)sample[0];
                var attempt = attempts.AddOrUpdate((number, engine), 1, (_, count) => count + 1);
                calls.Enqueue((number, engine, attempt));
                if (engine != "windows") return new(engine, "未使用モデルの結果", .1);
                if (number == 1)
                {
                    await gates[Math.Min(attempt, 3)].Task.WaitAsync(ct);
                    if (attempt < 3) return new(engine, "", .1, $"先行録音の試行{attempt}失敗");
                }
                return new(engine, number == 1 ? "先行" : "後続", .1);
            });
        try
        {
            controller.Open();
            var window = app.Windows.OfType<VoiceInputWindow>().Single();
            var grid = (DataGrid)window.FindName("QueueLog");
            void Gesture(VoiceInputGesture.Change change) => typeof(VoiceInputController)
                .GetMethod("OnGesture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(controller, [change, default(VoiceInputTarget.FocusStamp)]);
            async Task Record(int number)
            {
                Gesture(VoiceInputGesture.Change.Started);
                inputs.Last().Feed(Enumerable.Repeat((byte)number, 48000).ToArray());
                await Task.Delay(1100); Gesture(VoiceInputGesture.Change.Accepted);
                await Until(() => calls.Any(call => call.Number == number));
            }

            await Record(1); gates[1].SetResult();
            await Until(() => grid.Items.Cast<VoiceInputLogEntry>().First().Finished);
            var original = grid.Items.Cast<VoiceInputLogEntry>().First();
            Require(original.Results.Single().Error.Contains("試行1失敗"), "元録音の失敗を確定して再試行元として保持");
            await Record(2);
            await Until(() => grid.Items.Cast<VoiceInputLogEntry>().Last().Finished);
            Require(window.DraftText.Length == 0 && grid.Items.Count == 2,
                "先行録音が失敗中は成功した後続録音だけを先に採用しない");

            // 元行を選択したまま再試行する。設定切替による再構築も元の録音順を保つ。
            grid.SelectedItem = original;
            var firstRetry = controller.RetryRecognitionAsync(original);
            await Until(() => attempts.GetValueOrDefault((1, "windows")) == 2);
            var retryEntry = grid.Items.Cast<VoiceInputLogEntry>().Last();
            Require(retryEntry.RecordingNumber == original.Number && retryEntry.RetrySourceNumber == original.Number,
                "再試行行は元録音の識別子を引き継ぐ");
            controller.SetAutomaticOptions(true, false);
            Require(window.DraftText.Length == 0 && formats == 0,
                "再試行待ちの自動整形切替で後続録音を先に採用・整形しない");
            controller.SetAutomaticOptions(false, false);
            controller.SetAutomaticInput(false); controller.SetAutomaticInput(true);
            Require(window.DraftText.Length == 0 && formats == 0,
                "再試行待ちの自動採用オンオフでも録音順を再構築して待機");
            gates[2].SetResult(); await firstRetry.WaitAsync(TimeSpan.FromSeconds(6));
            Require(retryEntry.Finished && retryEntry.Results.Single().Error.Contains("試行2失敗") && window.DraftText.Length == 0,
                "再試行も失敗した場合は先行・後続の本文を未採用のまま保持");

            // 最新の失敗行に選び直さなくても、元行から同系列の試行を差し替える。
            var secondRetry = controller.RetryRecognitionAsync(original);
            await Until(() => attempts.GetValueOrDefault((1, "windows")) == 3);
            var successfulRetry = grid.Items.Cast<VoiceInputLogEntry>().Last();
            Require(successfulRetry.RecordingNumber == original.Number && window.DraftText.Length == 0,
                "元の失敗行からの再々試行も同じ録音系列として扱う");
            gates[3].SetResult(); await secondRetry.WaitAsync(TimeSpan.FromSeconds(6));
            await Until(() => window.DraftText == "先行後続。");
            Require(window.PreviewCards.All(card => card.Text.Length == 0 && !card.IsProcessing) && formats == 0,
                "最新の成功結果と後続結果を録音順に一度だけ採用し候補を消費");
            Require(calls.ToArray().SequenceEqual(new[] { (1, "windows", 1), (2, "windows", 1), (1, "windows", 2), (1, "windows", 3) }) && liveStarts == 0,
                "元録音・再試行とも先頭モデルだけを必要回数実行し副モデルと途中認識を起動しない");

            var callsBefore = calls.Count; var rowsBefore = grid.Items.Count;
            await controller.RetryRecognitionAsync(original);
            Require(calls.Count == callsBefore && grid.Items.Count == rowsBefore && window.DraftText == "先行後続。",
                "成功後に元の失敗行から再試行しても再認識せず本文を重複採用しない");
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult();
            await controller.StopAsync();
        }
    }
    private static async Task CheckTemporaryPinAsync(Application app)
    {
        using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "voice-pin-" + Guid.NewGuid().ToString("N"))));
        store.SaveVoiceInputSettings(new() { AutomaticInput = true, AutomaticFormatting = true, AutomaticConfirm = true });
        var formats = 0;
        TaskCompletionSource<string>? formattingGate = null;
        var controller = new VoiceInputController(store.Paths, store, app.Dispatcher, true, true,
            formatter: (text, _, _) => { formats++; return formattingGate?.Task ?? Task.FromResult("整形:" + text); });
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
            formattingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(VoiceInputController).GetField("_automaticSession", flags)!.SetValue(controller, typeof(VoiceInputController).GetField("_session", flags)!.GetValue(controller));
            typeof(VoiceInputController).GetField("_automaticDraftRequested", flags)!.SetValue(controller, true);
            typeof(VoiceInputController).GetMethod("TryAutomaticInput", flags)!.Invoke(controller, null);
            Require(formats == 2, "自動決定オンで非同期整形を開始");
            Message(0xA1, 2); Message(0x216); Message(0x232);
            formattingGate.SetResult("整形:整形待ち本文");
            await Until(() => !(bool)typeof(VoiceInputController).GetField("_automaticProcessing", flags)!.GetValue(controller)!);
            Require(window.IsVisible && window.DraftText == "整形:整形待ち本文" && !target.Consumed &&
                !(bool)typeof(VoiceInputController).GetField("_automaticFaulted", flags)!.GetValue(controller)!,
                "整形開始後のマウス移動でも書込みせず決定待ちにする");
        }
        finally { await controller.StopAsync(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 120 && !predicate(); i++) await Task.Delay(50);
        Require(predicate(), "期限内に処理完了");
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

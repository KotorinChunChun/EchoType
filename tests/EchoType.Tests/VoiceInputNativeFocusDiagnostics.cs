using EchoType.App;
using EchoType.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

internal static class VoiceInputNativeFocusDiagnostics
{
    private static readonly WindowProc Procedure = Dispatch;
    private static nint _first, _second;
    internal static int Fixture()
    {
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Name = "EchoTypeFocusFixture" };
        if (RegisterClassEx(ref cls) == 0) return 2;
        _first = CreateWindowEx(0, cls.Name, "検証専用設定", 0x10CF0000, 20, 20, 400, 230, 0, 0, cls.Instance, 0);
        _second = CreateWindowEx(0, cls.Name, "検証専用別窓", 0x10CF0000, 440, 20, 400, 230, 0, 0, cls.Instance, 0);
        var edits = Enumerable.Range(0, 3).Select(i => CreateWindowEx(0, "Edit",
            i == 2 ? "前の行\r\nこの位置\r\n最終行" : "未保存の用途" + (i + 1),
            i == 2 ? 0x50010084u : 0x50010080u, 10, 10 + i * 45, 350, i == 2 ? 88 : 32,
            _first, new nint(240 + i), cls.Instance, 0)).ToArray();
        var other = CreateWindowEx(0, "Edit", "別窓の入力", 0x50010080, 10, 10, 350, 32, _second, new nint(250), cls.Instance, 0);
        Console.WriteLine(string.Join(",", new[] { _first, _second, other }.Concat(edits).Select(h => h.ToInt64()))); Console.Out.Flush();
        if (_first == 0 || _second == 0 || edits.Any(h => h == 0) || other == 0) throw new Exception("Win32 fixtureの作成失敗");
        while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        return 0;
    }
    internal static async Task VerifyAsync(Application app, int seconds, bool interactive = false, bool confirmOnly = false)
    {
        var previousForeground = VoiceInputTarget.ForegroundWindow;
        using var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--voice-focus-native-fixture") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        VoiceInputController? controller = null; nint closeWindow = 0;
        try
        {
            var line = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new Exception("native fixture未起動");
            var handles = line.Split(',').Select(v => new nint(long.Parse(v))).ToArray();
            var first = closeWindow = handles[0]; var second = handles[1]; var other = handles[2]; var edits = handles.Skip(3).ToArray();
            using var store = new SecureStore(new AgentPaths(Path.Combine(AppContext.BaseDirectory, "test-output", "native-focus-" + Guid.NewGuid().ToString("N"))));
            controller = new(store.Paths, store, app.Dispatcher, true, true, keyState: _ => false);
            if (confirmOnly)
            {
                await VerifyConfirmAsync(app, controller, first, second, edits[0], edits[1], edits[2], other);
                await controller.StopAsync();
                controller = null;
                await VerifyRecordingFocusReturnAsync(app, store, first, second, other);
                return;
            }
            if (interactive)
            {
                SendMessage(first, 0x8001, edits[0], 1);
                if (VoiceInputTarget.ForegroundWindow != first)
                    Console.WriteLine("30秒以内に検証専用設定ウィンドウを前面にしてください。");
                for (var attempt = 0; attempt < 300 && VoiceInputTarget.ForegroundWindow != first; attempt++) await Task.Delay(100);
            }
            foreach (var edit in edits)
            {
                SendMessage(first, 0x8001, edit, interactive ? 1 : 0); await Task.Delay(80);
                RequireThreadFocus(first, edit);
                if (interactive)
                {
                    Require(VoiceInputTarget.ForegroundWindow == first, "検証対象を前景に設定");
                    var target = await Task.Run(() => VoiceInputTarget.CaptureNear(AutomationElement.FromHandle(edit), null));
                    target.SetFocusLog((stage, window) => VoiceInputFocusJournal.Write(store.Paths, "native-test", stage, "explicit-test", window));
                    await Task.Run(() => target.InsertAsync("明示入力の検証", replace: true));
                    Require(target.WriteVerified, "標準Editへの明示的な全文置換が成功");
                    var retained = await Task.Run(target.RefreshSnapshot);
                    SendMessage(second, 0x8001, other, 1); await Task.Delay(80);
                    var blocked = false;
                    try { await Task.Run(() => retained.InsertAsync("古い対象へ送らない", replace: true)); }
                    catch (InvalidOperationException) { blocked = true; }
                    Require(blocked && !retained.Consumed && VoiceInputTarget.ForegroundWindow == second, "別窓へ移った後は前面化・書込みを中止");
                    Require(await Task.Run(() => ((ValuePattern)AutomationElement.FromHandle(edit).GetCurrentPattern(ValuePattern.Pattern)).Current.Value) == "明示入力の検証", "拒否した入力で元の欄を変更しない");
                }
                else
                {
                    // 非入力デスクトップではUIAのWin32クライアント側プロバイダーが利用できない。
                    // ネイティブなアクティブ窓・フォーカスの保持だけを測る。
                    SendMessage(second, 0x8001, other, 0); await Task.Delay(80);
                    RequireThreadFocus(second, other);
                }
            }
            var watch = Stopwatch.StartNew(); var samples = 0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                RequireQuiet(VoiceInputTarget.ForegroundWindow == (interactive ? second : 0), "前景条件が変化した");
                RequireThreadFocus(second, other);
                samples++; await Task.Delay(25);
            }
            Console.WriteLine($"PASS 標準Win32 Editの未保存状態から別窓へ移動し{watch.Elapsed.TotalSeconds:F2}秒・{samples}標本で検証UIスレッド内のアクティブ窓と入力フォーカスを維持 / 通常画面={interactive}");
            if (interactive) await VerifyConfirmAsync(app, controller, first, second, edits[0], edits[1], edits[2], other);
        }
        catch (Exception ex) { Console.Error.WriteLine("native試験の元例外: " + ex); throw; }
        finally
        {
            if (controller is not null) await controller.StopAsync();
            if (interactive && previousForeground != 0 && fixture.Id == ForegroundProcessId()) SetForegroundWindow(previousForeground);
            if (!fixture.HasExited) { if (closeWindow != 0) PostMessage(closeWindow, 0x0010, 0, 0); else fixture.CloseMainWindow(); try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { Console.Error.WriteLine("検証fixtureは親の専用Job終了時に回収"); } }
        }
    }

    private static async Task VerifyConfirmAsync(Application app, VoiceInputController controller,
        nint first, nint second, nint edit, nint untouched, nint multiline, nint other)
    {
        static string Read(nint handle)
        {
            var text = new StringBuilder(1024);
            ReadControlText(handle, 0x000D, text.Capacity, text);
            return text.ToString();
        }
        var original = await Task.Run(() => Read(edit));
        var untouchedText = await Task.Run(() => Read(untouched));
        var otherText = await Task.Run(() => Read(other));
        controller.Open();
        var window = app.Windows.OfType<VoiceInputWindow>().Single();
        const uint preserveGeometryAndFocus = 0x0013;
        async Task Pick(nint field)
        {
            // 画面の大きさによってはEchoTypeが検証欄を覆うため、座標選択の間だけ試験窓を上に置く。
            RequireQuiet(SetWindowPos(first, -1, 0, 0, 0, 0, preserveGeometryAndFocus), "検証欄を前面の表示順にできません");
            try
            {
                var bounds = await Task.Run(() => AutomationElement.FromHandle(field).Current.BoundingRectangle);
                var point = new Point(bounds.Right - 8, bounds.Top + bounds.Height / 2);
                var hit = await Task.Run(() => AutomationElement.FromPoint(point).Current.NativeWindowHandle);
                RequireQuiet(hit == field, "検証欄の座標を別の要素が覆っています");
                await controller.PickTargetAsync(point);
            }
            finally { SetWindowPos(first, -2, 0, 0, 0, 0, preserveGeometryAndFocus); }
        }
        await Pick(edit);
        var target = (System.Windows.Controls.TextBox)window.FindName("TargetPath");
        var status = (System.Windows.Controls.TextBlock)window.FindName("StatusText");
        Require(target.Text.Contains("AutomationId=240"), "指定座標から目的の編集欄を取得 / " + status.Text);
        var button = (System.Windows.Controls.Button)window.FindName("ConfirmButton");
        void Confirm() => ((System.Windows.Automation.Provider.IInvokeProvider)
            new System.Windows.Automation.Peers.ButtonAutomationPeer(button)).Invoke();
        async Task Wait(Func<bool> done)
        {
            for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50);
            Require(done(), "決定処理を期限内に完了 / " + status.Text);
        }

        window.DraftText = "確定本文";
        VoiceInputWindow.ActivateNativeWindow(second);
        SendMessage(second, 0x8001, other, 1);
        await Task.Delay(100);
        Require(VoiceInputTarget.ForegroundWindow == second, "決定前に別窓を前面にする");
        await controller.ConfirmAsync();
        Require(status.Text.Contains("別のアプリへ移動"), "別窓からの書き込みを拒否 / " + status.Text);
        Require(await Task.Run(() => Read(edit)) == original && await Task.Run(() => Read(other)) == otherText,
            "別窓を前面にした場合は、どちらの欄にも書かずエラーを表示");

        window.FocusDraft();
        Confirm();
        await Wait(() => !window.IsVisible);
        Require(await Task.Run(() => Read(edit)) == original + "確定本文" &&
            await Task.Run(() => Read(untouched)) == untouchedText && await Task.Run(() => Read(other)) == otherText,
            "本物の決定ボタンで指定欄に追記し、同じ窓の別欄と別窓は変更しない");
        Require(VoiceInputTarget.ForegroundWindow == first, "入力後に指定窓が前面にある");

        controller.Open();
        await Pick(multiline);
        await controller.SetReplaceTargetTextAsync(true);
        var editor = (System.Windows.Controls.TextBox)window.FindName("Editor");
        var before = editor.Text;
        var position = before.IndexOf("位置", StringComparison.Ordinal);
        Require(position > 0 && before.EndsWith("最終行"), "複数行の既存本文を編集欄へ読み込む");
        editor.Select(position, 0);
        const string additional = "追加入力";
        Require(window.InsertDraft(additional) && window.DraftCaretIndex == position + additional.Length,
            "編集中のカーソルが追加入力の直後に残る");
        Confirm();
        try { await Wait(() => !window.IsVisible); }
        catch
        {
            Console.Error.WriteLine($"検証欄: 元={System.Text.Json.JsonSerializer.Serialize(before)}, 結果={System.Text.Json.JsonSerializer.Serialize(await Task.Run(() => Read(multiline)))}");
            throw;
        }
        var actual = await Task.Run(() => Read(multiline));
        var caret = actual.IndexOf(additional, StringComparison.Ordinal) + additional.Length;
        var selection = SendMessage(multiline, 0xB0, 0, 0);
        Require(actual.Replace("\r", "") == before.Insert(position, additional).Replace("\r", "") &&
            selection == (caret | caret << 16) && caret < actual.Length,
            "複数行を決定ボタンで置換後にカーソルが追加入力の直後に残る");
        var undo = SendMessage(multiline, 0xC7, 0, 0);
        var undone = Read(multiline);
        Require(undone.Replace("\r", "") == before.Replace("\r", ""),
            $"入力先自身の元に戻す操作が使える / 応答={undo}, 本文={System.Text.Json.JsonSerializer.Serialize(undone)}");
    }
    private static async Task VerifyRecordingFocusReturnAsync(Application app, SecureStore store, nint first, nint second, nint other)
    {
        store.SaveVoiceInputSettings(new());
        var pressed = 0;
        var polls = 0;
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new VoiceInputController(store.Paths, store, app.Dispatcher, false, true,
            openInput: _ => new VoiceInputQueueDiagnostics.Input(),
            recognizer: (_, _, _, _) => throw new InvalidOperationException("空録音は認識しない"),
            keyState: key =>
            {
                if (key == 0xFE && Interlocked.Increment(ref polls) == 2) armed.TrySetResult();
                return Volatile.Read(ref pressed) != 0 && key is 0xA2 or 0xA0;
            });
        static async Task Wait(Func<bool> ready, string message)
        {
            for (var i = 0; i < 75 && !ready(); i++) await Task.Delay(20);
            RequireQuiet(ready(), message);
        }
        try
        {
            await armed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            async Task<VoiceInputWindow> PressAsync()
            {
                SendMessage(first, 0x8001, first, 1);
                await Wait(() => VoiceInputTarget.ForegroundWindow == first, "編集欄のない検証窓を前面にできません");
                RequireThreadFocus(first, first);
                Volatile.Write(ref pressed, 1);
                await Wait(() => app.Windows.OfType<VoiceInputWindow>().Any(window => window.IsActive),
                    "録音キーでEchoTypeを前面にできません");
                return app.Windows.OfType<VoiceInputWindow>().Single();
            }
            var echo = await PressAsync();
            // 録音中に元の窓が再表示され、Windowsの直前のウィンドウが変わる状況を作る。
            ShowWindow(first, 0);
            ShowWindow(first, 8);
            RequireQuiet(SetWindowPos(second, 0, 0, 0, 0, 0, 0x0013), "検証窓の表示順を変更できません");
            RequireQuiet(echo.IsActive, "検証窓の再表示でEchoTypeからフォーカスが外れました");
            Volatile.Write(ref pressed, 0);
            await Wait(() => !echo.IsVisible, "空の録音を終了して画面を閉じられません");
            await Wait(() => VoiceInputTarget.ForegroundWindow == first, "録音開始元へフォーカスが戻りません");
            RequireThreadFocus(first, first);
            Console.WriteLine("PASS 録音キーを離して閉じた後に、編集欄のない元の窓へフォーカスを戻す");

            echo = await PressAsync();
            SendMessage(second, 0x8001, other, 1);
            await Wait(() => VoiceInputTarget.ForegroundWindow == second, "録音中に別の検証窓を前面にできません");
            Volatile.Write(ref pressed, 0);
            await Wait(() => !echo.IsVisible, "別窓に移動しても空の録音を閉じられません");
            Require(VoiceInputTarget.ForegroundWindow == second, "別窓に手動で移動した場合はフォーカスを維持");
            RequireThreadFocus(second, other);
        }
        finally { await controller.StopAsync(); }
    }
    private static nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x8001) { if (lParam != 0) SetForegroundWindow(hwnd); SetActiveWindow(hwnd); SetFocus(wParam); return 0; }
        if (message == 0x0010) { DestroyWindow(_first); DestroyWindow(_second); PostQuitMessage(0); return 0; }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private static int ForegroundProcessId() { GetWindowThreadProcessId(VoiceInputTarget.ForegroundWindow, out var pid); return pid; }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    private static void RequireThreadFocus(nint window, nint focus)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        RequireQuiet(GetGUIThreadInfo(thread, ref info) && info.Active == window && info.Focus == focus, "検証UIスレッドのアクティブ窓・入力先が変化した");
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint hwnd);
    private static void Require(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS " + text); }
    private static void RequireQuiet(bool value, string text) { if (!value) throw new Exception(text); }
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern nint ReadControlText(nint hwnd, uint message, nint capacity, StringBuilder output);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}

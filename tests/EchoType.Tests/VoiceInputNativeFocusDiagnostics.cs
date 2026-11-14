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
        var edits = Enumerable.Range(0, 3).Select(i => CreateWindowEx(0, "Edit", "未保存の用途" + (i + 1), 0x50010080, 10, 10 + i * 45, 350, 32, _first, new nint(240 + i), cls.Instance, 0)).ToArray();
        var other = CreateWindowEx(0, "Edit", "別窓の入力", 0x50010080, 10, 10, 350, 32, _second, new nint(250), cls.Instance, 0);
        Console.WriteLine(string.Join(",", new[] { _first, _second, other }.Concat(edits).Select(h => h.ToInt64()))); Console.Out.Flush();
        if (_first == 0 || _second == 0 || edits.Any(h => h == 0) || other == 0) throw new Exception("Win32 fixtureの作成失敗");
        while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        return 0;
    }
    internal static async Task VerifyAsync(Application app, int seconds, bool interactive = false)
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
        }
        catch (Exception ex) { Console.Error.WriteLine("native試験の元例外: " + ex); throw; }
        finally
        {
            if (controller is not null) await controller.StopAsync();
            if (interactive && previousForeground != 0 && fixture.Id == ForegroundProcessId()) SetForegroundWindow(previousForeground);
            if (!fixture.HasExited) { if (closeWindow != 0) PostMessage(closeWindow, 0x0010, 0, 0); else fixture.CloseMainWindow(); try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { Console.Error.WriteLine("検証fixtureは親の専用Job終了時に回収"); } }
        }
    }
    private static nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x8001) { if (lParam != 0) SetForegroundWindow(hwnd); SetActiveWindow(hwnd); SetFocus(wParam); return 0; }
        if (message == 0x0010) { DestroyWindow(_first); DestroyWindow(_second); PostQuitMessage(0); return 0; }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private static int ForegroundProcessId() { GetWindowThreadProcessId(VoiceInputTarget.ForegroundWindow, out var pid); return pid; }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
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
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using EchoType.Core;

namespace EchoType.Windows;

public sealed record WindowsCaptionSnapshot(string Text, string Status)
{
    public DateTimeOffset ObservedAt { get; init; }
    public string Session { get; init; } = "";
    public bool Ready { get; init; }
    public CaptionTextElement[]? Elements { get; init; }
}
public sealed record WindowsCaptionConfiguration(bool Succeeded, bool? OriginalMicrophone, bool? Microphone, bool Ready,
    string Language, string Status, string Error, int ProcessId, string Session, DateTimeOffset ObservedAt);

public static class WindowsLiveCaptionReader
{
    private static readonly SemaphoreSlim ConfigurationGate = new(1, 1);
    private static bool BackgroundMode => AppContext.TryGetSwitch("EchoType.Captions.Background", out var enabled) && enabled;
    public static void Open()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe");
        if (!File.Exists(path)) throw new InvalidOperationException("WindowsライブキャプションにはWindows 11 22H2以降が必要です。");
        // 計測中は既存のOS字幕を呼び直して前面へ出さない。
        var alreadyRunning = false;
        if (BackgroundMode)
            foreach (var process in Process.GetProcessesByName("LiveCaptions"))
                using (process)
                    alreadyRunning |= string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
        if (!alreadyRunning)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        var wait = Stopwatch.StartNew();
        do
        {
            if (MakeTransparent(path) || !BackgroundMode) return;
            Thread.Sleep(50);
        } while (wait.Elapsed < TimeSpan.FromSeconds(3));
        throw new InvalidOperationException("Windows字幕ウィンドウの非表示を確認できないため計測を開始しません。");
    }

    private static bool MakeTransparent(string? path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe");
        var found = false;
        // UIAは隠したウィンドウの子要素を取得できないため、透明化してアクティブ化せず描画を維持する。
        foreach (var process in Process.GetProcessesByName("LiveCaptions"))
        {
            using (process)
            {
                if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var pid);
                    if (pid == process.Id) { var name = new StringBuilder(100); GetClassName(handle, name, name.Capacity);
                        if (name.ToString() == "LiveCaptionsDesktopWindow")
                        {
                            var cloak = 0;
                            DwmSetWindowAttribute(handle, 13, ref cloak, sizeof(int));
                            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x80000);
                            if (!SetLayeredWindowAttributes(handle, 0, 0, 2)) throw new InvalidOperationException("Windows字幕ウィンドウを非表示にできませんでした。");
                            if (BackgroundMode && !SetWindowPos(handle, -2, 0, 0, 0, 0, 0x213))
                                throw new InvalidOperationException("Windows字幕ウィンドウの最前面を解除できませんでした。");
                            if (!IsWindowVisible(handle)) ShowWindow(handle, 4);
                            found = true;
                        } }
                    return true;
                }, 0);
            }
        }
        return found;
    }

    public static async Task<WindowsCaptionConfiguration> ConfigureAsync(bool? microphone, CancellationToken ct)
    {
        await ConfigurationGate.WaitAsync(ct);
        try { return await ReadConfigurationAsync(microphone, ct); }
        finally
        {
            ConfigurationGate.Release();
            if (BackgroundMode) MakeTransparent();
        }
    }

    private static async Task<WindowsCaptionConfiguration> ReadConfigurationAsync(bool? microphone, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Captions", "EchoType.Captions.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--caption-config"); start.ArgumentList.Add(microphone is null ? "get" : microphone.Value ? "on" : "off");
        using var job = new ProcessJob();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows字幕の設定確認を開始できません。"); job.Assign(process);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = JsonSerializer.Deserialize<WindowsCaptionConfiguration>(await output) ?? throw new InvalidOperationException("字幕設定の応答が空です。");
            await errors;
            if (process.ExitCode != 0 || !result.Succeeded) throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Status);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Windows字幕の設定確認が時間切れになりました。"); }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync();
        }
    }

    private delegate bool EnumWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint handle, uint color, byte alpha, uint flags);

    public static async Task ReadAsync(Action<WindowsCaptionSnapshot> updated, CancellationToken ct, bool everySnapshot = false)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Captions", "EchoType.Captions.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--live-captions");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows字幕の取得を開始できません。");
        using var job = new ProcessJob(); job.Assign(process);
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            WindowsCaptionSnapshot? last = null;
            while (true)
            {
                if (BackgroundMode) MakeTransparent();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new InvalidOperationException("Windows字幕の取得が終了しました。");
                if (line.Length > 120000) throw new InvalidOperationException("Windows字幕の応答が長すぎます。");
                var value = JsonSerializer.Deserialize<WindowsCaptionSnapshot>(line) ?? throw new InvalidOperationException("Windows字幕の応答が不正です。");
                if (everySnapshot || value.Text != last?.Text || value.Status != last?.Status)
                { ct.ThrowIfCancellationRequested(); updated(value); last = value; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Windows字幕の取得が応答しません。字幕を開き直してください。"); }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync();
            await errors;
        }
    }
}

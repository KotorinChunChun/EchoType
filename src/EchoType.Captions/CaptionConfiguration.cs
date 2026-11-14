using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

namespace EchoType.Captions;

/// <summary>実機で確認したOS字幕メニューのみをUIAで設定する。親がプロセス単位の期限を管理する。</summary>
internal static class CaptionConfiguration
{
    private sealed record Result(bool Succeeded, bool? OriginalMicrophone, bool? Microphone, bool Ready,
        string Language, string Status, string Error, int ProcessId, string Session, DateTimeOffset ObservedAt);

    public static int Run(string operation)
    {
        if (operation is not ("get" or "on" or "off")) return 2;
        bool? original = null, applied = null;
        var ready = false; var language = ""; var status = ""; var error = ""; var pid = 0; var session = "";
        var changed = false;
        Process? target = null;
        try
        {
            target = WaitForVisibleProcess(); pid = target.Id;
            session = pid + ":" + target.StartTime.ToUniversalTime().Ticks;
            var main = Windows(pid).FirstOrDefault(w => w.ClassName == "LiveCaptionsDesktopWindow");
            if (main is null) throw new InvalidOperationException("Windowsライブキャプションのウィンドウを表示してください。");
            var readyText = Find(pid, "ReadyToCaptionTextBlock", ControlType.Text);
            ready = readyText is not null || Find(pid, "CaptionsTextBlock", ControlType.Text) is not null;
            if (readyText?.Current.Name.Contains("日本語", StringComparison.Ordinal) == true) language = "ja-JP";
            status = ready ? "Windowsライブキャプションは準備済みです。" : "Windows側の字幕言語・初期設定を確認してください。";
            var toggle = Microphone(pid);
            original = applied = Read(toggle);
            if (operation != "get" && original.Value != (operation == "on"))
            {
                changed = true;
                applied = null;
                toggle.Toggle();
                applied = Read(Microphone(pid));
                if (applied != (operation == "on")) throw new InvalidOperationException("マイク設定の変更を確認できませんでした。");
            }
        }
        catch (Exception ex)
        {
            error = Message(ex);
            // 変更要求が失敗したら、現在値を確かめられる場合だけ元へ戻す。
            if (changed && original.HasValue && target is { HasExited: false })
            {
                try
                {
                    var toggle = Microphone(pid); applied = Read(toggle);
                    if (applied != original) { applied = null; toggle.Toggle(); applied = Read(Microphone(pid)); }
                    if (applied != original) error += " 元のマイク設定へ戻せませんでした。";
                }
                catch (Exception) { applied = null; error += " 元のマイク設定を確認できません。Windows字幕の設定を確認してください。"; }
            }
        }
        finally
        {
            if (target is not null)
            {
                try { if (!target.HasExited) CloseMenus(pid); }
                catch (Exception) { error += " Windows字幕の設定メニューを閉じられませんでした。"; }
                target.Dispose();
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new Result(error.Length == 0, original, applied, ready,
            language, status, error, pid, session, DateTimeOffset.UtcNow)));
        return error.Length == 0 ? 0 : 1;
    }

    private static string Message(Exception ex) => ex is InvalidOperationException ? ex.Message :
        "Windows字幕の設定を取得・変更できませんでした（" + ex.GetType().Name + "）。";

    private static Process WaitForVisibleProcess()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe");
        using var current = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        var foundProcess = false;
        do
        {
            foreach (var process in Process.GetProcessesByName("LiveCaptions"))
            {
                var keep = false;
                try
                {
                    if (process.SessionId != current.SessionId ||
                        !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    foundProcess = true;
                    if (Windows(process.Id).Any(w => w.ClassName == "LiveCaptionsDesktopWindow"))
                    { keep = true; return process; }
                }
                catch (Exception) { /* 起動/終了直後は次の短い照合で確認する。 */ }
                finally { if (!keep) process.Dispose(); }
            }
            Thread.Sleep(100);
        } while (watch.ElapsedMilliseconds < 3000);
        throw new InvalidOperationException(foundProcess
            ? "Windowsライブキャプションのウィンドウを表示してください。"
            : "Windowsライブキャプションを先に開いてください。");
    }

    private static TogglePattern Microphone(int pid)
    {
        var element = Find(pid, "MicrophoneMenuFlyoutItem", ControlType.MenuItem);
        if (element is null)
        {
            var preferences = Find(pid, "PreferencesButton", ControlType.MenuItem);
            if (preferences is null)
            {
                var settings = Find(pid, "SettingsButton", ControlType.Button)
                    ?? throw new InvalidOperationException("Windows字幕の設定ボタンを取得できません。");
                ((InvokePattern)settings.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                preferences = Wait(pid, "PreferencesButton", ControlType.MenuItem);
            }
            ((ExpandCollapsePattern)preferences.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            element = Wait(pid, "MicrophoneMenuFlyoutItem", ControlType.MenuItem);
        }
        return (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
    }

    private static bool Read(TogglePattern toggle) => toggle.Current.ToggleState switch
    {
        ToggleState.On => true,
        ToggleState.Off => false,
        _ => throw new InvalidOperationException("Windows字幕のマイク設定が不定です。")
    };

    private static AutomationElement Wait(int pid, string id, ControlType type)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (Find(pid, id, type) is { } value) return value;
            Thread.Sleep(25);
        } while (watch.ElapsedMilliseconds < 750);
        throw new InvalidOperationException("Windows字幕の設定メニューが応答しません。");
    }

    private static AutomationElement? Find(int pid, string id, ControlType type)
    {
        foreach (var window in Windows(pid))
        {
            // WinUIのpopupは親HWNDからのDescendants検索だけでは見落とすことがある。
            var root = AutomationElement.FromHandle(window.Handle);
            var found = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, id),
                new PropertyCondition(AutomationElement.ControlTypeProperty, type)));
            if (found is not null) return found;
        }
        return null;
    }

    private static void CloseMenus(int pid)
    {
        foreach (var window in Windows(pid).Where(w => w.Visible && w.ClassName == "Xaml_WindowedPopupClass"))
        {
            var popup = AutomationElement.FromHandle(window.Handle).FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Popup"));
            if (popup is not null && popup.TryGetCurrentPattern(WindowPattern.Pattern, out var value))
                ((WindowPattern)value).Close();
        }
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 250 && Windows(pid).Any(w => w.Visible && w.ClassName == "Xaml_WindowedPopupClass"))
            Thread.Sleep(25);
        if (Windows(pid).Any(w => w.Visible && w.ClassName == "Xaml_WindowedPopupClass"))
            throw new InvalidOperationException("Windows字幕の設定メニューが開いたままです。");
    }

    private sealed record Window(IntPtr Handle, string ClassName, bool Visible);
    internal static IntPtr CaptionWindow(int pid) => Windows(pid).FirstOrDefault(w => w.ClassName == "LiveCaptionsDesktopWindow")?.Handle ?? IntPtr.Zero;
    private static List<Window> Windows(int pid)
    {
        var windows = new List<Window>();
        EnumProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid) return true;
            var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "LiveCaptionsDesktopWindow")
            {
                var cloak = 0;
                DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int));
                SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 0x80000);
                if (!SetLayeredWindowAttributes(hwnd, 0, 0, 2)) throw new InvalidOperationException("Windows字幕を非表示にできませんでした。");
                if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, 4);
            }
            if (name.ToString() is "LiveCaptionsDesktopWindow" or "Xaml_WindowedPopupClass")
                windows.Add(new(hwnd, name.ToString(), IsWindowVisible(hwnd)));
            return true;
        };
        EnumWindows(callback, IntPtr.Zero); GC.KeepAlive(callback);
        return windows;
    }
    private delegate bool EnumProc(IntPtr window, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint handle, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}

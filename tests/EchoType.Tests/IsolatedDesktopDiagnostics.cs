using EchoType.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>SwitchDesktopを呼ばず、実入力を使わないWPF試験だけを非表示デスクトップで実行する。</summary>
internal static class IsolatedDesktopDiagnostics
{
    public static int Run(string mode, string? appExecutable = null)
    {
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "test-output", "app-" + Guid.NewGuid().ToString("N"));
        var (executable, argument) = mode switch
        {
            "queue" => (Environment.ProcessPath!, "--queue-child"),
            "focus" => (Environment.ProcessPath!, "--focus-child"),
            "app" or "app-legacy" when appExecutable is not null => (Path.GetFullPath(appExecutable),
                $"--smoke-ui --data-root \"{dataRoot}\""),
            _ => throw new ArgumentException("隔離対象外の試験です。")
        };
        if (mode == "app-legacy") MigrationTests.SeedSavedLegacySettings(dataRoot);
        var name = "EchoTypeTest_" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, null, 0, 0, 0x10000000, 0);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "検証用デスクトップを作成できません。通常画面には切り替えません。");
        var log = Path.Combine(AppContext.BaseDirectory, "test-output", name + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        nint output = new(-1); ProcessInformation pi = default;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            output = CreateFile(log, 0x40000000, 3, ref security, 2, 0x80, 0);
            if (output == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = name, Flags = 0x100, StdOutput = output, StdError = output, StdInput = GetStdHandle(-10) };
            var command = new StringBuilder($"\"{executable}\" {argument}");
            using var job = new ProcessJob();
            if (!CreateProcess(executable, command, 0, 0, true, 0x08000004, 0, AppContext.BaseDirectory, ref startup, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var process = Process.GetProcessById(pi.ProcessId);
            job.Assign(process);
            if (ResumeThread(pi.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!process.WaitForExit(mode is "update" or "focus" ? 240000 : 120000)) throw new TimeoutException("非表示デスクトップの検証が制限時間内に完了しませんでした。");
            CloseHandle(output); output = new(-1);
            using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) Console.Write(reader.ReadToEnd());
            Console.WriteLine($"非表示デスクトップ試験: {mode} / 終了コード {process.ExitCode} / ログ {log}");
            return process.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine("非表示デスクトップ試験失敗: " + ex.Message); return 1; }
        finally
        {
            if (pi.Thread != 0) CloseHandle(pi.Thread);
            if (pi.Process != 0) CloseHandle(pi.Process);
            if (output != new nint(-1)) CloseHandle(output);
            CloseDesktop(desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CountX, CountY, Fill, Flags;
        public short Show, ReservedSize; public nint ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, string? device, nint mode, int flags, uint access, nint security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFile(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
}

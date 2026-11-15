using EchoType.Windows;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace EchoType.App;

internal sealed class EchoTypeManagement : IDisposable
{
    internal const string ToolId = "echo-type";
    private const int MaximumRequestCharacters = 4096;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _lifetime = new();

    public EchoTypeManagement(Dispatcher dispatcher, string root, string launchOrigin, Action show, Action stop,
        Func<string?> stopBlockReason)
    {
        var cancellation = _lifetime.Token;
        _ = Task.Run(() => RunAsync(dispatcher, PipeName(root), launchOrigin, show, stop, stopBlockReason, cancellation));
    }

    internal static string PipeName(string root)
    {
        var prefix = "KotorinToys." + WindowsIdentity.GetCurrent().User!.Value + "." + ToolId;
        if (string.Equals(root, AgentPaths.DefaultRoot, StringComparison.OrdinalIgnoreCase)) return prefix;
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        return prefix + "." + profile;
    }

    private static async Task RunAsync(Dispatcher dispatcher, string pipeName, string launchOrigin, Action show, Action stop,
        Func<string?> stopBlockReason, CancellationToken cancellation)
    {
        var version = WindowTitle.Version;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(Deadline);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await ReadLineAsync(reader, deadline.Token).ConfigureAwait(false);
                if (line is null) continue;
                var request = ReadRequest(line);
                var response = await dispatcher.InvokeAsync(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    return Apply(request, show, stopBlockReason);
                }, DispatcherPriority.Normal, deadline.Token).Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                var body = JsonSerializer.Serialize(new
                {
                    protocolVersion = 2, requestId = request.Id, pid = Environment.ProcessId,
                    ready = response.Ready, version, launchOrigin, capabilities = new[] { "status", "show", "stop" },
                    result = response.Result, detail = response.Detail, shortcuts = (object?)null
                });
                await writer.WriteLineAsync(body.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (response.Result == "accepted")
                    await dispatcher.InvokeAsync(() =>
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        stop();
                    }, DispatcherPriority.Normal, deadline.Token).Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or JsonException
                or FormatException or KeyNotFoundException)
            {
                if (!cancellation.IsCancellationRequested)
                    await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    private static (string Command, int? ExpectedPid, string Id) ReadRequest(string line)
    {
        if (!line.StartsWith('{')) return (line, null, "");
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        return (request.GetProperty("command").GetString() ?? "",
            request.TryGetProperty("expectedPid", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.GetInt32() : null,
            request.TryGetProperty("requestId", out var id) ? id.GetString() ?? "" : "");
    }

    private static (bool Ready, string Result, string Detail) Apply(
        (string Command, int? ExpectedPid, string Id) request, Action show, Func<string?> stopBlockReason)
    {
        if (request.ExpectedPid is { } pid && pid != Environment.ProcessId)
            return (true, "refused", "対象PIDが変わりました。");
        try
        {
            switch (request.Command)
            {
                case "status": return (true, "completed", "");
                case "show":
                    show();
                    return (true, "completed", "");
                case "stop":
                    var reason = stopBlockReason();
                    return string.IsNullOrEmpty(reason) ? (true, "accepted", "") : (true, "refused", reason);
                default: return (true, "unsupported", "未対応の操作です。");
            }
        }
        catch (Exception)
        {
            return (false, "failed", "UI操作を完了できませんでした。");
        }
    }

    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken cancellation)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false) > 0)
        {
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaximumRequestCharacters) throw new InvalidDataException("管理要求が長すぎます。");
            line.Append(buffer[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }

    internal static async Task<bool> ShowExistingAsync(string root)
    {
        try
        {
            var pipe = PipeName(root);
            using var deadline = new CancellationTokenSource(Deadline);
            using var status = await SendAsync(pipe, "status", deadline.Token).ConfigureAwait(false);
            var response = status.RootElement;
            if (response.GetProperty("protocolVersion").GetInt32() != 2 || !response.GetProperty("ready").GetBoolean()) return false;
            var pid = response.GetProperty("pid").GetInt32();
            using var existing = Process.GetProcessById(pid);
            using var current = Process.GetCurrentProcess();
            if (existing.SessionId != current.SessionId ||
                !string.Equals(existing.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
            AllowSetForegroundWindow(pid);
            using var shown = await SendAsync(pipe, JsonSerializer.Serialize(new { command = "show", expectedPid = pid }),
                deadline.Token, pid).ConfigureAwait(false);
            return shown.RootElement.GetProperty("result").GetString() == "completed";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException or ArgumentException
            or KeyNotFoundException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<JsonDocument> SendAsync(string name, string request, CancellationToken cancellation,
        int? expectedPid = null)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellation).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid == 0 ||
            expectedPid is { } expected && serverPid != expected)
            throw new IOException("管理窓口のプロセスを確認できません。");
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(request.AsMemory(), cancellation).ConfigureAwait(false);
        var reply = JsonDocument.Parse(await ReadLineAsync(reader, cancellation).ConfigureAwait(false) ?? "");
        try
        {
            if (reply.RootElement.GetProperty("pid").GetInt32() != serverPid)
                throw new IOException("管理窓口と応答のプロセスが一致しません。");
            return reply;
        }
        catch
        {
            reply.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

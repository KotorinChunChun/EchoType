using EchoType.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EchoType.Windows;

public sealed partial class CodexClient
{
    public static string DiscoverExecutable()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        if (File.Exists(file)) return file;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(folder.Trim('"')) || folder.StartsWith(@"\\")) continue;
            var candidate = Path.Combine(folder.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }
    public static string[] Restrictions => ["shell_tool", "unified_exec", "apps", "plugins", "hooks", "computer_use", "browser_use", "browser_use_external", "in_app_browser", "image_generation", "multi_agent", "multi_agent_v2", "code_mode", "code_mode_host", "remote_plugin", "skill_mcp_dependency_install", "skill_search", "workspace_dependencies", "memories"];
    private async Task<string> RunAsync(string schema, string prompt, AppSettings settings, CancellationToken ct, string purpose, string model = "gpt-6-luna", bool search = false, string? effort = null, int? timeoutSeconds = null, int? maxOutputBytes = null)
    {
        using var activity = ModelActivityHub.For(paths).Begin("文章生成（Codex）", model, purpose, "クラウド", ModelPhase.Processing);
        try { var result = await RunCoreAsync(schema, prompt, settings, ct, purpose, model, search, effort, timeoutSeconds, maxOutputBytes).ConfigureAwait(false); activity.Finish(ModelPhase.Completed); return result; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { activity.Finish(ModelPhase.Cancelled); throw; }
        catch { activity.Finish(ModelPhase.Failed); throw; }
    }
    private async Task<string> RunCoreAsync(string schema, string prompt, AppSettings settings, CancellationToken ct, string purpose, string model = "gpt-6-luna", bool search = false, string? effort = null, int? timeoutSeconds = null, int? maxOutputBytes = null)
    {
        if (RunForTest is { } run)
        {
            var sent = new ModelExchange(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, purpose, model, "送信中", prompt);
            var watch = Stopwatch.StartNew(); ReportExchange(sent);
            try { var result = await run(sent, ct); ct.ThrowIfCancellationRequested(); ReportExchange(sent with { State = "成功", Response = result, Seconds = watch.Elapsed.TotalSeconds }); return result; }
            catch (Exception ex) { ReportExchange(sent with { State = ct.IsCancellationRequested ? "中断" : "失敗", Response = ex.Message, Seconds = watch.Elapsed.TotalSeconds }); throw; }
        }
        if (!settings.CodexEnabled) throw new InvalidOperationException("設定画面でLuna接続を有効にしてください。手動で登録することもできます。");
        AgentPaths.RequireLocalPath(settings.CodexExecutable);
        if (!string.Equals(Path.GetFileName(settings.CodexExecutable), "codex.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Codex CLIのcodex.exeを指定してください。");
        var id = Guid.NewGuid().ToString("N");
        var schemaPath = Path.Combine(paths.Model, id + ".schema.json");
        await File.WriteAllTextAsync(schemaPath, schema, ct);
        var output = Path.Combine(paths.Model, id + ".json");
        var start = new ProcessStartInfo(settings.CodexExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Model,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "exec", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--model", model, "--json", "--output-schema", schemaPath, "--output-last-message", output, "-c", "approval_policy=\"never\"", "-c", search ? "web_search=\"live\"" : "web_search=\"disabled\"", "-c", "features.skip_host_skill_discovery=true", "-c", "project_doc_max_bytes=0", "-c", "mcp_servers={}", "-c", "model_reasoning_effort=\"" + (effort ?? (model.Contains("sol", StringComparison.Ordinal) ? "high" : "low")) + "\"" }) start.ArgumentList.Add(arg);
        foreach (var feature in Restrictions) { start.ArgumentList.Add("--disable"); start.ArgumentList.Add(feature); }
        start.ArgumentList.Add("-");
        if (!string.IsNullOrWhiteSpace(settings.CodexHome)) start.Environment["CODEX_HOME"] = settings.CodexHome;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? (search || model.Contains("sol", StringComparison.Ordinal) ? 180 : 60)));
        using var job = new ProcessJob();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Codex CLIを起動できませんでした。");
        job.Assign(process);
        CodexUsage? usage = null;
        var stdout = DrainEventsAsync(process.StandardOutput, value => usage = usage is null ? value : new(usage.InputTokens + value.InputTokens, usage.CachedInputTokens + value.CachedInputTokens, usage.OutputTokens + value.OutputTokens));
        var stderr = DrainAsync(process.StandardError);
        CodexSendLog? log = null;
        var status = "失敗";
        var exchange = new ModelExchange(id, DateTimeOffset.UtcNow, purpose, model, "送信中", prompt);
        var exchangeWatch = Stopwatch.StartNew(); var rawResponse = "";
        try
        {
            Journal.Rollup(DateTimeOffset.UtcNow);
            log = new(id, DateTimeOffset.UtcNow, purpose, model, prompt + Environment.NewLine, schema, "送信中");
            Journal.Begin(log);
            ReportExchange(exchange);
            await process.StandardInput.WriteLineAsync(prompt.AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            job.Dispose();
            var events = await stdout; var errors = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Codex CLIが終了コード{process.ExitCode}で停止しました。ログインとLunaの利用可否を確認してください。" + (errors.Contains("not supported", StringComparison.OrdinalIgnoreCase) ? " このCLIまたはアカウントでは指定機能が利用できません。" : ""));
            if (events.Contains("\"command_execution\"", StringComparison.Ordinal) || events.Contains("\"mcp_tool_call\"", StringComparison.Ordinal) || events.Contains("\"file_change\"", StringComparison.Ordinal)) throw new InvalidOperationException("予期しないツール使用を検出したため応答を破棄しました。");
            if (!File.Exists(output) || new FileInfo(output).Length > (maxOutputBytes ?? (purpose == "文章整形" ? 512000 : 64000))) throw new InvalidOperationException("AIの応答を読み取れませんでした。");
            var result = await File.ReadAllTextAsync(output, ct); rawResponse = result; status = "成功"; return result;
        }
        catch (OperationCanceledException)
        {
            status = "中断";
            rawResponse = "Lunaへの問い合わせを中断しました。";
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw new OperationCanceledException("Lunaへの問い合わせを中断しました。", ct);
        }
        catch (Exception ex) { rawResponse = ex.Message; throw; }
        finally
        {
            job.Dispose();
            try { await stdout; await stderr; }
            finally
            {
                ReportExchange(exchange with { State = status, Response = rawResponse, Seconds = exchangeWatch.Elapsed.TotalSeconds,
                    Usage = usage is null ? null : new(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens) });
                try { if (log is not null) Journal.Complete(log with { Status = status, Usage = usage }); }
                finally { if (File.Exists(output)) File.Delete(output); if (File.Exists(schemaPath)) File.Delete(schemaPath); }
            }
        }
    }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var chars = new char[4096]; int read;
        while ((read = await reader.ReadAsync(chars)) > 0) if (result.Length < 200000) result.Append(chars, 0, Math.Min(read, 200000 - result.Length));
        return result.ToString();
    }
    private static async Task<string> DrainEventsAsync(StreamReader reader, Action<CodexUsage> usage)
    {
        var result = new StringBuilder(); string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (CodexUsage.ParseEvent(line) is { } value) usage(value);
            if (result.Length < 200000) result.AppendLine(line[..Math.Min(line.Length, 200000 - result.Length)]);
        }
        return result.ToString();
    }
}

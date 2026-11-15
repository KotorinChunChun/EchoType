using EchoType.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace EchoType.Windows;

public static class WhisperSetup
{
    private const string PythonVersion = "3.14.10";
    private const string PythonSha256 = "F54BA67C9951C3D773EA47D2BB095150C037C399F0423A968BCDA22809241784";
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static string EnvironmentRoot(AgentPaths paths) => Path.Combine(paths.Model, "whisper-venv");
    private static string EnvironmentMarker(AgentPaths paths) => Path.Combine(EnvironmentRoot(paths), "ready");
    private static string EnvironmentPython(AgentPaths paths) => Path.Combine(EnvironmentRoot(paths), "Scripts", "python.exe");
    private static string LegacyPython(AgentPaths paths) => Path.Combine(paths.RecognitionModels, "venv", "Scripts", "python.exe");
    private static readonly string[] ImportCheck = ["-I", "-c",
        "import os, sys; app_dlls = os.add_dll_directory(sys.argv[1]); import faster_whisper", AppContext.BaseDirectory];

    internal static string PythonPath(AgentPaths paths) => !paths.HasModelOverride && File.Exists(EnvironmentMarker(paths)) && File.Exists(EnvironmentPython(paths))
        ? EnvironmentPython(paths)
        : LegacyPython(paths);

    public static async Task InstallAsync(AgentPaths paths, IEnumerable<string> engines, IProgress<string>? progress, CancellationToken ct)
    {
        if (paths.HasModelOverride) throw new InvalidOperationException("参照中のモデル環境にはEchoTypeから導入できません。");
        var models = engines.Select(AsrEngines.ModelId).Distinct().ToArray();
        if (models.Length == 0 || models.Any(model => !AsrEngines.IsWhisper(model)))
            throw new ArgumentException("準備するWhisperモデルを確認してください。", nameof(engines));

        FileStream setupLock;
        try { setupLock = new(Path.Combine(paths.Root, "control", "asr-setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("音声認識の準備中です。完了後にもう一度お試しください。"); }
        using (setupLock)
        {
            progress?.Report("音声認識環境を確認しています。");
            var python = PythonPath(paths);
            if (!File.Exists(python) || await RunAsync(python, ImportCheck, paths, null, ct).ConfigureAwait(false) != 0)
                python = await InstallRuntimeAsync(paths, progress, ct).ConfigureAwait(false);

            var missing = models.Where(model => !WhisperModelStore.HasWeights(paths, model)).ToArray();
            if (missing.Length == 0) return;
            var script = Path.Combine(AppContext.BaseDirectory, "Asr", "whisper_setup.py");
            var arguments = new List<string> { "-B", script, "--fallback", WhisperModelStore.UserRoot(paths), "--legacy", Path.Combine(paths.Model, "asr") };
            if (WhisperModelStore.MachineRoot(paths) is { } machine) arguments.AddRange(["--primary", machine]);
            arguments.AddRange(missing);
            progress?.Report("モデルを準備しています。");
            var result = await RunAsync(python, arguments, paths, line =>
            {
                if (missing.Contains(line, StringComparer.Ordinal)) progress?.Report($"{AsrEngines.Get(line).Name}を準備しています。");
            }, ct).ConfigureAwait(false);
            if (result != 0 || missing.Any(model => !WhisperModelStore.HasWeights(paths, model)))
                throw new InvalidOperationException("モデルを準備できませんでした。接続と空き容量を確認して、もう一度お試しください。");
        }
    }

    private static async Task<string> InstallRuntimeAsync(AgentPaths paths, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("EchoType用のPythonを準備しています。");
        var python = await ObtainPythonAsync(paths, ct).ConfigureAwait(false);
        var root = EnvironmentRoot(paths);
        if (Directory.Exists(root)) AgentPaths.RequireLocalPath(root, true);
        var marker = EnvironmentMarker(paths);
        if (File.Exists(marker)) File.Delete(marker);
        if (await RunAsync(python, ["-I", "-m", "venv", root], paths, null, ct).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("音声認識用のPython環境を作成できませんでした。");
        var environment = EnvironmentPython(paths);
        progress?.Report("音声認識ライブラリを準備しています。");
        var requirements = Path.Combine(AppContext.BaseDirectory, "Asr", "requirements.txt");
        if (await RunAsync(environment, ["-I", "-m", "pip", "--isolated", "--disable-pip-version-check", "install", "--only-binary=:all:", "--no-cache-dir", "--index-url", "https://pypi.org/simple", "-r", requirements], paths, null, ct).ConfigureAwait(false) != 0 ||
            await RunAsync(environment, ["-I", "-m", "pip", "check"], paths, null, ct).ConfigureAwait(false) != 0 ||
            await RunAsync(environment, ImportCheck, paths, null, ct).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("音声認識ライブラリを準備できませんでした。接続を確認して、もう一度お試しください。");
        File.WriteAllText(marker, PythonVersion);
        return environment;
    }

    private static async Task<string> ObtainPythonAsync(AgentPaths paths, CancellationToken ct)
    {
        var root = Path.Combine(paths.Model, "whisper-python-" + PythonVersion);
        var python = Path.Combine(root, "tools", "python.exe");
        if (File.Exists(python))
        {
            AgentPaths.RequireLocalPath(python);
            return python;
        }
        if (Directory.Exists(root)) throw new InvalidOperationException("EchoType用のPythonが不完全です。導入先を確認してください。");
        var staging = Path.Combine(paths.Model, Path.GetRandomFileName());
        var package = staging + ".zip";
        try
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/python/{PythonVersion}/python.{PythonVersion}.nupkg";
            using var response = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var archive = new FileStream(package, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await source.CopyToAsync(archive, ct).ConfigureAwait(false);
                archive.Position = 0;
                if (Convert.ToHexString(await SHA256.HashDataAsync(archive, ct).ConfigureAwait(false)) != PythonSha256)
                    throw new InvalidDataException("取得したPythonの検証に失敗しました。");
                archive.Position = 0;
                using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
                zip.ExtractToDirectory(staging);
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(staging, "tools", "python.exe"))) throw new InvalidDataException("Pythonの配布物が不完全です。");
            Directory.Move(staging, root);
            return python;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            File.Delete(package);
        }
    }

    private static async Task<int> RunAsync(string executable, IEnumerable<string> arguments, AgentPaths paths, Action<string>? output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Root,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "PYTHONPATH", "PYTHONHOME", "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN" }) start.Environment.Remove(name);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PIP_CONFIG_FILE"] = "NUL";
        start.Environment["HF_HOME"] = Path.Combine(paths.Root, "cache", "hf-setup");
        start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        using var job = new ProcessJob();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("音声認識の準備を開始できませんでした。");
        job.Assign(process);
        using var cancel = ct.Register(job.Dispose);
        await Task.WhenAll(process.WaitForExitAsync(), DrainAsync(process.StandardOutput, output), DrainAsync(process.StandardError, null)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task DrainAsync(StreamReader reader, Action<string>? output)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) output?.Invoke(line);
    }
}

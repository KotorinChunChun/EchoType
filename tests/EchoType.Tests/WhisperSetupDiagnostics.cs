using EchoType.Core;
using EchoType.Windows;

internal static class WhisperSetupDiagnostics
{
    public static async Task<int> RunAsync(string wavePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "EchoType-test-" + Guid.NewGuid().ToString("N")[..12]);
        var paths = new AgentPaths(root);
        string[] models = ["whisper-base", "whisper-turbo"];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var token = timeout.Token;
        var wave = await File.ReadAllBytesAsync(wavePath, token);
        var progress = new Progress<string>(Console.WriteLine);
        await WhisperSetup.InstallAsync(paths, models, progress, token);
        await WhisperSetup.InstallAsync(paths, models, progress, token);
        foreach (var model in models)
        {
            if (!LocalAsrWorker.IsReady(paths, model)) throw new InvalidOperationException($"{model} の準備が完了していません。");
            using var worker = new LocalAsrWorker(paths);
            await worker.WarmupAsync(model, "cpu", new RecognitionPerformance(), token);
            var result = await worker.RecognizeAsync(model, wave, "", "cpu", token, prepared: true);
            if (!result.Text.Contains("音声認識") || !result.Text.Contains("テスト"))
                throw new InvalidOperationException($"{model} の合成音声認識結果が一致しません。");
            Console.WriteLine($"{model}: 準備・初期化・音声認識に成功しました。");
        }
        return 0;
    }
}

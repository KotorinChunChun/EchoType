using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

internal static class SpeechRuntime
{
    private const string CpuFamily = "Microsoft.Windows.AI.Speech.Preview.CPU_8wekyb3d8bbwe";
    private const string NpuFamily = "Microsoft.Windows.AI.Speech.Preview.NPU_8wekyb3d8bbwe";

    internal static IEnumerable<Package> ModelPackages() => new PackageManager().FindPackagesForUser("")
        .Where(p => p.Id.FamilyName is CpuFamily or NpuFamily);

    internal static void PreloadCpuLibraries()
    {
        // SDK 2.4.8はモデル配下のwinmlディレクトリをDLL探索に含めない。
        // OSが登録したMicrosoftパッケージの固定DLLだけを絶対パスで読み込む。
        var package = ModelPackages().SingleOrDefault(p => p.Id.FamilyName == CpuFamily);
        if (package is null) return;
        var directory = Path.Combine(package.InstalledLocation.Path, "winml");
        foreach (var name in new[] { "asrapi.dll", "asrmodelapi.dll", "cacheapi.dll", "NPUDetect.dll", "vadapi.dll" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Windows AI CPUモデルのDLLがありません: {name}");
            // モデル破棄まで保持。補助プロセス終了時にOSが解放する。
            NativeLibrary.Load(path);
        }
    }
}

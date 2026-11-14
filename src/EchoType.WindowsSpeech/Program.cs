using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Speech;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

Console.OutputEncoding = Encoding.UTF8;
var timer = Stopwatch.StartNew();
var stage = "Initialize";
try
{
    if (args.Length is < 1 or > 2 || args[0] is not ("--status" or "--install" or "--recognize" or "--diagnose"))
        throw new ArgumentException("音声認識の要求が不正です。");
    // カタログ初期化だけを行う。状態確認のたびにEPのダウンロードを要求しない。
    _ = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
    stage = "Readiness";
    if (args[0] == "--diagnose")
    {
        string capability;
        try { capability = Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("systemAIModels").CheckAccess().ToString(); }
        catch (Exception ex) { capability = $"{ex.Message} (0x{ex.HResult:X8})"; }
        Write(new { PackageIdentity = Identity(), Capability = capability, OsVersion = Environment.OSVersion.Version.ToString(),
            State = SpeechRecognitionModel.GetReadyState().ToString(), Sdk = "2.4.8-experimental", WindowsMl = "2.3.5-experimental",
            ModelPackages = SpeechRuntime.ModelPackages().Select(p => new { p.Id.Name, Version = p.Id.Version.ToString(), Path = p.InstalledLocation.Path }) });
        return 0;
    }
    if (args[0] == "--install" && SpeechRecognitionModel.GetReadyState() != AIFeatureReadyState.Ready)
    {
        stage = "Prepare";
        var ready = await SpeechRecognitionModel.EnsureReadyAsync();
        if (SpeechRecognitionModel.GetReadyState() != AIFeatureReadyState.Ready)
        {
            Write(new { State = SpeechRecognitionModel.GetReadyState().ToString(), Text = "", Error = $"Windows AI音声モデルの準備に失敗しました。{ready.Status}" + (ready.ExtendedError is { } failure ? $"（0x{failure.HResult:X8}）{failure.Message}" : "") + " CPUモデルはMicrosoft Storeの製品ID 9N5Q3M1ZZJ1Lで導入できます。", RecognitionSeconds = (double?)null, Stage = stage, PackageIdentity = Identity() });
            return 1;
        }
    }
    stage = "Readiness";
    var state = SpeechRecognitionModel.GetReadyState().ToString();
    if (args[0] != "--recognize")
    { Write(new { State = state, Text = "", Error = "", RecognitionSeconds = (double?)null, PackageIdentity = Identity() }); return 0; }
    if (state != "Ready")
    {
        Write(new { State = state, Text = "", Error = $"Windows AI音声モデルは準備未完了です（{state}）。認識は開始していません。", RecognitionSeconds = (double?)null, Stage = stage });
        return 1;
    }
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1])) throw new ArgumentException("認識する音声ファイルがありません。");
    stage = "Create";
    SpeechRuntime.PreloadCpuLibraries();
    var created = await SpeechRecognitionModel.TryCreateAsync();
    using var model = created.SpeechModel;
    if (model is null) throw created.ExtendedError ?? new InvalidOperationException("Windows AI音声モデルを生成できませんでした。");
    using var recognizer = new BatchRecognition(model);
    var loadSeconds = timer.Elapsed.TotalSeconds;
    stage = "Recognize";
    var recognition = Stopwatch.StartNew();
    var text = await recognizer.RecognizeFromFile(args[1]);
    Write(new { State = "Ready", Text = text, Error = "", RecognitionSeconds = recognition.Elapsed.TotalSeconds, LoadSeconds = loadSeconds });
    return 0;
}
catch (Exception ex)
{
    Write(new { State = "Unavailable", Text = "", Error = $"{ex.Message}（0x{ex.HResult:X8}）", RecognitionSeconds = (double?)null, Stage = stage });
    return 1;
}

static void Write(object result) => Console.WriteLine(JsonSerializer.Serialize(result));
static string? Identity()
{
    uint size = 0;
    if (GetCurrentPackageFullName(ref size, null) != 122) return null;
    var name = new StringBuilder((int)size);
    return GetCurrentPackageFullName(ref size, name) == 0 ? name.ToString() : null;
}
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

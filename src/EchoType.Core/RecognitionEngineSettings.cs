namespace EchoType.Core;

/// <summary>モデルの構築条件は起動中に変更しない。保存設定から稼働設定への投影。</summary>
public sealed class RecognitionEngineSettings(AppSettings started)
{
    public AppSettings Apply(AppSettings desired) => desired with
    {
        LocalAsrDevice = started.LocalAsrDevice,
        Performance = desired.Performance with
        {
            CpuThreads = started.Performance.CpuThreads,
            CudaCompute = started.Performance.CudaCompute,
            MaxConcurrent = started.Performance.MaxConcurrent,
            MaxResidentModels = started.Performance.MaxResidentModels
        }
    };
    public static IReadOnlyDictionary<string, object> Values(AppSettings settings) => new Dictionary<string, object>
    {
        ["Device"] = settings.LocalAsrDevice,
        ["CpuThreads"] = settings.Performance.CpuThreads,
        ["CudaCompute"] = settings.Performance.CudaCompute,
        ["MaxConcurrent"] = settings.Performance.MaxConcurrent,
        ["MaxResidentModels"] = settings.Performance.MaxResidentModels
    };
    public string[] Pending(AppSettings desired)
    {
        var actual = Values(Apply(desired));
        return Values(desired).Where(p => !Equals(p.Value, actual[p.Key])).Select(p => p.Key).ToArray();
    }
    public string Notice(AppSettings desired)
    {
        var labels = new Dictionary<string, string> { ["Device"] = "処理デバイス", ["CpuThreads"] = "CPUスレッド数", ["CudaCompute"] = "CUDA計算精度", ["MaxConcurrent"] = "同時推論数", ["MaxResidentModels"] = "常駐モデル数" };
        var pending = Pending(desired);
        if (pending.Length == 0) return "";
        var saved = Values(desired); var actual = Values(Apply(desired));
        return "再起動が必要です。再起動までは変更前の設定で動作します。" + string.Join("、", pending.Select(k => $"{labels[k]}: 稼働中 {actual[k]} → 保存済み {saved[k]}"));
    }
}

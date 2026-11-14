namespace EchoType.Core;

public enum RecognitionPurpose { Inquiry, Immediate, Full, Archive, Comparison, Warmup, BatchRetry }

public sealed record RecognitionPerformance
{
    public bool ShareResults { get; init; } = true;
    public bool ComparisonReuse { get; init; }
    public int MaxConcurrent { get; init; } = 2;
    public int MaxResidentModels { get; init; } = 3;
    public int CpuThreads { get; init; } = 2;
    public int ImmediateBeam { get; init; } = 1;
    public int FullBeam { get; init; } = 5;
    public string CudaCompute { get; init; } = "float16";
    public bool WindowedImmediate { get; init; } = true;
    public int WindowSeconds { get; init; } = 12;
    public int OverlapSeconds { get; init; } = 3;
    public bool ConditionalCorrection { get; init; }
    public bool RelevantHints { get; init; }
    public bool QuickWakeAcknowledgement { get; init; }
    public int CompleteQuestionWaitSeconds { get; init; }
    public int MaximumInquiryWaitSeconds { get; init; }
    public void Validate()
    {
        if (MaxConcurrent is < 1 or > 8 || MaxResidentModels is < 1 or > 8 || CpuThreads is < 1 or > 16 ||
            ImmediateBeam is < 1 or > 5 || FullBeam is < 1 or > 5 || CudaCompute is not ("float16" or "int8_float16") ||
            WindowSeconds is < 6 or > 30 || OverlapSeconds is < 1 or > 5 || OverlapSeconds >= WindowSeconds ||
            CompleteQuestionWaitSeconds is < 0 or > 30 || MaximumInquiryWaitSeconds is < 0 or > 30)
            throw new InvalidOperationException("認識の性能設定が範囲外です。同時数1〜8、常駐数1〜8、CPUスレッド1〜16、探索数1〜5、窓6〜30秒、重なり1〜5秒、待機0〜30秒です。");
    }
}

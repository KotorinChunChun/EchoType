namespace EchoType.Core;

/// <summary>比較の速さは実認識と補正だけで評価し、受付順・常駐状態・結果再利用に左右させない。</summary>
public static class AsrComparisonTiming
{
    public static double? Seconds(AsrResult result)
    {
        if (!result.InputVerified || !result.Succeeded || result.Reused || string.IsNullOrWhiteSpace(result.Text) ||
            result.RecognitionSeconds is not { } recognition || !double.IsFinite(recognition) || recognition <= 0)
            return null;
        // 古い履歴の総時間には待機が混ざるため、認識時間未記録時の代用にはしない。
        var correction = result.PostProcessingSeconds ?? 0;
        if (AsrEngines.HasPostProcessing(result.EngineId) && result.PostProcessingSeconds is null ||
            !double.IsFinite(correction) || correction < 0) return null;
        var seconds = recognition + correction;
        return double.IsFinite(seconds) ? seconds : null;
    }
}

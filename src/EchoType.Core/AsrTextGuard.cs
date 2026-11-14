namespace EchoType.Core;

/// <summary>短い言い直しや語彙単語自体は許容し、長く支配的な同一語句の反復だけを採用しない。</summary>
public static class AsrTextGuard
{
    public static bool IsDegenerateRepetition(string text)
    {
        var value = TranscriptionScore.Normalize(text);
        if (value.Length < 80) return false;
        for (var period = 1; period <= Math.Min(64, value.Length / 8); period++)
        {
            var run = period;
            for (var i = period; i < value.Length; i++)
            {
                run = value[i] == value[i - period] ? run + 1 : period;
                if (run >= 80 && run >= period * 8 && run >= value.Length * .8) return true;
            }
        }
        return false;
    }
    public static AsrResult Filter(AsrResult result) => FilterRepetition(InputCensorship.Filter(result));
    private static AsrResult FilterRepetition(AsrResult result) => result.Succeeded && IsDegenerateRepetition(result.Text)
        ? result with { RejectedText = result.Text, Text = "", Error = "長い同一語句の繰り返しを検出したため、この認識結果は採用しません。" } : result;
}

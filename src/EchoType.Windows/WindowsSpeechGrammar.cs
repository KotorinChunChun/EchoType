using System.Globalization;
using System.Speech.Recognition;

namespace EchoType.Windows;

internal static class WindowsSpeechGrammar
{
    public static Grammar Create(CultureInfo culture)
    {
        // DictationGrammarだと、結果イベントの前にSystem.SpeechがGetAlternatesを呼ぶ。
        // 日本語SAPIがArgumentExceptionを返すと認識用スレッドからプロセス全体が終了する。
        // 通常の文法へ自由発話を埋め込み、未使用の別候補抽出を通さず最良結果を受け取る。
        var builder = new GrammarBuilder { Culture = culture };
        builder.AppendDictation();
        return new Grammar(builder);
    }

    public static float Confidence(RecognizedPhrase phrase)
    {
        // 外側の文法規則のConfidenceは1になるので、発話の確からしさとして使わない。
        // 語ごとの平均値を上限にし、発話を100%扱いで操作受付へ渡すのを防ぐ。
        // 最小値だと一語の0で発話全体が0となり、確定文字列まで捨てられてしまう。
        if (phrase.Words.Count == 0) return 0;
        return Math.Min(Valid(phrase.Confidence), phrase.Words.Average(word => Valid(word.Confidence)));
    }

    private static float Valid(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

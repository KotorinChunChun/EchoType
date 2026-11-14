namespace EchoType.App;

internal sealed class VoiceTimingWindow : Window
{
    public VoiceTimingWindow(VoiceInputController controller)
    {
        Title = "音声入力の処理時間"; Width = 900; Height = 520; MinWidth = 600; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { Margin = new Thickness(12), IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = controller.Timings };
        foreach (var (label, property, width) in new[] { ("時刻", "Time", 90), ("処理", "Stage", 100), ("モデル・入力先", "Model", 180), ("待ち秒", "Waiting", 80), ("処理秒", "Processing", 80), ("入力トークン", "InputTokens", 100), ("出力トークン", "OutputTokens", 100), ("結果", "Result", 300) })
            grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(property) { StringFormat = property is "Waiting" or "Processing" ? "F2" : null }, Width = width });
        Content = grid;
    }
}

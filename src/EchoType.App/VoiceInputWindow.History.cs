namespace EchoType.App;

public partial class VoiceInputWindow
{
    private void UnsentDrafts_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UnsentDrafts.SelectedItem is not VoiceInputUnsentDraft entry) return;
        // 選択状態を残さず、同じ履歴も次回もう一度選べるようにする。
        UnsentDrafts.SelectedIndex = -1;
        _controller.RestoreUnsentDraft(entry);
    }

    public bool AppendUnsentDraft(string text)
    {
        if (Editor.IsReadOnly) return false;
        if (Editor.Text.Length + text.Length > Editor.MaxLength)
        {
            SetStatus("編集欄は16000文字までです。未送信テキストは履歴に残しています。");
            return false;
        }
        Editor.CaretIndex = Editor.Text.Length;
        return InsertDraft(text);
    }
}

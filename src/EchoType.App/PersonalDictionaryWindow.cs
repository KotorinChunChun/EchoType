using EchoType.Core;

namespace EchoType.App;

internal sealed class PersonalDictionaryWindow : Window
{
    public PersonalDictionaryWindow(VoiceInputController controller, string wrong = "", string correct = "")
    {
        Title = "個人辞書"; Width = 760; Height = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var store = controller.PersonalizationStore;
        var layout = new DockPanel { Margin = new Thickness(16) }; Content = layout;
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (label, property) in new[] { ("誤表記", "Wrong"), ("正表記", "Correct"), ("読み", "Reading"), ("適用先", "Scope") })
            grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        layout.Children.Add(grid);
        void Refresh() { grid.ItemsSource = store.GetPersonalWords(); controller.RefreshPersonalization(); }
        void Edit(PersonalWord? existing)
        {
            TextBox? old = null, word = null, reading = null, scope = null;
            var dialog = new EntryDialog(this, "単語の編集", () => store.SavePersonalWord(new(existing?.Id ?? Guid.NewGuid().ToString(), old!.Text.Trim(), word!.Text.Trim(), reading!.Text.Trim(), scope!.Text.Trim())));
            old = dialog.Text("誤表記", existing?.Wrong ?? wrong); word = dialog.Text("正表記", existing?.Correct ?? correct);
            reading = dialog.Text("読み", existing?.Reading ?? ""); scope = dialog.Text("適用するアプリの実行ファイル（空欄ですべて）", existing?.ApplicationPath ?? "", 1000);
            if (dialog.ShowDialog() == true) Refresh();
        }
        var add = new Button { Content = "追加" }; add.Click += (_, _) => Edit(null); buttons.Children.Add(add);
        var edit = new Button { Content = "編集" }; edit.Click += (_, _) => { if (grid.SelectedItem is PersonalWord w) Edit(w); }; buttons.Children.Add(edit);
        var delete = new Button { Content = "削除" }; delete.Click += (_, _) => { if (grid.SelectedItem is PersonalWord w) { store.DeletePersonalWord(w.Id); Refresh(); } }; buttons.Children.Add(delete);
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is PersonalWord w) Edit(w); };
        Refresh(); if (wrong.Length > 0 || correct.Length > 0) Loaded += (_, _) => Edit(null);
    }
}

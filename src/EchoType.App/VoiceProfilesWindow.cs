using EchoType.Core;

namespace EchoType.App;

internal sealed class VoiceProfilesWindow : Window
{
    public VoiceProfilesWindow(VoiceInputController controller)
    {
        Title = "アプリ別設定"; Width = 780; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var store = controller.PersonalizationStore;
        var layout = new DockPanel { Margin = new Thickness(16) }; Content = layout;
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "名前", Binding = new System.Windows.Data.Binding("Name"), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "実行ファイル", Binding = new System.Windows.Data.Binding("ApplicationPath"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); layout.Children.Add(grid);
        void Refresh() { grid.ItemsSource = store.GetVoiceProfiles(); controller.RefreshPersonalization(); }
        void Edit(VoiceApplicationProfile? existing)
        {
            var settings = existing?.Settings ?? controller.Settings;
            EntryDialog? dialog = null; TextBox? name = null, path = null, prompt = null, simple = null, vocabulary = null;
            ComboBox? format = null, effort = null; CheckBox? automatic = null, autoFormat = null, autoConfirm = null;
            dialog = new(this, "アプリ設定", () =>
            {
                var value = new VoiceApplicationProfile(path!.Text.Trim(), name!.Text.Trim(), settings with
                {
                    AutomaticInput = automatic!.IsChecked == true, AutomaticFormatting = autoFormat!.IsChecked == true,
                    AutomaticConfirm = autoConfirm!.IsChecked == true, FormattingPrompt = prompt!.Text, SimpleFormattingPrompt = simple!.Text,
                    FormattingModel = (string)format!.SelectedItem, FormattingEffort = (string)effort!.SelectedItem,
                    Recognition = settings.Recognition with { AsrVocabulary = vocabulary!.Text }
                });
                store.SaveVoiceProfile(value);
                if (existing is not null && !string.Equals(existing.ApplicationPath, value.ApplicationPath, StringComparison.OrdinalIgnoreCase)) store.DeleteVoiceProfile(existing.ApplicationPath);
            });
            name = dialog.Text("名前", existing?.Name ?? Path.GetFileNameWithoutExtension(controller.TargetApplicationPath));
            path = dialog.Text("実行ファイル", existing?.ApplicationPath ?? controller.TargetApplicationPath, 1000);
            automatic = dialog.Check("自動入力", settings.AutomaticInput); autoFormat = dialog.Check("自動整形", settings.AutomaticFormatting); autoConfirm = dialog.Check("自動決定", settings.AutomaticConfirm);
            format = dialog.Choice("整形モデル", VoiceInputSettings.FormattingModels, settings.FormattingModel);
            effort = dialog.Choice("エフォート", VoiceInputSettings.FormattingEfforts, settings.FormattingEffort);
            prompt = dialog.Text("整形方針", settings.FormattingPrompt, 8000, true); simple = dialog.Text("単純整形", settings.SimpleFormattingPrompt, 8000, true);
            vocabulary = dialog.Text("語彙ヒント", settings.Recognition.AsrVocabulary, 1000, true);
            if (dialog.ShowDialog() == true) Refresh();
        }
        var add = new Button { Content = "追加" }; add.Click += (_, _) => Edit(null); buttons.Children.Add(add);
        var edit = new Button { Content = "編集" }; edit.Click += (_, _) => { if (grid.SelectedItem is VoiceApplicationProfile p) Edit(p); }; buttons.Children.Add(edit);
        var delete = new Button { Content = "共通設定へ戻す" }; delete.Click += (_, _) => { if (grid.SelectedItem is VoiceApplicationProfile p) { store.DeleteVoiceProfile(p.ApplicationPath); Refresh(); } }; buttons.Children.Add(delete);
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is VoiceApplicationProfile p) Edit(p); };
        Refresh();
    }
}

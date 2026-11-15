using EchoType.Core;

namespace EchoType.App;

/// <summary>通常録音の設定と分離した、音声入力専用の設定。</summary>
public sealed class VoiceInputSettingsWindow : Window
{
    public VoiceInputSettingsWindow(VoiceInputController controller, string[] models)
    {
        Title = "音声入力の設定"; Width = 680; Height = 650; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = true;
        var settings = controller.Settings;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var layout = new DockPanel(); Content = layout;
        var footer = new StackPanel { Margin = new Thickness(20, 6, 20, 10) }; DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        var enabled = new CheckBox { Content = "録音キーを有効にする", IsChecked = settings.Enabled };
        panel.Children.Add(enabled);
        var personalize = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(personalize);
        var profile = new Button { Content = "アプリ別設定", Margin = new Thickness(0, 0, 8, 0) };
        profile.Click += (_, _) => { new VoiceProfilesWindow(controller) { Owner = this }.ShowDialog(); DialogResult = false; };
        var dictionary = new Button { Content = "個人辞書" };
        dictionary.Click += (_, _) => { new PersonalDictionaryWindow(controller) { Owner = this }.ShowDialog(); controller.RefreshPersonalization(); };
        personalize.Children.Add(profile); personalize.Children.Add(dictionary);
        Title = "音声入力の設定 — " + controller.ProfileName;
        var keysPanel = new WrapPanel(); panel.Children.Add(keysPanel);
        var shortcut = settings.GetRecordingShortcut();
        var sideChoices = new[]
        {
            new { Label = "不要", Value = VoiceModifierSide.None }, new { Label = "左", Value = VoiceModifierSide.Left },
            new { Label = "右", Value = VoiceModifierSide.Right }, new { Label = "左右どちらか", Value = VoiceModifierSide.Either },
            new { Label = "左右両方", Value = VoiceModifierSide.Both }
        };
        ComboBox KeyField(string label, System.Collections.IEnumerable choices, object selection)
        {
            var field = new StackPanel { Margin = new Thickness(0, 4, 12, 4) };
            var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = selection, MinWidth = 90 };
            field.Children.Add(new Label { Content = label, Target = combo });
            field.Children.Add(combo);
            keysPanel.Children.Add(field);
            return combo;
        }
        var controlKey = KeyField("Ctrl", sideChoices, shortcut.Control);
        var shiftKey = KeyField("Shift", sideChoices, shortcut.Shift);
        var altKey = KeyField("Alt", sideChoices, shortcut.Alt);
        var windowsKey = KeyField("Windows", sideChoices, shortcut.Windows);
        var mainKeyChoices = Enumerable.Range('A', 26).Concat(Enumerable.Range(0x70, 24)).Prepend(0)
            .Select(key => new { Label = VoiceRecordingShortcut.MainKeyName(key), Value = key }).ToArray();
        var mainKey = KeyField("主キー", mainKeyChoices, shortcut.MainKey);
        TextBox Field(string label, string value, int max = 0)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) });
            var box = new TextBox { Text = value, MinHeight = 30, MaxLength = max };
            panel.Children.Add(box); return box;
        }
        var seconds = Field("最大録音秒数（2〜300秒）", settings.MaximumSeconds.ToString());
        var gap = Field("入力欄との間隔（0〜200）", settings.TargetGap.ToString());
        System.Windows.Automation.AutomationProperties.SetAutomationId(gap, "VoiceTargetGap");
        panel.Children.Add(new TextBlock { Text = "認識デバイス", Margin = new Thickness(0, 12, 0, 4) });
        var compute = new ComboBox { ItemsSource = new[] { "cpu", "cuda" }, SelectedItem = settings.Recognition.LocalAsrDevice, MinHeight = 30 };
        panel.Children.Add(compute);
        var restart = new TextBlock { Text = controller.EngineRestartNotice, Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
        panel.Children.Add(restart);
        compute.SelectionChanged += (_, _) => restart.Text = compute.SelectedItem as string == settings.Recognition.LocalAsrDevice ? controller.EngineRestartNotice : "変更は保存後、次回のアプリ起動時に反映します。それまでは変更前の設定で動作します。";
        panel.Children.Add(new TextBlock { Text = "途中認識の確定モデル（速報はWindowsで即時表示）", Margin = new Thickness(0, 12, 0, 4) });
        var liveEngine = new ComboBox { ItemsSource = VoiceInputSettings.LiveEngines,
            DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = settings.LiveEngine, MinHeight = 30 };
        panel.Children.Add(liveEngine);
        var length = new CheckBox { Content = "Windowsとの文字数差を検査", IsChecked = settings.Recognition.AsrLengthCheckEnabled, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(length);
        var prompt = Field("プロンプト整形プロンプト", settings.FormattingPrompt, 8000);
        prompt.Height = 160; prompt.AcceptsReturn = true; prompt.TextWrapping = TextWrapping.Wrap; prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var defaults = new Button { Content = "整形プロンプトを既定に戻す", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        defaults.Click += (_, _) => prompt.Text = VoiceInputSettings.DefaultFormattingPrompt; panel.Children.Add(defaults);
        var simple = Field("単純整形のプロンプト", settings.SimpleFormattingPrompt, 8000);
        simple.Height = 100; simple.AcceptsReturn = true; simple.TextWrapping = TextWrapping.Wrap; simple.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var endings = Field("句点を補う終了文字列（1行1語・追加／削除できます）", string.Join(Environment.NewLine, settings.SentenceEndings), 16000);
        endings.Height = 120; endings.AcceptsReturn = true; endings.TextWrapping = TextWrapping.Wrap; endings.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) }; footer.Children.Add(status);
        var save = new Button { Content = "保存して閉じる", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 6, 14, 6) };
        save.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(seconds.Text, out var maximum)) throw new InvalidOperationException("録音秒数は整数で指定してください。");
                if (!int.TryParse(gap.Text, out var targetGap)) throw new InvalidOperationException("入力欄との間隔は整数で指定してください。");
                controller.SaveSettings(controller.Settings with
                {
                    Enabled = enabled.IsChecked == true, MaximumSeconds = maximum, Models = models, LiveEngine = (string)liveEngine.SelectedValue,
                    TargetGap = targetGap,
                    FormattingPrompt = prompt.Text, SimpleFormattingPrompt = simple.Text,
                    RecordingShortcut = new((VoiceModifierSide)controlKey.SelectedValue, (VoiceModifierSide)shiftKey.SelectedValue,
                        (VoiceModifierSide)altKey.SelectedValue, (VoiceModifierSide)windowsKey.SelectedValue, (int)mainKey.SelectedValue),
                    SentenceEndings = endings.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray(),
                    Recognition = controller.Settings.Recognition with
                    {
                        LocalAsrDevice = (string)compute.SelectedItem, AsrLengthCheckEnabled = length.IsChecked == true,
                        CloudTranscription = false
                    }
                });
                DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        footer.Children.Add(save);
    }
}

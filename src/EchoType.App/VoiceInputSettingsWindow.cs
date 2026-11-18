using EchoType.Core;

namespace EchoType.App;

/// <summary>通常録音の設定と分離した、音声入力専用の設定。</summary>
public sealed partial class VoiceInputSettingsWindow : Window
{
    public VoiceInputSettingsWindow(VoiceInputController controller)
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1120, work.Width); Height = Math.Min(620, work.Height); MinWidth = Math.Min(820, work.Width); MinHeight = Math.Min(440, work.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = true;
        SourceInitialized += (_, _) => VoiceInputWindow.RemoveMinimizeButton(this);
        var settings = controller.Settings.MigrateSpeechConnections();
        var layout = new DockPanel { Margin = new Thickness(12, 8, 12, 8) }; Content = layout;
        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var columns = new Grid();
        var viewport = new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetAutomationId(viewport, "VoiceSettingsViewport");
        layout.Children.Add(viewport);
        StackPanel Column(double width)
        {
            var index = columns.ColumnDefinitions.Count;
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            var column = new StackPanel { Margin = new Thickness(index == 0 ? 0 : 12, 0, 0, 0) };
            Grid.SetColumn(column, index); columns.Children.Add(column);
            return column;
        }
        var panel = Column(.85);
        var speechPanel = Column(1.35);
        var formattingPanel = Column(1);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        var enabled = new CheckBox { Content = "録音キーを有効にする", IsChecked = settings.Enabled, Margin = new Thickness(0, 4, 0, 6) };
        panel.Children.Add(enabled);
        var personalize = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) }; panel.Children.Add(personalize);
        var profile = SettingsButton("アプリ別設定");
        profile.Click += (_, _) => { new VoiceProfilesWindow(controller) { Owner = this }.ShowDialog(); DialogResult = false; };
        var dictionary = SettingsButton("個人辞書");
        dictionary.Click += (_, _) => { new PersonalDictionaryWindow(controller) { Owner = this }.ShowDialog(); controller.RefreshPersonalization(); };
        personalize.Children.Add(profile); personalize.Children.Add(dictionary);
        Title = WindowTitle.Create("音声入力の設定 " + controller.ProfileName);
        var keysPanel = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 }; panel.Children.Add(keysPanel);
        var shortcut = settings.GetRecordingShortcut();
        var sideChoices = new[]
        {
            new { Label = "不要", Value = VoiceModifierSide.None }, new { Label = "左", Value = VoiceModifierSide.Left },
            new { Label = "右", Value = VoiceModifierSide.Right }, new { Label = "左右どちらか", Value = VoiceModifierSide.Either },
            new { Label = "左右両方", Value = VoiceModifierSide.Both }
        };
        ComboBox KeyField(string label, System.Collections.IEnumerable choices, object selection)
        {
            var field = new StackPanel { Margin = new Thickness(0, 2, keysPanel.Children.Count % 2 == 0 ? 8 : 0, 2) };
            var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = selection, MinHeight = 27 };
            field.Children.Add(new Label { Content = label, Target = combo, Padding = new Thickness(0, 1, 0, 2) });
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
        TextBox Field(Panel column, string label, string value, int max = 0)
        {
            column.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 2) });
            var box = new TextBox { Text = value, MinHeight = 30, MaxLength = max, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4) };
            column.Children.Add(box); return box;
        }
        var seconds = Field(panel, "最大録音秒数（2〜300秒）", settings.MaximumSeconds.ToString());
        var gap = Field(panel, "入力欄との間隔（0〜200）", settings.TargetGap.ToString());
        System.Windows.Automation.AutomationProperties.SetAutomationId(gap, "VoiceTargetGap");
        var readSpeech = AddSpeechSettings(speechPanel, settings);
        var prompt = Field(formattingPanel, "整形プロンプト", settings.FormattingPrompt, 8000);
        prompt.Height = 125; prompt.AcceptsReturn = true; prompt.TextWrapping = TextWrapping.Wrap; prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var defaults = SettingsButton("既定に戻す");
        defaults.ToolTip = "整形プロンプトを既定に戻す";
        defaults.Click += (_, _) => prompt.Text = VoiceInputSettings.DefaultFormattingPrompt; formattingPanel.Children.Add(defaults);
        var simple = Field(formattingPanel, "単純整形のプロンプト", settings.SimpleFormattingPrompt, 8000);
        simple.Height = 105; simple.AcceptsReturn = true; simple.TextWrapping = TextWrapping.Wrap; simple.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var endings = Field(formattingPanel, "句点を補う終了文字列", string.Join(Environment.NewLine, settings.SentenceEndings), 16000);
        endings.Height = 105; endings.ToolTip = "1行に1語ずつ入力"; endings.AcceptsReturn = true; endings.TextWrapping = TextWrapping.Wrap; endings.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var save = SettingsButton("保存して閉じる");
        save.Margin = new Thickness(0);
        DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save); footer.Children.Add(status);
        save.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(seconds.Text, out var maximum)) throw new InvalidOperationException("録音秒数は整数で指定してください。");
                if (!int.TryParse(gap.Text, out var targetGap)) throw new InvalidOperationException("入力欄との間隔は整数で指定してください。");
                controller.SaveSettings(readSpeech(controller.Settings) with
                {
                    Enabled = enabled.IsChecked == true, MaximumSeconds = maximum,
                    TargetGap = targetGap,
                    FormattingPrompt = prompt.Text, SimpleFormattingPrompt = simple.Text,
                    RecordingShortcut = new((VoiceModifierSide)controlKey.SelectedValue, (VoiceModifierSide)shiftKey.SelectedValue,
                        (VoiceModifierSide)altKey.SelectedValue, (VoiceModifierSide)windowsKey.SelectedValue, (int)mainKey.SelectedValue),
                    SentenceEndings = endings.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray()
                });
                DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
    }
    private static Button SettingsButton(string label) => new()
    {
        Content = label, Width = 112, Height = 32, MinHeight = 32, Padding = new Thickness(4, 2, 4, 2),
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 6, 4)
    };
}

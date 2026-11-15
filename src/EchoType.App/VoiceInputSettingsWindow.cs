using EchoType.Core;

namespace EchoType.App;

/// <summary>通常録音の設定と分離した、音声入力専用の設定。</summary>
public sealed class VoiceInputSettingsWindow : Window
{
    public VoiceInputSettingsWindow(VoiceInputController controller)
    {
        Title = "音声入力の設定"; Width = 680; Height = 650; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = true;
        SourceInitialized += (_, _) => VoiceInputWindow.RemoveMinimizeButton(this);
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
        var finalSpeech = SpeechFields("確定モデル", settings.FinalSpeech());
        var liveEnabled = new CheckBox { Content = "即時表示", IsChecked = settings.LiveDisplayEnabled, Margin = new Thickness(0, 14, 0, 4) };
        panel.Children.Add(liveEnabled);
        var liveSpeech = SpeechFields("即時表示モデル", settings.PreviewSpeech());
        void UpdateLive() => liveSpeech.Panel.Visibility = liveEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        liveEnabled.Checked += (_, _) => UpdateLive();
        liveEnabled.Unchecked += (_, _) => UpdateLive();
        UpdateLive();

        (StackPanel Panel, Func<ExternalSpeechSettings> Read) SpeechFields(string name, ExternalSpeechSettings value)
        {
            var fields = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(fields);
            var modes = new[]
            {
                new { Name = "このPCの speech.cpp サーバー", Mode = ExternalSpeechMode.SpeechCpp },
                new { Name = "このPCの TranscriptionServer", Mode = ExternalSpeechMode.TranscriptionServer },
                new { Name = "外部サーバー（APIキーなし）", Mode = ExternalSpeechMode.Server },
                new { Name = "外部API（APIキーあり）", Mode = ExternalSpeechMode.ApiKey }
            };
            var mode = new ComboBox { ItemsSource = modes, DisplayMemberPath = "Name", SelectedValuePath = "Mode", SelectedValue = value.Mode, MinHeight = 30 };
            fields.Children.Add(new Label { Content = name + "の接続方式", Target = mode });
            fields.Children.Add(mode);
            var endpoint = new TextBox { Text = value.Endpoint, MaxLength = 2048, MinHeight = 30 };
            fields.Children.Add(new Label { Content = "文字起こしAPIのURL", Target = endpoint });
            fields.Children.Add(endpoint);
            var model = new TextBox { Text = value.Model, MaxLength = 200, MinHeight = 30 };
            var modelLabel = new Label { Content = name + "の名前", Target = model };
            fields.Children.Add(modelLabel);
            fields.Children.Add(model);
            var modelList = new ComboBox
            {
                DisplayMemberPath = nameof(Windows.TranscriptionModel.Label),
                SelectedValuePath = nameof(Windows.TranscriptionModel.Id),
                MinHeight = 30
            };
            fields.Children.Add(modelList);
            var reload = new Button { Content = "モデル一覧を更新", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var listStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(reload);
            fields.Children.Add(listStatus);
            var key = new PasswordBox { Password = value.ApiKey, MaxLength = 4096, MinHeight = 30 };
            var keyLabel = new Label { Content = "APIキー", Target = key };
            fields.Children.Add(keyLabel);
            fields.Children.Add(key);
            var lastMode = value.Mode;
            var otherEndpoint = value.Mode == ExternalSpeechMode.TranscriptionServer ? ExternalSpeechSettings.SpeechCppDefault.Endpoint : value.Endpoint;
            var serverEndpoint = value.Mode == ExternalSpeechMode.TranscriptionServer ? value.Endpoint : ExternalSpeechSettings.TranscriptionServerDefault.Endpoint;
            CancellationTokenSource? pendingList = null;
            bool UsesTranscriptionServer() => (ExternalSpeechMode)mode.SelectedValue == ExternalSpeechMode.TranscriptionServer;
            void UpdateFields()
            {
                var server = UsesTranscriptionServer();
                modelList.Visibility = reload.Visibility = listStatus.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
                model.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
                modelLabel.Target = server ? modelList : model;
                key.Visibility = keyLabel.Visibility = (ExternalSpeechMode)mode.SelectedValue == ExternalSpeechMode.ApiKey ? Visibility.Visible : Visibility.Collapsed;
            }
            ExternalSpeechSettings Read() => new() { Mode = (ExternalSpeechMode)mode.SelectedValue, Endpoint = endpoint.Text.Trim(),
                Model = UsesTranscriptionServer() ? modelList.SelectedValue as string ?? "" : model.Text.Trim(),
                ApiKey = (ExternalSpeechMode)mode.SelectedValue == ExternalSpeechMode.ApiKey ? key.Password : "" };
            void ResetList()
            {
                pendingList?.Cancel();
                pendingList = null;
                modelList.ItemsSource = null;
                if (UsesTranscriptionServer() && value.Mode == ExternalSpeechMode.TranscriptionServer &&
                    endpoint.Text.Trim() == value.Endpoint && value.Model.Length > 0)
                {
                    modelList.ItemsSource = new[] { new Windows.TranscriptionModel(value.Model, value.Model) };
                    modelList.SelectedValue = value.Model;
                }
                listStatus.Text = "";
            }
            async Task RefreshListAsync()
            {
                if (!UsesTranscriptionServer() || !fields.IsVisible) return;
                pendingList?.Cancel();
                using var current = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                pendingList = current;
                var selected = Read();
                listStatus.Text = "モデル一覧を取得中…";
                try
                {
                    var models = await Windows.ExternalSpeechRecognizer.ListTranscriptionModelsAsync(selected, current.Token);
                    if (pendingList != current) return;
                    var savedUnavailable = selected.Model.Length > 0 && selected.Mode == value.Mode &&
                        selected.Endpoint == value.Endpoint && selected.Model == value.Model && models.All(m => m.Id != selected.Model);
                    if (savedUnavailable)
                        models = [.. models, new Windows.TranscriptionModel(value.Model, $"{value.Model}（保存済み・現在の一覧にありません）")];
                    modelList.ItemsSource = models;
                    modelList.SelectedValue = selected.Model;
                    listStatus.Text = models.Length == 0 ? "選択できる文字起こしモデルがありません。" :
                        savedUnavailable ? "保存済みのモデルが一覧にありません。利用可能なモデルを選び直してください。" : "";
                }
                catch (OperationCanceledException)
                {
                    if (pendingList == current) listStatus.Text = "モデル一覧の取得がタイムアウトしました。";
                }
                catch (InvalidOperationException ex)
                {
                    if (pendingList == current) listStatus.Text = ex.Message;
                }
                finally { if (pendingList == current) pendingList = null; }
            }
            endpoint.TextChanged += (_, _) => ResetList();
            mode.SelectionChanged += async (_, _) =>
            {
                if (UsesTranscriptionServer())
                {
                    otherEndpoint = endpoint.Text;
                    endpoint.Text = serverEndpoint;
                }
                else if (lastMode == ExternalSpeechMode.TranscriptionServer)
                {
                    serverEndpoint = endpoint.Text;
                    endpoint.Text = otherEndpoint;
                }
                lastMode = (ExternalSpeechMode)mode.SelectedValue;
                ResetList();
                UpdateFields();
                await RefreshListAsync();
            };
            fields.IsVisibleChanged += async (_, _) =>
            {
                if (fields.IsVisible) await RefreshListAsync();
                else { pendingList?.Cancel(); pendingList = null; }
            };
            reload.Click += async (_, _) => await RefreshListAsync();
            Closed += (_, _) => { pendingList?.Cancel(); pendingList = null; };
            UpdateFields();
            ResetList();

            var check = new Button { Content = "接続テスト", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var checkStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(check);
            fields.Children.Add(checkStatus);
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                checkStatus.Text = "確認中…";
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                void Cancel(object? sender, EventArgs args) => cancellation.Cancel();
                Closed += Cancel;
                try
                {
                    var selected = Read();
                    checkStatus.Foreground = System.Windows.Media.Brushes.DarkSlateGray;
                    checkStatus.Text = await Windows.ExternalSpeechRecognizer.CheckAsync(selected, cancellation.Token) + "  " + selected.Model;
                }
                catch (OperationCanceledException) { checkStatus.Text = "接続テストがタイムアウトまたは中止されました。"; }
                catch (Exception ex)
                {
                    checkStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
                    checkStatus.Text = ex.Message;
                }
                finally { Closed -= Cancel; check.IsEnabled = true; }
            };
            return (fields, Read);
        }
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
                var final = finalSpeech.Read();
                if (final.Mode == ExternalSpeechMode.TranscriptionServer && final.Model.Length == 0) final.ValidateAddress();
                else if (final != ExternalSpeechSettings.SpeechCppDefault) final.ValidateEndpoint();
                var live = liveSpeech.Read();
                if (liveEnabled.IsChecked == true) live.ValidateEndpoint();
                controller.SaveSettings(controller.Settings with
                {
                    Enabled = enabled.IsChecked == true, MaximumSeconds = maximum,
                    ExternalSpeech = final == ExternalSpeechSettings.SpeechCppDefault ? new() : final,
                    LiveDisplayEnabled = liveEnabled.IsChecked == true,
                    LiveSpeech = live,
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
        footer.Children.Add(save);
    }
}

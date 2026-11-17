using EchoType.Core;
using EchoType.Windows;

namespace EchoType.App;

public sealed partial class VoiceInputSettingsWindow
{
    private enum ConnectionKind { TranscriptionServer, SpeechCpp, ExternalServer, ExternalApi }

    private Func<VoiceInputSettings, VoiceInputSettings> AddSpeechSettings(Panel column, VoiceInputSettings settings)
    {
        var connections = settings.SpeechConnections.ToList();
        var manage = new ComboBox { DisplayMemberPath = nameof(SpeechConnection.Name), SelectedValuePath = nameof(SpeechConnection.Id), MinHeight = 30 };
        AddLabel(column, "登録済みの接続", manage);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        column.Children.Add(actions);
        var add = SettingsButton("追加");
        var edit = SettingsButton("編集");
        var remove = SettingsButton("削除");
        foreach (var button in new[] { add, edit, remove })
        {
            button.Margin = new Thickness(0, 0, 4, 2);
            actions.Children.Add(button);
        }
        manage.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = manage.SelectedItem is SpeechConnection;
        var final = AddSpeechRole(column, "確定", settings.FinalSelection, connections);
        var liveEnabled = new CheckBox { Content = "即時表示", IsChecked = settings.LiveDisplayEnabled, Margin = new Thickness(0, 6, 0, 0) };
        column.Children.Add(liveEnabled);
        var preview = AddSpeechRole(column, "即時表示", settings.PreviewSelection, connections);
        void UpdatePreview() => preview.Panel.Visibility = liveEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        liveEnabled.Checked += (_, _) => UpdatePreview();
        liveEnabled.Unchecked += (_, _) => UpdatePreview();
        UpdatePreview();

        void Refresh(Guid? selected)
        {
            manage.ItemsSource = connections.ToArray();
            manage.SelectedValue = selected;
            edit.IsEnabled = remove.IsEnabled = manage.SelectedItem is SpeechConnection;
            final.Refresh(); preview.Refresh();
        }
        void OpenEditor(SpeechConnection? original)
        {
            TextBox name = null!, endpoint = null!;
            ComboBox kind = null!;
            PasswordBox key = null!;
            SpeechConnection? updated = null;
            var id = original?.Id ?? Guid.NewGuid();
            string Label(ConnectionKind value) => value switch
            {
                ConnectionKind.TranscriptionServer => "TranscriptionServer",
                ConnectionKind.SpeechCpp => "speech.cpp",
                ConnectionKind.ExternalServer => "外部サーバー（APIキーなし）",
                _ => "外部API（APIキーあり）"
            };
            var modes = Enum.GetValues<ConnectionKind>().ToDictionary(Label);
            var current = original?.Settings.Mode switch
            {
                ExternalSpeechMode.SpeechCpp => ConnectionKind.SpeechCpp,
                ExternalSpeechMode.Server => ConnectionKind.ExternalServer,
                ExternalSpeechMode.ApiKey => ConnectionKind.ExternalApi,
                _ => ConnectionKind.TranscriptionServer
            };
            var dialog = new EntryDialog(this, original is null ? "接続の追加" : "接続の編集", () =>
            {
                var mode = modes[(string)kind.SelectedItem] switch
                {
                    ConnectionKind.TranscriptionServer => ExternalSpeechMode.TranscriptionServer,
                    ConnectionKind.SpeechCpp => ExternalSpeechMode.SpeechCpp,
                    ConnectionKind.ExternalServer => ExternalSpeechMode.Server,
                    _ => ExternalSpeechMode.ApiKey
                };
                updated = new(id, name.Text.Trim(), new()
                {
                    Mode = mode, Endpoint = endpoint.Text.Trim(), ApiKey = mode == ExternalSpeechMode.ApiKey ? key.Password : ""
                });
                updated.Validate();
            }) { Height = 340 };
            name = dialog.Text("接続名", original?.Name ?? "");
            kind = dialog.Choice("接続方式", modes.Keys, Label(current));
            endpoint = dialog.Text("文字起こしAPIのURL", original?.Settings.Endpoint ?? ExternalSpeechSettings.TranscriptionServerDefault.Endpoint, 2048);
            var password = dialog.Password("APIキー", original?.Settings.ApiKey ?? "");
            key = password.Box;
            void UpdateKey() => key.Visibility = password.Label.Visibility =
                modes[(string)kind.SelectedItem] == ConnectionKind.ExternalApi ? Visibility.Visible : Visibility.Collapsed;
            kind.SelectionChanged += (_, _) =>
            {
                var next = modes[(string)kind.SelectedItem];
                var previousDefault = DefaultUrl(current);
                if (endpoint.Text.Length == 0 || endpoint.Text == previousDefault) endpoint.Text = DefaultUrl(next);
                current = next;
                UpdateKey();
            };
            string DefaultUrl(ConnectionKind value) => value switch
            {
                ConnectionKind.TranscriptionServer => ExternalSpeechSettings.TranscriptionServerDefault.Endpoint,
                ConnectionKind.SpeechCpp => ExternalSpeechSettings.SpeechCppDefault.Endpoint,
                _ => ""
            };
            UpdateKey();
            if (dialog.ShowDialog() != true || updated is null) return;
            if (original is null) connections.Add(updated);
            else connections[connections.IndexOf(original)] = updated;
            Refresh(updated.Id);
        }
        add.Click += (_, _) => OpenEditor(null);
        edit.Click += (_, _) => { if (manage.SelectedItem is SpeechConnection selected) OpenEditor(selected); };
        remove.Click += (_, _) =>
        {
            if (manage.SelectedItem is not SpeechConnection selected) return;
            connections.Remove(selected);
            Refresh(null);
        };
        Refresh(connections.FirstOrDefault()?.Id);
        return source => source with
        {
            ExternalSpeech = new(), LiveSpeech = new(), SpeechConnections = connections.ToArray(),
            FinalSelection = final.Read(), PreviewSelection = preview.Read(), LiveDisplayEnabled = liveEnabled.IsChecked == true
        };
    }

    private (StackPanel Panel, Func<SpeechSelection?> Read, Action Refresh) AddSpeechRole(
        Panel column, string name, SpeechSelection? initial, List<SpeechConnection> connections)
    {
        var fields = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        column.Children.Add(fields);
        var choice = new ComboBox { DisplayMemberPath = nameof(SpeechConnection.Name), SelectedValuePath = nameof(SpeechConnection.Id), MinHeight = 30 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(choice, name == "確定" ? "FinalSpeechConnection" : "PreviewSpeechConnection");
        AddLabel(fields, name + "の接続", choice);
        var model = new TextBox { MaxLength = 200, MinHeight = 28 };
        var modelLabel = AddLabel(fields, name + "モデル", model);
        var modelList = new ComboBox { DisplayMemberPath = nameof(TranscriptionModel.Label), SelectedValuePath = nameof(TranscriptionModel.Id), MinHeight = 28 };
        fields.Children.Add(modelList);
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var refresh = SettingsButton("モデル一覧を更新");
        var check = SettingsButton("接続テスト");
        refresh.Margin = new Thickness(0, 0, 4, 2);
        buttons.Children.Add(refresh); buttons.Children.Add(check); fields.Children.Add(buttons);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        fields.Children.Add(status);
        var current = initial?.ConnectionId ?? Guid.Empty;
        var selections = new Dictionary<Guid, string>();
        if (initial is not null) selections[initial.ConnectionId] = initial.Model;
        CancellationTokenSource? pending = null;
        var updating = false;
        SpeechConnection? Selected() => choice.SelectedItem as SpeechConnection is { } item && item.Id != Guid.Empty ? item : null;
        bool UsesList() => Selected()?.Settings.Mode == ExternalSpeechMode.TranscriptionServer;
        string ReadModel() => UsesList() ? modelList.SelectedValue as string ?? "" : model.Text.Trim();
        SpeechSelection? Read() => Selected() is { } selected ? new(selected.Id, ReadModel()) : null;
        void CancelRequest() { pending?.Cancel(); pending = null; }
        void ShowFields()
        {
            CancelRequest();
            status.Text = "";
            var selected = Selected();
            var editable = selected is not null && selected.Id != SpeechConnection.WindowsDebug.Id;
            modelLabel.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
            model.Visibility = editable && !UsesList() ? Visibility.Visible : Visibility.Collapsed;
            modelList.Visibility = refresh.Visibility = UsesList() ? Visibility.Visible : Visibility.Collapsed;
            check.Visibility = selected is not null ? Visibility.Visible : Visibility.Collapsed;
            modelLabel.Target = UsesList() ? modelList : model;
            var saved = selections.GetValueOrDefault(current, "");
            model.Text = saved;
            modelList.ItemsSource = saved.Length == 0 ? [] : new[] { new TranscriptionModel(saved, saved) };
            modelList.SelectedValue = saved;
        }
        async Task RunRequestAsync(bool list)
        {
            if (!fields.IsVisible || Selected() is not { } selected || list && !UsesList()) return;
            CancelRequest();
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(list ? 8 : 15));
            pending = request;
            var requested = selected.Settings with { Model = ReadModel() };
            status.Text = list ? "モデル一覧を取得中…" : "確認中…";
            try
            {
                if (list)
                {
                    var models = await ExternalSpeechRecognizer.ListTranscriptionModelsAsync(requested, request.Token);
                    if (pending != request) return;
                    var modelId = ReadModel();
                    var missing = modelId.Length > 0 && models.All(item => item.Id != modelId);
                    if (missing) models = [.. models, new(modelId, $"{modelId}（現在の一覧にありません）")];
                    modelList.ItemsSource = models;
                    modelList.SelectedValue = modelId;
                    status.Text = missing ? "選択したモデルが一覧にありません。" :
                        models.Length == 0 ? "選択できる文字起こしモデルがありません。" : "";
                }
                else
                {
                    var message = await ExternalSpeechRecognizer.CheckAsync(requested, request.Token);
                    if (pending == request) status.Text = message;
                }
            }
            catch (OperationCanceledException) { if (pending == request) status.Text = "確認がタイムアウトしました。"; }
            catch (Exception ex) { if (pending == request) status.Text = ex.Message; }
            finally { if (pending == request) pending = null; }
        }
        choice.SelectionChanged += async (_, _) =>
        {
            if (updating) return;
            if (current != Guid.Empty)
                selections[current] = modelList.Visibility == Visibility.Visible ? modelList.SelectedValue as string ?? "" : model.Text.Trim();
            current = Selected()?.Id ?? Guid.Empty;
            ShowFields();
            await RunRequestAsync(list: true);
        };
        fields.IsVisibleChanged += async (_, _) =>
        {
            if (fields.IsVisible) await RunRequestAsync(list: true);
            else CancelRequest();
        };
        refresh.Click += async (_, _) => await RunRequestAsync(list: true);
        check.Click += async (_, _) => await RunRequestAsync(list: false);
        Closed += (_, _) => CancelRequest();
        void Refresh()
        {
            if (Selected() is not null) selections[current] = ReadModel();
            updating = true;
            choice.ItemsSource = new[] { new SpeechConnection(Guid.Empty, "未選択", new()), SpeechConnection.WindowsDebug }.Concat(connections).ToArray();
            if (current != SpeechConnection.WindowsDebug.Id && connections.All(item => item.Id != current)) current = Guid.Empty;
            choice.SelectedValue = current;
            updating = false;
            ShowFields();
            _ = RunRequestAsync(list: true);
        }
        return (fields, Read, Refresh);
    }

    private static Label AddLabel(Panel panel, string text, Control target)
    {
        var label = new Label { Content = text, Target = target, Padding = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(label);
        panel.Children.Add(target);
        return label;
    }
}

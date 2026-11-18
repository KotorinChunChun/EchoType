using EchoType.Core;
using EchoType.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace EchoType.App;

public sealed partial class VoiceInputSettingsWindow
{
    private Func<VoiceInputSettings, VoiceInputSettings> AddSpeechSettings(Panel column, VoiceInputSettings settings)
    {
        var connections = settings.SpeechConnections.ToList();
        var models = settings.SpeechModels.ToList();
        var final = settings.FinalSelection;
        var preview = settings.PreviewSelection;
        var live = new CheckBox { Content = "即時表示を有効にする", IsChecked = settings.LiveDisplayEnabled, Margin = new Thickness(0, 4, 0, 6) };
        column.Children.Add(live);

        Grid Row()
        {
            var grid = new Grid { MinHeight = 28 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1.5, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
            return grid;
        }
        void Cell(Grid grid, int index, UIElement element)
        {
            Grid.SetColumn(element, index);
            grid.Children.Add(element);
        }
        TextBlock Caption(string text, bool header = false, bool center = false) => new()
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = center ? new Thickness(0) : new Thickness(6, 0, 4, 0),
            TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = text
        };
        var headings = Row();
        headings.Background = Brushes.WhiteSmoke;
        Cell(headings, 0, Caption("登録済みモデル", header: true));
        Cell(headings, 1, Caption("接続名", header: true));
        Cell(headings, 2, Caption("確定", header: true, center: true));
        Cell(headings, 3, Caption("即時表示", header: true, center: true));
        column.Children.Add(headings);

        var rows = new StackPanel();
        AutomationProperties.SetAutomationId(rows, "RegisteredSpeechModels");
        var scroll = new ScrollViewer { Content = rows, MinHeight = 106, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.ScrollChanged += (_, _) => headings.Margin = new Thickness(1, 0,
            scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 1 + SystemParameters.VerticalScrollBarWidth : 1, 0);
        column.Children.Add(new Border { Child = scroll, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) });
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        column.Children.Add(actions);
        var manage = SettingsButton("接続・モデル管理");
        var checkFinal = SettingsButton("確定をテスト");
        var checkPreview = SettingsButton("即時表示をテスト");
        foreach (var button in new[] { manage, checkFinal, checkPreview }) actions.Children.Add(button);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        column.Children.Add(status);
        CancellationTokenSource? pending = null;

        void UpdateTests()
        {
            checkFinal.IsEnabled = pending is null && final is not null;
            checkPreview.IsEnabled = pending is null && preview is not null;
        }
        void AddRow(string label, string connection, SpeechSelection? selection)
        {
            var row = Row();
            if (rows.Children.Count % 2 == 1) row.Background = Brushes.WhiteSmoke;
            rows.Children.Add(row);
            Cell(row, 0, Caption(label));
            Cell(row, 1, Caption(connection));
            AddRole(2, "確定", final, value => final = value);
            AddRole(3, "即時表示", preview, value => preview = value);

            void AddRole(int index, string role, SpeechSelection? current, Action<SpeechSelection?> set)
            {
                var button = new RadioButton
                {
                    GroupName = role, Tag = selection, IsChecked = current == selection,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = string.IsNullOrEmpty(connection) ? $"{role} / {label}" : $"{role} / {label} / {connection} / {selection?.Model}"
                };
                AutomationProperties.SetName(button, $"{role} {label} {connection}".TrimEnd());
                button.Checked += (_, _) => { set(selection); status.Text = ""; UpdateTests(); };
                Cell(row, index, button);
            }
        }
        void Refresh()
        {
            rows.Children.Clear();
            foreach (var connection in connections)
                foreach (var model in models.Where(model => model.Selection.ConnectionId == connection.Id))
                    AddRow(model.Label, connection.Name, model.Selection);
            AddRow(SpeechConnection.WindowsDebug.Name, "Windows", new(SpeechConnection.WindowsDebug.Id, ""));
            AddRow("未選択", "", null);
            UpdateTests();
        }
        async Task CheckAsync(SpeechSelection? selection)
        {
            if (selection is null || pending is not null) return;
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            pending = request;
            UpdateTests();
            status.Text = "接続を確認中…";
            try
            {
                var connection = selection.ConnectionId == SpeechConnection.WindowsDebug.Id
                    ? SpeechConnection.WindowsDebug : connections.Single(item => item.Id == selection.ConnectionId);
                var model = connection.Id == SpeechConnection.WindowsDebug.Id ? connection.Settings : connection.Settings with { Model = selection.Model };
                var result = await ExternalSpeechRecognizer.CheckAsync(model, request.Token);
                if (pending == request) status.Text = result;
            }
            catch (OperationCanceledException) { if (pending == request) status.Text = "接続確認がタイムアウトしました。"; }
            catch (Exception ex) { if (pending == request) status.Text = ex.Message; }
            finally
            {
                if (pending == request) pending = null;
                UpdateTests();
            }
        }

        manage.Click += (_, _) =>
        {
            if (!EditSpeechConnections(connections, models)) return;
            if (final is not null && final.ConnectionId != SpeechConnection.WindowsDebug.Id && models.All(item => item.Selection != final)) final = null;
            if (preview is not null && preview.ConnectionId != SpeechConnection.WindowsDebug.Id && models.All(item => item.Selection != preview)) preview = null;
            status.Text = "";
            Refresh();
        };
        checkFinal.Click += async (_, _) => await CheckAsync(final);
        checkPreview.Click += async (_, _) => await CheckAsync(preview);
        Closed += (_, _) => { pending?.Cancel(); pending = null; };
        Refresh();
        return source =>
        {
            if (live.IsChecked == true && preview is null) throw new InvalidOperationException("即時表示用のモデルを選んでください。");
            return source with
            {
                ExternalSpeech = new(), LiveSpeech = new(), SpeechConnections = connections.ToArray(), SpeechModels = models.ToArray(),
                FinalSelection = final, PreviewSelection = preview, LiveDisplayEnabled = live.IsChecked == true
            };
        };
    }

    private bool EditSpeechConnections(List<SpeechConnection> connections, List<RegisteredSpeechModel> models)
    {
        var editedConnections = connections.ToList();
        var editedModels = models.ToList();
        var dialog = new EntryDialog(this, "接続・モデル管理", () => { }) { Height = 400 };
        var list = new ListBox
        {
            DisplayMemberPath = nameof(SpeechConnection.Name), MinHeight = 160, MaxHeight = 220,
            Margin = new Thickness(0, 0, 0, 6)
        };
        dialog.Add(new TextBlock { Text = "登録済みの接続", Margin = new Thickness(0, 0, 0, 4) });
        dialog.Add(list);
        var actions = new WrapPanel();
        dialog.Add(actions);
        var add = SettingsButton("接続を追加");
        var edit = SettingsButton("接続・モデル編集");
        var remove = SettingsButton("接続を削除");
        foreach (var button in new[] { add, edit, remove }) actions.Children.Add(button);
        void Refresh(SpeechConnection? selected)
        {
            list.ItemsSource = editedConnections.ToArray();
            list.SelectedItem = selected;
            edit.IsEnabled = remove.IsEnabled = selected is not null;
        }
        void Open(SpeechConnection? selected)
        {
            var currentModels = editedModels.Where(model => model.Selection.ConnectionId == selected?.Id).ToList();
            var updated = EditSpeechConnection(dialog, selected, currentModels);
            if (updated is null) return;
            if (selected is null) editedConnections.Add(updated);
            else editedConnections[editedConnections.IndexOf(selected)] = updated;
            editedModels.RemoveAll(model => model.Selection.ConnectionId == updated.Id);
            editedModels.AddRange(currentModels);
            Refresh(updated);
        }
        list.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = list.SelectedItem is SpeechConnection;
        add.Click += (_, _) => Open(null);
        edit.Click += (_, _) => { if (list.SelectedItem is SpeechConnection selected) Open(selected); };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not SpeechConnection selected) return;
            editedConnections.Remove(selected);
            editedModels.RemoveAll(model => model.Selection.ConnectionId == selected.Id);
            Refresh(editedConnections.FirstOrDefault());
        };
        Refresh(editedConnections.FirstOrDefault());
        if (dialog.ShowDialog() != true) return false;
        connections.Clear();
        connections.AddRange(editedConnections);
        models.Clear();
        models.AddRange(editedModels);
        return true;
    }

    private static SpeechConnection? EditSpeechConnection(Window owner, SpeechConnection? original, List<RegisteredSpeechModel> models)
    {
        var modes = new Dictionary<string, ExternalSpeechMode>
        {
            ["TranscriptionServer"] = ExternalSpeechMode.TranscriptionServer,
            ["speech.cpp"] = ExternalSpeechMode.SpeechCpp,
            ["外部サーバー（APIキーなし）"] = ExternalSpeechMode.Server,
            ["外部API（APIキーあり）"] = ExternalSpeechMode.ApiKey
        };
        var id = original?.Id ?? Guid.NewGuid();
        var mode = original is not null && modes.ContainsValue(original.Settings.Mode)
            ? original.Settings.Mode : ExternalSpeechMode.TranscriptionServer;
        TextBox name = null!, endpoint = null!;
        PasswordBox key = null!;
        SpeechConnection? updated = null;
        ExternalSpeechSettings Read() => new()
        {
            Mode = mode, Endpoint = endpoint.Text.Trim(), ApiKey = mode == ExternalSpeechMode.ApiKey ? key.Password : ""
        };
        var dialog = new EntryDialog(owner, original is null ? "接続の追加" : "接続・モデルの編集", () =>
        {
            updated = new(id, name.Text.Trim(), Read());
            updated.Validate();
        });
        name = dialog.Text("接続名", original?.Name ?? "");
        var kind = dialog.Choice("接続方式", modes.Keys, modes.First(item => item.Value == mode).Key);
        endpoint = dialog.Text("文字起こしAPIのURL", original?.Settings.Endpoint ?? DefaultUrl(mode), 2048);
        var password = dialog.Password("APIキー", original?.Settings.ApiKey ?? "");
        key = password.Box;
        var fields = new StackPanel();
        dialog.Add(fields);
        var list = new ListBox
        {
            DisplayMemberPath = nameof(RegisteredSpeechModel.Label), MinHeight = 58, MaxHeight = 94,
            Margin = new Thickness(0, 0, 0, 4)
        };
        AutomationProperties.SetName(list, "この接続の登録済みモデル");
        fields.Children.Add(new TextBlock { Text = "この接続の登録済みモデル", Margin = new Thickness(0, 4, 0, 3) });
        fields.Children.Add(list);
        var remove = SettingsButton("モデルを削除");
        fields.Children.Add(remove);
        var available = new ComboBox { MinHeight = 28, DisplayMemberPath = nameof(TranscriptionModel.Label), ToolTip = "サーバーから取得したモデル" };
        var manual = new TextBox { MaxLength = 200, MinHeight = 28, Margin = new Thickness(0), Padding = new Thickness(6, 4, 6, 4), ToolTip = "APIに送るモデル名" };
        fields.Children.Add(new TextBlock { Text = "追加するモデル", Margin = new Thickness(0, 3, 0, 3) });
        fields.Children.Add(available);
        fields.Children.Add(manual);
        var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        fields.Children.Add(actions);
        var refresh = SettingsButton("モデル一覧を取得");
        var add = SettingsButton("モデルを登録");
        actions.Children.Add(refresh);
        actions.Children.Add(add);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        fields.Children.Add(status);
        CancellationTokenSource? pending = null;

        void UpdateList(RegisteredSpeechModel? selected)
        {
            list.ItemsSource = models.ToArray();
            list.SelectedItem = selected;
            remove.IsEnabled = selected is not null;
        }
        void ClearAvailable()
        {
            pending?.Cancel();
            pending = null;
            available.ItemsSource = null;
            refresh.IsEnabled = true;
            status.Text = "";
        }
        void UpdateMode()
        {
            var serverList = mode == ExternalSpeechMode.TranscriptionServer;
            available.Visibility = refresh.Visibility = serverList ? Visibility.Visible : Visibility.Collapsed;
            manual.Visibility = serverList ? Visibility.Collapsed : Visibility.Visible;
            password.Box.Visibility = password.Label.Visibility = mode == ExternalSpeechMode.ApiKey ? Visibility.Visible : Visibility.Collapsed;
            UpdateAdd();
        }
        void UpdateAdd() => add.IsEnabled = mode == ExternalSpeechMode.TranscriptionServer
            ? available.SelectedItem is TranscriptionModel : !string.IsNullOrWhiteSpace(manual.Text);
        kind.SelectionChanged += (_, _) =>
        {
            var next = modes[(string)kind.SelectedItem];
            if (endpoint.Text.Length == 0 || endpoint.Text == DefaultUrl(mode)) endpoint.Text = DefaultUrl(next);
            mode = next;
            ClearAvailable();
            UpdateMode();
        };
        endpoint.TextChanged += (_, _) => ClearAvailable();
        available.SelectionChanged += (_, _) => UpdateAdd();
        manual.TextChanged += (_, _) => UpdateAdd();
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is RegisteredSpeechModel;
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not RegisteredSpeechModel model) return;
            models.Remove(model);
            UpdateList(models.FirstOrDefault());
        };
        add.Click += (_, _) =>
        {
            var candidate = mode == ExternalSpeechMode.TranscriptionServer
                ? (TranscriptionModel?)available.SelectedItem : new(manual.Text.Trim(), manual.Text.Trim());
            if (candidate is null) return;
            if (candidate.Id.Length is 0 or > 200 || candidate.Id.Any(char.IsControl))
            {
                status.Text = "モデル名を確認してください。";
                return;
            }
            var selection = new SpeechSelection(id, candidate.Id);
            if (models.FirstOrDefault(item => item.Selection == selection) is { } existing)
            {
                UpdateList(existing);
                status.Text = "このモデルは登録済みです。";
                return;
            }
            var model = new RegisteredSpeechModel(selection, candidate.Label);
            models.Add(model);
            UpdateList(model);
            manual.Clear();
            status.Text = "";
        };
        async Task LoadModelsAsync()
        {
            ClearAvailable();
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            pending = request;
            refresh.IsEnabled = false;
            status.Text = "モデル一覧を取得中…";
            try
            {
                var fetched = await ExternalSpeechRecognizer.ListTranscriptionModelsAsync(Read(), request.Token);
                if (pending != request) return;
                available.ItemsSource = fetched;
                available.SelectedIndex = fetched.Length == 1 ? 0 : -1;
                status.Text = fetched.Length == 0 ? "選択できる文字起こしモデルがありません。" : "";
            }
            catch (OperationCanceledException) { if (pending == request) status.Text = "モデル一覧の取得がタイムアウトしました。"; }
            catch (Exception ex) { if (pending == request) status.Text = ex.Message; }
            finally { if (pending == request) { pending = null; refresh.IsEnabled = true; } }
        }
        refresh.Click += async (_, _) => await LoadModelsAsync();
        if (original?.Settings.Mode == ExternalSpeechMode.TranscriptionServer)
            dialog.Loaded += async (_, _) => await LoadModelsAsync();
        dialog.Closed += (_, _) => ClearAvailable();
        UpdateMode();
        UpdateList(models.FirstOrDefault());
        return dialog.ShowDialog() == true ? updated : null;
    }

    private static string DefaultUrl(ExternalSpeechMode mode) => mode switch
    {
        ExternalSpeechMode.TranscriptionServer => ExternalSpeechSettings.TranscriptionServerDefault.Endpoint,
        ExternalSpeechMode.SpeechCpp => ExternalSpeechSettings.SpeechCppDefault.Endpoint,
        _ => ""
    };
}

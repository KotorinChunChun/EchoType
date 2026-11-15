using EchoType.Core;
using EchoType.Windows;
using System.Windows.Media.Imaging;

namespace EchoType.App;

internal sealed class InitialWhisperSetup : Window
{
    private static InitialWhisperSetup? _active;
    private readonly AgentPaths _paths;
    private readonly string[] _models;
    private readonly Button _prepare;
    private readonly Button _later;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Action _onReady;
    private CancellationTokenSource? _installation;
    private Task? _completion;

    public static bool IsInstalling => _active?._installation is not null;

    public static async Task CloseAsync()
    {
        if (_active is not { } active) return;
        active._installation?.Cancel();
        if (active._completion is { } completion) await completion;
        if (active.IsVisible) active.Close();
    }


    private static string[] SelectedModels(VoiceInputSettings settings) => settings.ExternalSpeech.Enabled ? [] : (settings.AutomaticInput
        ? settings.RecognitionModels()
        : settings.RecognitionModels().Append(settings.LiveEngine))
        .Where(AsrEngines.IsWhisper).Select(AsrEngines.ModelId).Distinct().ToArray();

    public static void Show(AgentPaths paths, VoiceInputSettings settings, Action onReady)
    {
        if (_active is not null) { _active.Activate(); return; }
        _active = new(paths, SelectedModels(settings), onReady);
        _active.Show();
    }

    private InitialWhisperSetup(AgentPaths paths, string[] models, Action onReady)
    {
        _paths = paths;
        _models = models;
        _onReady = onReady;
        Title = WindowTitle.Create("音声認識モデル");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/EchoType;component/Assets/EchoType.ico"));
        Width = 480;
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(20) };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var missing = models.Where(model => !LocalAsrWorker.IsReady(paths, model)).ToArray();
        if (models.Length == 0) summary.Text = "Whisperを使う場合は、設定でモデルを選択してください。";
        else if (missing.Length == 0) summary.Text = "選択中のWhisperモデルは準備済みです。";
        else if (paths.HasModelOverride) summary.Text = "起動時に指定されたモデル環境の準備状況を確認してください。";
        else summary.Text = "選択中のWhisperモデルを準備します。必要なファイルを取得するにはインターネット接続が必要です。";
        panel.Children.Add(summary);

        foreach (var model in models)
        {
            var state = LocalAsrWorker.IsReady(paths, model) ? "準備済み" : WhisperModelStore.HasWeights(paths, model) ? "実行環境が必要" : "モデルが必要";
            panel.Children.Add(new TextBlock { Text = $"{AsrEngines.Get(model).Name}　{state}", Margin = new Thickness(0, 0, 0, 5) });
        }

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10), Visibility = Visibility.Collapsed };
        panel.Children.Add(_status);
        _progress = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        panel.Children.Add(_progress);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        _prepare = new Button { Content = "準備する", IsDefault = true, IsEnabled = missing.Length > 0 && !paths.HasModelOverride };
        _prepare.Click += (_, _) => _completion = PrepareAsync();
        if (missing.Length > 0 && !paths.HasModelOverride) buttons.Children.Add(_prepare);
        _later = new Button { Content = _prepare.IsEnabled ? "あとで" : "閉じる" };
        _later.Click += (_, _) =>
        {
            if (_installation is { } installation) installation.Cancel();
            else Close();
        };
        buttons.Children.Add(_later);
        panel.Children.Add(buttons);
        Content = panel;

        Closing += (_, args) =>
        {
            if (_installation is not { } installation) return;
            args.Cancel = true;
            installation.Cancel();
        };
        Closed += (_, _) => _active = null;
    }

    private async Task PrepareAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _installation = cancellation;
        _prepare.IsEnabled = false;
        _later.Content = "中止";
        _status.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        var prepared = false;
        try
        {
            await WhisperSetup.InstallAsync(_paths, _models, new Progress<string>(text => _status.Text = text), cancellation.Token);
            _onReady();
            prepared = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _status.Text = "準備を中止しました。あとから再開できます。"; }
        catch (OperationCanceledException) { _status.Text = "準備がタイムアウトしました。接続を確認して、もう一度お試しください。"; }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = error is InvalidOperationException ? error.Message : "準備できませんでした。接続と保存先を確認して、もう一度お試しください。";
        }
        finally
        {
            _installation = null;
            _prepare.IsEnabled = true;
            _later.Content = "あとで";
            _progress.Visibility = Visibility.Collapsed;
        }
        if (prepared && IsVisible) Close();
    }
}

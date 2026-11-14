using EchoType.Windows;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using Forms = System.Windows.Forms;

namespace EchoType.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    private SecureStore? _store;
    private VoiceInputController? _voice;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Console.OutputEncoding = new UTF8Encoding(false);
        var migrate = e.Args.Contains("--migrate-desktop-agent");
        var smoke = e.Args.Contains("--smoke-ui");
        try
        {
            var options = Parse(e.Args);
            if (smoke && !options.ContainsKey("--data-root"))
                throw new ArgumentException("画面試験には --data-root が必要です。");
            if (migrate && (smoke || options.ContainsKey("--open") || options.ContainsKey("--model-root")))
                throw new ArgumentException("移行は単独で実行してください。");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.GetValueOrDefault("--data-root") ?? AgentPaths.DefaultRoot));
            if (smoke && string.Equals(root, AgentPaths.DefaultRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("画面試験には専用の保存先を指定してください。");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
            _instance = new Mutex(false, @"Local\EchoType-" + identity);
            try { _ownsInstance = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            if (!_ownsInstance) throw new InvalidOperationException("EchoType はこの保存先ですでに起動しています。");

            var paths = new AgentPaths(root, options.GetValueOrDefault("--model-root"));
            _store = new SecureStore(paths);
            if (migrate)
            {
                Console.WriteLine(_store.ImportDesktopAgent(options.GetValueOrDefault("--source-root") ?? AgentPaths.LegacyRoot));
                Shutdown();
                return;
            }

            using (var runtime = SharedRecognitionRuntime.Acquire(paths))
            {
                var shared = _store.LoadSettings();
                var recognition = _store.LoadVoiceInputSettings().Recognition;
                runtime.ConfigureEngineSettings(shared, recognition);
                _voice = new(paths, _store, Dispatcher, noAudio: smoke, monitor: !smoke);
                EngineSettingsJournal.Change(paths, "startup", recognition, recognition with { Performance = shared.Performance },
                    runtime.EffectiveSettings(recognition, voice: true), voice: true);
            }
            if (smoke)
            {
                await RunUiSmokeAsync(_voice);
                await ExitAsync();
                return;
            }
            CreateTray();
            SystemEvents.SessionSwitch += SessionSwitch;
            _voice.StartWhisperWarmup();
            if (options.ContainsKey("--open")) _voice.Open();
        }
        catch (Exception ex)
        {
            if (migrate || smoke) Console.Error.WriteLine("EchoType: " + ex.GetType().Name + " — " + ex.Message);
            else MessageBox.Show(ex.Message, "EchoType", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(1);
        }
    }

    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
            Dispatcher.BeginInvoke(() => _voice?.SetSessionLocked(e.Reason == SessionSwitchReason.SessionLock));
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var options = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            var value = name switch
            {
                "--open" or "--migrate-desktop-agent" or "--smoke-ui" => null,
                "--data-root" or "--source-root" or "--model-root" when i + 1 < args.Length && !args[i + 1].StartsWith("--") => args[++i],
                _ => throw new ArgumentException("起動オプションを確認してください。")
            };
            if (!options.TryAdd(name, value)) throw new ArgumentException("起動オプションが重複しています。");
        }
        if (options.ContainsKey("--source-root") && !options.ContainsKey("--migrate-desktop-agent"))
            throw new ArgumentException("--source-root は移行時だけ使用できます。");
        return options;
    }

    private void CreateTray()
    {
        _icon = AppIcon.CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("音声入力", null, (_, _) => _voice!.Open());
        menu.Items.Add("設定", null, (_, _) => _voice!.OpenSettings());
        menu.Items.Add("終了", null, async (_, _) => await ExitAsync());
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "EchoType", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => _voice!.Open();
    }

    private async Task RunUiSmokeAsync(VoiceInputController voice)
    {
        voice.Open();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var window = Windows.OfType<VoiceInputWindow>().Single();
        if (!window.IsVisible || window.FindName("TargetButton") is not Button { ActualWidth: > 0, ActualHeight: > 0 })
            throw new InvalidOperationException("音声入力ウィンドウを表示できません。");
        var settings = new VoiceInputSettingsWindow(voice, voice.Settings.Models) { Owner = window, ShowActivated = false };
        settings.Show();
        settings.UpdateLayout();
        if (!settings.IsVisible || settings.Content is not FrameworkElement { ActualHeight: > 0 })
            throw new InvalidOperationException("設定ウィンドウを表示できません。");
        settings.Close();
        voice.SetSessionLocked(true);
        voice.Open();
        if (window.IsVisible) throw new InvalidOperationException("ロック中に音声入力ウィンドウが表示されました。");
        voice.SetSessionLocked(false);
        voice.Open();
        if (!window.IsVisible) throw new InvalidOperationException("ロック解除後に音声入力ウィンドウを表示できません。");
        voice.Dismiss();
        if (window.IsVisible) throw new InvalidOperationException("音声入力ウィンドウを閉じられません。");
        Console.WriteLine("音声入力と設定の表示・終了、ロック中の取消しと解除後の再表示を確認しました。");
    }

    private async Task ExitAsync(int code = 0)
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.SessionSwitch -= SessionSwitch;
        try { if (_voice is not null) await _voice.StopAsync(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); code = 1; }
        finally { Shutdown(code); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _icon?.Dispose();
        _store?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

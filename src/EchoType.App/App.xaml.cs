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
    private EchoTypeManagement? _management;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Console.OutputEncoding = new UTF8Encoding(false);
        var migrate = e.Args.Contains("--migrate-desktop-agent");
        var smoke = e.Args.Contains("--smoke-ui");
        var managementSmoke = e.Args.Contains("--smoke-management");
        var isolated = smoke || managementSmoke;
        var background = e.Args.Contains("--startup");
        try
        {
            var options = Parse(e.Args);
            if (isolated && !options.ContainsKey("--data-root"))
                throw new ArgumentException("画面試験には --data-root が必要です。");
            if (migrate && (isolated || background || options.ContainsKey("--open") || options.ContainsKey("--model-root") || options.ContainsKey("--launch-origin")))
                throw new ArgumentException("移行は単独で実行してください。");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.GetValueOrDefault("--data-root") ?? AgentPaths.DefaultRoot));
            if (isolated && string.Equals(root, AgentPaths.DefaultRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("画面試験には専用の保存先を指定してください。");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
            _instance = new Mutex(false, @"Local\EchoType-" + identity);
            try { _ownsInstance = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            if (!_ownsInstance)
            {
                if (smoke || migrate) throw new InvalidOperationException("EchoType はこの保存先ですでに起動しています。");
                if (!background && !await EchoTypeManagement.ShowExistingAsync(root))
                    throw new InvalidOperationException("起動済みのEchoTypeの画面を表示できませんでした。");
                Shutdown();
                return;
            }

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
                _voice = new(paths, _store, Dispatcher, noAudio: isolated, monitor: !isolated);
                EngineSettingsJournal.Change(paths, "startup", recognition, recognition with { Performance = shared.Performance },
                    runtime.EffectiveSettings(recognition, voice: true), voice: true);
            }
            if (smoke)
            {
                await RunUiSmokeAsync(_voice, paths);
                await ExitAsync();
                return;
            }
            CreateTray(paths, isolated);
            SystemEvents.SessionSwitch += SessionSwitch;
            _voice.StartWhisperWarmup();
            _management = new(Dispatcher, root, options.GetValueOrDefault("--launch-origin") ?? "standalone",
                () => ShowInput(paths, isolated), () => _ = ExitAsync(), _voice.ManagementStopBlockReason);
            if (!background) ShowInput(paths, isolated);
        }
        catch (Exception ex)
        {
            if (migrate || isolated || background) Console.Error.WriteLine("EchoType: " + ex.GetType().Name + " — " + ex.Message);
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
                "--open" or "--startup" or "--migrate-desktop-agent" or "--smoke-ui" or "--smoke-management" => null,
                "--data-root" or "--source-root" or "--model-root" when i + 1 < args.Length && !args[i + 1].StartsWith("--") => args[++i],
                "--launch-origin" when i + 1 < args.Length && args[i + 1] is "host_manual" or "direct_task" or "via_kotorintoys" => args[++i],
                _ => throw new ArgumentException("起動オプションを確認してください。")
            };
            if (!options.TryAdd(name, value)) throw new ArgumentException("起動オプションが重複しています。");
        }
        if (options.ContainsKey("--source-root") && !options.ContainsKey("--migrate-desktop-agent"))
            throw new ArgumentException("--source-root は移行時だけ使用できます。");
        return options;
    }

    private void ShowInput(AgentPaths paths, bool isolated)
    {
        _voice!.Open();
        if (!isolated && !_voice.IsPaused) InitialWhisperSetup.ShowIfNeeded(paths, _voice.Settings, _voice.StartWhisperWarmup);
    }

    private void CreateTray(AgentPaths paths, bool isolated)
    {
        _icon = AppIcon.CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("音声入力", null, (_, _) => ShowInput(paths, isolated));
        menu.Items.Add("設定", null, (_, _) => _voice!.OpenSettings());
        if (!isolated)
            menu.Items.Add("音声認識モデル", null, (_, _) => InitialWhisperSetup.Show(paths, _voice!.Settings, _voice.StartWhisperWarmup));
        var pause = new Forms.ToolStripMenuItem("一時停止");
        pause.Click += (_, _) =>
        {
            _voice!.SetPaused(!_voice.IsPaused);
            pause.Text = _voice.IsPaused ? "再開" : "一時停止";
            _tray!.Text = _voice.IsPaused ? "EchoType（一時停止中）" : "EchoType";
        };
        menu.Items.Add(pause);
        menu.Items.Add("終了", null, async (_, _) => await ExitAsync());
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "EchoType", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowInput(paths, isolated);
    }

    private async Task RunUiSmokeAsync(VoiceInputController voice, AgentPaths paths)
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
        voice.SetPaused(true);
        voice.Open();
        if (!voice.IsPaused || window.IsVisible) throw new InvalidOperationException("一時停止中に音声入力ウィンドウが表示されました。");
        voice.SetPaused(false);
        voice.Open();
        if (voice.IsPaused || !window.IsVisible) throw new InvalidOperationException("再開後に音声入力ウィンドウを表示できません。");
        InitialWhisperSetup.ShowIfNeeded(paths, voice.Settings, voice.StartWhisperWarmup);
        if (!Windows.OfType<InitialWhisperSetup>().Any(setup => setup.IsVisible))
            throw new InvalidOperationException("不足しているモデルの準備画面を表示できません。");
        await InitialWhisperSetup.CloseAsync();
        InitialWhisperSetup.ShowIfNeeded(paths, voice.Settings, voice.StartWhisperWarmup);
        if (Windows.OfType<InitialWhisperSetup>().Any(setup => setup.IsVisible))
            throw new InvalidOperationException("一度閉じた初回案内が再表示されました。");
        voice.Dismiss();
        if (window.IsVisible) throw new InvalidOperationException("音声入力ウィンドウを閉じられません。");
        Console.WriteLine("音声入力と設定、ロックと一時停止、初回モデル案内と再表示の抑止を確認しました。");
    }

    private async Task ExitAsync(int code = 0)
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.SessionSwitch -= SessionSwitch;
        try
        {
            await InitialWhisperSetup.CloseAsync();
            if (_voice is not null) await _voice.StopAsync();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); code = 1; }
        finally { Shutdown(code); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _management?.Dispose();
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

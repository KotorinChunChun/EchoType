using EchoType.Core;
using EchoType.Windows;
using NAudio.Wave;
using System.Windows.Threading;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private readonly AgentPaths _paths;
    private readonly SecureStore _store;
    private readonly SemaphoreSlim _recognitionSlots = new(2, 2);
    private readonly Dispatcher _dispatcher;
    private readonly bool _noAudio;
    private readonly bool _monitorTargets;
    private readonly Func<VoiceInputTarget.FocusStamp> _readTargetFocus;
    private readonly Func<VoiceInputTarget.FocusStamp, VoiceInputTarget> _captureOpenTarget;
    private readonly Func<string, VoiceInputSettings, CancellationToken, Task<string>> _format;
    private readonly CodexClient? _formatCodex;
    private readonly Func<InputDevice, IWaveIn> _openInput;
    private readonly Func<string, SpeechSegment, AppSettings, CancellationToken, Task<AsrResult>>? _recognize;
    private readonly Func<IVoiceInputLive> _liveFactory;
    private readonly VoiceInputKeyboard? _keyboard;
    private readonly DispatcherTimer _targetTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly HashSet<Task> _tasks = [];
    private readonly List<CancellationTokenSource> _retired = [];
    private InputDevice? _explicitDevice;
    private string _recordingDevice = "";
    private VoiceInputWindow? _window;
    private VoiceInputSettingsWindow? _traySettings;
    private VoiceInputTarget? _target, _lastExternal, _placedTarget;
    private VoiceInputTarget? _loadedTarget;
    private int _targetCaptureVersion;
    private int _targetLoadVersion;
    private bool _loadingTarget;
    public bool ReplacementReady => _target is not null && ReferenceEquals(_loadedTarget, _target);
    private string _targetError = "入力欄で設定した録音キーを押すか、ターゲットをドラッグしてください。";
    private VoiceInputCapture? _capture, _inputMonitor;
    private Recording? _recording;
    private VoiceInputSession? _session;
    private CancellationTokenSource? _operation;
    private Task _work = Task.CompletedTask;
    private VoiceInputTarget.FocusStamp _lastExternalFocus;
    private bool _busy, _stopping, _sessionLocked;
    private WaveOutEvent? _playback;
    private WaveFileReader? _playReader;
    private byte[]? _playWave;
    private sealed class Recording(VoiceInputSession session, VoiceInputLogEntry entry, VoiceInputCapture capture,
        IVoiceInputLive live, VoiceInputSettings settings)
    {
        public VoiceInputSession Session { get; } = session;
        public VoiceInputLogEntry Entry { get; } = entry;
        public VoiceInputCapture Capture { get; } = capture;
        public IVoiceInputLive Live { get; set; } = live;
        public VoiceInputSettings Settings { get; } = settings;
    }
    private readonly HashSet<Recording> _previewRecordings = [];
    public VoiceInputSettings Settings { get; private set; }
    public event Action<string>? Status;
    private bool ConfigurationLocked => _busy || _capture is not null || (_session?.Pending ?? 0) > 0;
    public VoiceInputController(AgentPaths paths, SecureStore store, Dispatcher dispatcher, bool noAudio, bool monitor,
        Func<string, VoiceInputSettings, CancellationToken, Task<string>>? formatter = null,
        Func<InputDevice, IWaveIn>? openInput = null,
        Func<string, SpeechSegment, AppSettings, CancellationToken, Task<AsrResult>>? recognizer = null,
        Func<int, bool>? keyState = null, Func<IVoiceInputLive>? liveFactory = null,
        Func<VoiceInputTarget.FocusStamp>? readTargetFocus = null,
        Func<VoiceInputTarget.FocusStamp, VoiceInputTarget>? captureOpenTarget = null)
    {
        _paths = paths; _store = store; _dispatcher = dispatcher; _noAudio = noAudio;
        _monitorTargets = monitor; _readTargetFocus = readTargetFocus ?? VoiceInputTarget.CaptureFocusStamp;
        _captureOpenTarget = captureOpenTarget ?? VoiceInputTarget.CaptureRemembered;
        Settings = store.LoadVoiceInputSettings();
        _commonSettings = Settings; _profiles = store.GetVoiceProfiles(); _words = store.GetPersonalWords();
        _formatCodex = formatter is null ? new CodexClient(paths) : null;
        _format = formatter ?? _formatCodex!.FormatVoiceInputAsync;
        _openInput = openInput ?? (device => device.Open());
        _recognize = recognizer; _liveFactory = liveFactory ?? CreateLive;
        _meterTimer.Tick += (_, _) => UpdateMeter();
        if (monitor)
        {
            _keyboard = new(Settings.GetRecordingShortcut(), keyState);
            _keyboard.Changed += (change, stamp) =>
            {
                var generation = Volatile.Read(ref _pauseGeneration);
                _dispatcher.BeginInvoke(() =>
                {
                    if (generation == _pauseGeneration) OnGesture(change, stamp);
                });
            };
            _keyboard.EscapePressed += () =>
            {
                // 本体・設定画面にフォーカスがあるときはWPF側で処理し、二重に閉じない。
                if (!VoiceInputTarget.ExternalForeground) return;
                var generation = Volatile.Read(ref _pauseGeneration);
                _dispatcher.BeginInvoke(() =>
                {
                    if (generation != _pauseGeneration || _paused || _sessionLocked || _stopping) return;
                    if (_window?.SettingsOpen == true) _window.CloseSettings();
                    else if (_window?.IsVisible == true || _capture is not null || _busy) Dismiss();
                });
            };
            _targetTimer.Tick += (_, _) => { if (!ConfigurationLocked) TrackTarget(); };
            _targetTimer.Start(); TrackTarget();
        }
    }
    private IVoiceInputLive CreateLive()
    {
        var speech = Settings.PreviewSpeech();
        return new VoiceInputAccurateLive((segment, ct) => ExternalSpeechRecognizer.RecognizeAsync(segment.Wave, speech, ct));
    }
    private void TrackTarget()
    {
        if (_stopping || _sessionLocked || _paused || !_monitorTargets) return;
        // 監視中はUIA・本文・選択範囲に触れない。外部プロバイダーへの遅延要求を残さない。
        var stamp = _readTargetFocus();
        if (stamp.Window == 0) return;
        if (stamp.Window != _lastExternalFocus.Window || stamp.Focus != _lastExternalFocus.Focus || stamp.ProcessId != _lastExternalFocus.ProcessId)
        {
            _lastExternal = null;
            VoiceInputFocusJournal.Write(_paths, "", "observe", "native-focus-changed", stamp.Focus);
        }
        _lastExternalFocus = stamp;
    }
    private async Task CaptureTargetForOpenAsync(VoiceInputSession session, VoiceInputTarget.FocusStamp stamp, int version)
    {
        var operation = Guid.NewGuid().ToString("N");
        var allowedForeground = VoiceInputTarget.ForegroundWindow;
        try
        {
            VoiceInputFocusJournal.Write(_paths, operation, "capture", "explicit-open", stamp.Focus);
            var target = await Task.Run(() => _captureOpenTarget(stamp)).WaitAsync(TimeSpan.FromSeconds(3));
            if (!Current(session) || version != _targetCaptureVersion || _target is not null || _window?.IsVisible != true ||
                !VoiceInputTarget.IsOwnOrExpectedForeground(allowedForeground))
            { VoiceInputFocusJournal.Write(_paths, operation, "discard", "foreground-or-session-changed", stamp.Focus); return; }
            _target = target; _lastExternal = target; _targetError = "";
            SelectApplication(target.ApplicationPath); _window.ShowTarget(target); RefreshState();
            if (ReplaceRequested) await LoadTargetTextAsync(target);
            if (_window.IsVisible && ReferenceEquals(_target, target)) await _window.PlaceNearTargetAsync(target);
        }
        catch (Exception)
        {
            if (Current(session) && version == _targetCaptureVersion && _target is null)
            { _targetError = "入力欄を取得できません。ターゲットをドラッグしてください。"; _window?.ShowTarget(null, _targetError); }
            VoiceInputFocusJournal.Write(_paths, operation, "capture-failed", "explicit-open", stamp.Focus);
        }
    }
    private VoiceInputSession EnsureSession()
    {
        if (_session is null)
        {
            _session = new(_paths); _window?.BindLog(_session.Entries);
        }
        return _session;
    }
    private bool Current(VoiceInputSession session) => !_stopping && !session.Closed && ReferenceEquals(_session, session);
    public void Open()
    {
        if (_stopping || _sessionLocked || _paused) return;
        if (_capture is null) SelectApplication((_target ?? _lastExternal)?.ApplicationPath ?? "");
        TrackTarget();
        var session = EnsureSession();
        _window ??= new(this, Settings); _window.BindLog(session.Entries);
        if (!_window.IsVisible) TemporarilyPinned = false;
        if (_capture is null && _target is null) _target = _lastExternal;
        _window.ShowTarget(_target, _targetError); RefreshState();
        var wasVisible = _window.IsVisible;
        if (!wasVisible) _window.ResetFormattingDiff();
        if (!wasVisible && Settings.ReplaceTargetText && !_reediting) _loadedTarget = null;
        _window.ShowActivated = true; _window.Show();
        if (ReplaceRequested && _target is not null && !ReplacementReady) Track(LoadTargetTextAsync(_target));
        if (!wasVisible || !ReferenceEquals(_placedTarget, _target)) { _placedTarget = _target; Track(_window.PlaceNearTargetAsync(_target)); }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        VisibilityChanged(); UpdateMeter(); _window.FocusDraft();
        ApplyFinalResults(session);
        if (_target is null && _lastExternalFocus.Window != 0 && _monitorTargets)
            Track(CaptureTargetForOpenAsync(session, _lastExternalFocus, ++_targetCaptureVersion));
    }
    public void OpenSettings()
    {
        if (_stopping || _sessionLocked || ConfigurationLocked || _window?.SettingsOpen == true || _traySettings is not null) return;
        var dialog = _traySettings = new VoiceInputSettingsWindow(this);
        if (_window?.IsVisible == true) dialog.Owner = _window;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { if (dialog.ShowDialog() == true) _window?.ShowProfileSettings(Settings); }
        finally { _traySettings = null; }
    }
    public void SaveSettings(VoiceInputSettings settings)
    {
        if (ConfigurationLocked) throw new InvalidOperationException("録音・認識の終了後に設定を保存してください。");
        var shortcut = settings.GetRecordingShortcut();
        var keysChanged = shortcut != Settings.GetRecordingShortcut();
        PersistSettings(settings);
        _window?.ShowProfileSettings(Settings);
        Say("音声入力の設定を保存しました。");
        if (keysChanged) _keyboard?.Configure(shortcut);
    }
    public void SelectDevice(InputDevice device)
    {
        if (_capture is not null || _busy || _stopping) throw new InvalidOperationException("録音・整形終了後にデバイスを選択してください。");
        var settings = Settings with { Recognition = Settings.Recognition with { DeviceNumber = device.Number, DeviceName = device.Name } };
        PersistSettings(settings); _explicitDevice = device;
        Say("録音デバイスを保存しました。次の押下音声から使用します。"); StopMonitor(); VisibilityChanged();
    }
    public void ForgetDeviceSelection() { _explicitDevice = null; StopMonitor(); VisibilityChanged(); }
    private InputDevice ResolveDevice() => InputDevice.Resolve(RecordingDevices.InputDevices(), Settings.Recognition.DeviceNumber, Settings.Recognition.DeviceName,
        _explicitDevice == new InputDevice(Settings.Recognition.DeviceNumber, Settings.Recognition.DeviceName));
    public void VisibilityChanged()
    {
        if (_stopping || _paused || _noAudio || _window?.IsVisible != true) { StopMonitor(); return; }
        if (_capture is not null || _inputMonitor is not null) return;
        try
        {
            var device = ResolveDevice(); _recordingDevice = device.Name;
            var monitor = new VoiceInputCapture(_openInput(device), 2, monitorOnly: true); _inputMonitor = monitor;
            monitor.Interrupted += () => _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_inputMonitor, monitor)) return;
                StopMonitor(); UpdateMeter(); Say("マイクが停止しました。デバイスを選び直してください。");
            });
            monitor.Start(); _meterTimer.Start();
        }
        catch (Exception ex) { StopMonitor(); Say("マイクを開けません: " + ex.Message); }
    }
    private void StopMonitor()
    {
        var monitor = _inputMonitor; _inputMonitor = null; monitor?.Dispose();
        if (_capture is null) _meterTimer.Stop();
    }
    private void UpdateMeter()
    {
        if (_window?.IsVisible == true) _window.ShowMeter(_capture?.Meter() ?? _inputMonitor?.Meter(), _recordingDevice, _capture is not null);
    }
    private void Say(string message) { _window?.SetStatus(message); Status?.Invoke("音声入力: " + message); }
    private void RefreshState() => _window?.SetActivity(_capture is not null, _session?.Pending ?? 0, _busy || _loadingTarget || _automaticProcessing || Settings.AutomaticInput && (_automaticEntries.Count > 0 || _automaticDraftRequested) && !_automaticFaulted);
    private void SetBusy(bool busy) { _busy = busy; RefreshState(); if (!busy && _session is { } session) ApplyFinalResults(session); }
    private void Track(Task task)
    {
        _tasks.Add(task); _ = Observe();
        async Task Observe() { try { await task; } catch (Exception ex) { if (!_stopping) Say(ex.Message); } finally { _tasks.Remove(task); } }
    }
    private void OnGesture(VoiceInputGesture.Change change, VoiceInputTarget.FocusStamp stamp)
    {
        if (_stopping || _sessionLocked || _paused) return;
        if (change == VoiceInputGesture.Change.Started) BeginRecording(stamp);
        else if (_capture is not null) ReleaseRecording(change == VoiceInputGesture.Change.Accepted);
    }
    private void BeginRecording(VoiceInputTarget.FocusStamp stamp)
    {
        if (!Settings.Enabled || _noAudio || _busy || _automaticSending || _capture is not null || _window?.SettingsOpen == true || _traySettings is not null) return;
        if (stamp.Window != 0) SelectApplication(VoiceInputTarget.ApplicationForWindow(stamp.Window));
        _automaticCancellation?.Cancel();
        var keepAutomaticTarget = Settings.AutomaticInput && _automaticEntries.Count > 0;
        var session = EnsureSession();
        IVoiceInputLive? startingLive = null;
        VoiceInputCapture? startingCapture = null;
        try
        {
            Settings.Validate();
            if (_recognize is null) Settings.FinalSpeech().ValidateEndpoint();
            StopPlayback(); StopMonitor();
            _window?.ResetFormattingDiff();
            var device = ResolveDevice(); _recordingDevice = device.Name;
            var noPreview = !Settings.LiveDisplayEnabled;
            var live = startingLive = noPreview ? new NoPreview() : _liveFactory();
            var capture = startingCapture = new VoiceInputCapture(_openInput(device), Settings.MaximumSeconds);
            var entry = new VoiceInputLogEntry { Number = ++session.RecordingSequence, LiveEngine = noPreview ? "" : Settings.PreviewSpeech().Model }; session.Entries.Add(entry);
            var effective = Effective(Settings);
            var recording = new Recording(session, entry, capture, live, effective with { Recognition = effective.Recognition with { DeviceNumber = device.Number, DeviceName = device.Name } }); _recording = recording; _capture = capture;
            _previewRecordings.Add(recording);
            RegisterAutomaticRecording(recording);
            startingLive = null; startingCapture = null;
            capture.Frame += live.Feed;
            live.Updated += text => _dispatcher.BeginInvoke(() =>
            {
                if (!Current(session) || !ReferenceEquals(_recording, recording)) return;
                entry.LiveText = text; entry.Changed(); _window?.ShowLive(text, false);
            });
            if (live is VoiceInputAccurateLive preview) preview.Failed += error => _dispatcher.BeginInvoke(() =>
            {
                if (Current(session) && ReferenceEquals(_recording, recording)) _window?.ShowLiveError(error);
            });
            capture.LimitReached += () => _dispatcher.BeginInvoke(() => { if (ReferenceEquals(_recording, recording)) { _keyboard?.Reset(); ReleaseRecording(true); } });
            capture.Interrupted += () => _dispatcher.BeginInvoke(() => { if (ReferenceEquals(_recording, recording)) { ReleaseRecording(false); Say("マイク停止により今回の録音を破棄しました。"); } });
            capture.Start(); _meterTimer.Start(); RefreshState(); _window?.ResetLive();
            if (stamp.Window != 0 && !keepAutomaticTarget)
            {
                _target = null; Track(CaptureTargetAndShowAsync(recording, stamp));
            }
            else { Open(); SayRecording(entry); }
        }
        catch (Exception ex)
        {
            startingCapture?.Dispose(); startingLive?.Dispose();
            if (_recording is not null) ReleaseRecording(false);
            Open(); Say(ex.Message);
            _window?.ShowRecognition(ex.Message, error: true);
        }
    }
    private void SayRecording(VoiceInputLogEntry entry) => Say($"録音 #{entry.Number} … 録音キーを離すと全文認識。認識中も次の録音を開始できます。");
    private async Task CaptureTargetAndShowAsync(Recording recording, VoiceInputTarget.FocusStamp stamp)
    {
        var version = ++_targetCaptureVersion;
        _automaticTargetPending = true;
        try
        {
            var target = await Task.Run(() => VoiceInputTarget.CaptureFocused(stamp)).WaitAsync(TimeSpan.FromSeconds(3));
            if (!Current(recording.Session) || version != _targetCaptureVersion || !VoiceInputTarget.IsOwnOrExpectedForeground(stamp.Window)) return;
            _target = target; _lastExternal = target; _targetError = "";
        }
        catch (Exception ex) { if (Current(recording.Session)) _targetError = ex.Message; }
        finally
        {
            if (Current(recording.Session) && version == _targetCaptureVersion && VoiceInputTarget.IsOwnOrExpectedForeground(stamp.Window))
            { Open(); _window!.ShowLive(recording.Entry.LiveText, _capture is null); if (_capture is not null) SayRecording(recording.Entry); }
            if (version == _targetCaptureVersion) { _automaticTargetPending = false; TryAutomaticInput(); }
        }
    }
    private void ReleaseRecording(bool keep)
    {
        var recording = _recording; if (recording is null) return;
        _recording = null; _capture = null;
        recording.Entry.Status = keep ? "音声確定中" : "破棄中"; recording.Entry.Changed();
        _window?.ShowLive(recording.Entry.LiveText, true);
        // StopAsyncは最初のawaitまでに入力受付を止める。次の録音は別のIWaveInで受け付ける。
        Track(FinishRecordingAsync(recording, keep)); RefreshState();
    }
    private async Task FinishRecordingAsync(Recording recording, bool keep)
    {
        var session = recording.Session; var entry = recording.Entry; SpeechSegment? segment = null;
        var queued = false;
        try
        {
            segment = await recording.Capture.StopAsync(keep);
            recording.Capture.Dispose();
            if (!Current(session)) return;
            VisibilityChanged();
            if (segment is null) { entry.DraftHandled = true; entry.Status = "短押し・中止で破棄"; _automaticEntries.RemoveAll(item => ReferenceEquals(item.Entry, entry)); return; }
            entry.Seconds = segment.Seconds;
            var recognitionSettings = recording.Settings;
            entry.Metadata = segment with { Wave = [] }; entry.RecognitionSettings = recognitionSettings;
            entry.CachePath = session.Cache.Save(entry.Number, segment.Wave);
            entry.Status = "認識待ち"; entry.Changed();
            Track(RecognizeQueuedAsync(session, entry, segment with { Wave = [] }, recognitionSettings)); queued = true;
            var live = await recording.Live.CompleteAsync(session.Cancellation.Token);
            if (!Current(session)) return;
            entry.LiveText = live.Text; entry.LiveError = live.Error;
            if (_recording is null && ReferenceEquals(session.Entries.LastOrDefault(), entry))
            {
                _window?.ShowLive(live.Text, true);
                if (live.Error.Length > 0) _window?.ShowLiveError(live.Error);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(session)) { entry.LiveError = ex.Message; entry.Status = "失敗: " + ex.Message; Say(ex.Message); } }
        finally
        {
            if (segment is not null) Array.Clear(segment.Wave);
            recording.Capture.Dispose(); recording.Live.Dispose(); await recording.Live.Stopped; entry.LiveFinished = true;
            _previewRecordings.Remove(recording);
            if (Current(session))
            {
                if (!queued) entry.Finished = true;
                ApplyFinalResults(session);
                entry.Changed(); RefreshState(); VisibilityChanged(); MaybeCloseEmpty(session);
                TryAutomaticInput();
            }
        }
    }
    private async Task RecognizeQueuedAsync(VoiceInputSession session, VoiceInputLogEntry entry, SpeechSegment metadata, VoiceInputSettings settings)
    {
        var token = session.Cancellation.Token;
        var speech = settings.FinalSpeech();
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        var watch = new System.Diagnostics.Stopwatch();
        AsrResult result;
        try
        {
            await _recognitionSlots.WaitAsync(token);
            try
            {
                waiting.Stop();
                watch.Start();
                if (!Current(session)) return;
                entry.Status = "確定認識中"; entry.Changed();
                _window?.ShowRecognition($"認識中  {speech.Model}");
                result = await Task.Run(async () =>
                {
                    var wave = session.Cache.Read(entry.CachePath);
                    try
                    {
                        if (_recognize is not null) return await _recognize(AsrEngines.ExternalTranscription, metadata with { Wave = wave }, settings.Recognition, token);
                        return await ExternalSpeechRecognizer.RecognizeAsync(wave, speech, token);
                    }
                    finally { Array.Clear(wave); }
                }, token);
            }
            finally { _recognitionSlots.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) { result = new(AsrEngines.ExternalTranscription, "", watch.Elapsed.TotalSeconds, ex.Message); }
        if (!Current(session) || token.IsCancellationRequested) return;
        result = result with { PendingSeconds = result.PendingSeconds + waiting.Elapsed.TotalSeconds };
        if (result.Succeeded && string.IsNullOrWhiteSpace(result.Text))
            result = result with { Error = "音声から文字を認識できませんでした。" };
        entry.Results = [result];
        entry.Finished = true;
        entry.Status = result.Succeeded ? "認識完了" : "認識失敗";
        entry.Changed(); RefreshState();
        RecordTiming($"録音 #{entry.Number}", speech.Model, result.PendingSeconds + result.QueueSeconds,
            result.RecognitionSeconds ?? result.ProcessingSeconds, result.Succeeded ? "完了" : result.Error);
        _window?.ShowRecognition(result.Succeeded ? $"認識完了  {speech.Model}" : $"認識失敗  #{entry.Number}  {speech.Model}  {result.Error}", !result.Succeeded);
        if (!result.Succeeded) Say("確定認識: " + result.Error);
        ApplyFinalResults(session);
        MaybeCloseEmpty(session);
    }

    private void ApplyFinalResults(VoiceInputSession session)
    {
        if (!Current(session) || _busy || _capture is not null || _window?.IsVisible != true) return;
        if (Settings.AutomaticInput) { TryAutomaticInput(); return; }
        foreach (var entry in session.Entries.TakeWhile(entry => entry.Finished).Where(entry => !entry.DraftHandled))
        {
            if (entry.Results.FirstOrDefault() is { Succeeded: true } result)
            {
                if (!_window.InsertDraft(Settings.CompleteCandidate(result.Text), focus: false)) return;
                _window.ResetFormattingDiff();
            }
            entry.DraftHandled = true;
        }
    }

    private void MaybeCloseEmpty(VoiceInputSession session)
    {
        if (TemporarilyPinned || !Current(session) || _capture is not null || session.Entries.Any(e => !e.Finished || !e.LiveFinished)) return;
        if (session.Entries.Any(e => e.LiveText.Length > 0 || e.LiveError.Length > 0 || e.Results.Any(r => r.Text.Length > 0 || r.Error is { Length: > 0 }))) return;
        if (!string.IsNullOrWhiteSpace(_window?.DraftText)) return;
        Dismiss();
    }
    public void Play(VoiceInputLogEntry entry)
    {
        if (_capture is not null) { Say("録音終了後に再生してください。"); return; }
        try
        {
            StopPlayback();
            if (_session is null || !_session.Entries.Contains(entry)) return;
            _playWave = _session.Cache.Read(entry.CachePath);
            _playReader = new WaveFileReader(new MemoryStream(_playWave, false)); _playback = new(); _playback.Init(_playReader); _playback.Play();
        }
        catch (Exception ex) { StopPlayback(); Say(ex.Message); }
    }
    public void StopPlayback()
    {
        _playback?.Stop(); _playback?.Dispose(); _playback = null; _playReader?.Dispose(); _playReader = null;
        if (_playWave is not null) Array.Clear(_playWave); _playWave = null;
    }
    public async Task PickTargetAsync(Point point)
    {
        if (_busy || _capture is not null || _stopping) return;
        var version = ++_targetCaptureVersion;
        _target = null; _loadedTarget = null; _lastExternal = null;
        SetBusy(true);
        try
        {
            var target = await Task.Run(() => VoiceInputTarget.CapturePoint(point));
            if (version != _targetCaptureVersion) return;
            SelectApplication(target.ApplicationPath); _target = target; _lastExternal = target; _targetError = "";
            if (Settings.ReplaceTargetText) await LoadTargetTextAsync(target);
            if (version == _targetCaptureVersion) Say("入力先と文字位置を変更しました。");
        }
        catch (Exception ex) { if (version == _targetCaptureVersion) { _targetError = ex.Message; Say(ex.Message); } }
        finally
        {
            SetBusy(false);
            if (version == _targetCaptureVersion)
            { _window?.ShowTarget(_target, _targetError); _placedTarget = _target; if (_window is not null) await _window.PlaceNearTargetAsync(_target); }
        }
    }
    public async Task SetReplaceTargetTextAsync(bool enabled)
    {
        _reediting = false;
        Settings = Settings with { ReplaceTargetText = enabled };
        PersistSettings(Settings); _loadedTarget = null;
        if (enabled && _target is not null) await LoadTargetTextAsync(_target);
        RefreshState();
    }
    private async Task LoadTargetTextAsync(VoiceInputTarget target)
    {
        var version = ++_targetLoadVersion;
        _loadingTarget = true;
        _loadedTarget = null;
        RefreshState();
        try
        {
            var snapshot = await Task.Run(target.RefreshSnapshot);
            if (version != _targetLoadVersion || !Settings.ReplaceTargetText || !ReferenceEquals(_target, target) || _window?.IsVisible != true) return;
            if (snapshot.OriginalText.Length > 16000) throw new InvalidOperationException("入力先が16000文字を超えるため読み込めません。");
            _target = snapshot; _loadedTarget = snapshot;
            _window.LoadTargetText(snapshot); _window.ShowTarget(snapshot); RefreshState();
        }
        catch (Exception ex) { if (version == _targetLoadVersion) { Say("現在の入力済文字列を取得できません: " + ex.Message); RefreshState(); } }
        finally { if (version == _targetLoadVersion) { _loadingTarget = false; RefreshState(); TryAutomaticInput(); } }
    }
    public void SetCapturePanelExpanded(bool expanded)
    {
        var settings = Settings with { CapturePanelExpanded = expanded };
        PersistSettings(settings);
    }
    public void SaveWindowSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 240 || height < 180 || width > 16000 || height > 16000) return;
        if (Settings.WindowWidth == width && Settings.WindowHeight == height) return;
        var settings = Settings with { WindowWidth = width, WindowHeight = height };
        PersistSettings(settings);
    }
    public async Task FormatAsync(bool simple = false)
    {
        if (_busy || _capture is not null || _stopping || _window is null || string.IsNullOrWhiteSpace(_window.DraftText)) return;
        var text = _window.DraftText;
        var settings = Settings with { FormattingModel = _window.SelectedFormattingModel, FormattingEffort = _window.SelectedFormattingEffort };
        try { PersistSettings(settings); }
        catch (Exception ex) { Say(ex.Message); return; }
        SetBusy(true); var operation = new CancellationTokenSource(); _operation = operation; _work = FormatCoreAsync(); await _work;
        async Task FormatCoreAsync()
        {
            var succeeded = false;
            try
            {
                Say($"{settings.FormattingModel} / {settings.FormattingEffort} で{(simple ? "単純整形" : "プロンプト用整形")}中…");
                var effective = Effective(settings);
                var formatted = await FormatMeasuredAsync(text, simple ? effective with { FormattingPrompt = settings.SimpleFormattingPrompt } : effective, operation.Token); operation.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(formatted) || formatted.Length > 16000) throw new InvalidOperationException("整形結果が空、または16000文字を超えています。元の本文を保持しました。");
                _window.ShowFormattingDiff(text, formatted); succeeded = true;
                Say(Settings.AutomaticInput && AutomaticConfirmEnabled ? "整形しました。入力へ進みます。" : "整形しました。右側を編集し、決定で入力できます。");
            }
            catch (OperationCanceledException) { Say("整形を中止しました。編集本文は保持しています。"); }
            catch (Exception ex) { Say(ex.Message); }
            finally { operation.Dispose(); _operation = null; SetBusy(false); if (succeeded) ContinueAutomaticDraft(); TryAutomaticInput(); }
        }
    }
    public async Task ConfirmAsync()
    {
        if (_busy || _capture is not null || _stopping || _window is null || (!ReplaceRequested && string.IsNullOrWhiteSpace(_window.DraftText))) return;
        if (_target is null) { Say("ターゲットをドラッグして入力先を指定してください。"); return; }
        if (ReplaceRequested && !ReplacementReady) { Say("入力先の本文を取得してから決定してください。"); return; }
        var target = _target; var text = _window.DraftText;
        var session = EnsureSession();
        var replace = ReplaceRequested;
        SetBusy(true); var operation = new CancellationTokenSource(); _operation = operation; _work = SendAsync(); await _work;
        async Task SendAsync()
        {
            try
            {
                await Task.Run(target.Validate); operation.Token.ThrowIfCancellationRequested();
                var result = await InsertMeasuredAsync(target, text, replace, operation.Token);
                if (!Current(session)) return;
                Say(result);
                if (target.WriteVerified)
                {
                    _target = null; _loadedTarget = null; _window.ShowTarget(null);
                    _window.DraftText = ""; Dismiss();
                }
            }
            catch (OperationCanceledException) { if (Current(session)) Say("入力を中止しました。"); }
            catch (Exception ex) { if (Current(session)) Say(target.Consumed ? "入力結果が不明です。自動再送はしません。入力先を確認してください。" : ex.Message); }
            finally
            {
                if (target.Consumed && ReferenceEquals(_target, target)) { _target = null; _loadedTarget = null; _window.ShowTarget(null); }
                operation.Dispose(); _operation = null; SetBusy(false);
                if (Current(session) && _window.IsVisible && target.CanReturnFocus) _window.FocusDraft();
            }
        }
    }
    public void Cancel() => Dismiss();
    internal void HideToTray()
    {
        ++_targetCaptureVersion;
        _automaticTargetPending = false;
        _window?.Hide();
        VisibilityChanged();
    }
    internal void SetSessionLocked(bool locked)
    {
        _sessionLocked = locked;
        _keyboard?.Reset();
        if (!locked) return;
        _window?.CloseSettings();
        _traySettings?.Close();
        Dismiss();
        _lastExternal = null;
        _lastExternalFocus = default;
    }

    public void Dismiss()
    {
        RememberUnsentDraft();
        _reediting = false;
        ++_targetCaptureVersion; ++_targetLoadVersion;
        _loadingTarget = false; _automaticTargetPending = false;
        ResetAutomaticInput();
        _keyboard?.Reset(); _operation?.Cancel();
        var session = _session; _session = null;
        if (session is not null) { session.Closed = true; session.Cancellation.Cancel(); _retired.Add(session.Cancellation); }
        _window?.Hide(); StopMonitor(); StopPlayback(); ReleaseRecording(false);
        if (session is not null)
        {
            try { session.Cache.Dispose(); } catch (Exception ex) { Say("一時音声の削除に失敗しました: " + ex.Message); }
        }
        _window?.BindLog(null); _window?.ShowLive("", false);
        _target = null; _loadedTarget = null; _placedTarget = null;
        if (_lastExternal?.Consumed == true) _lastExternal = null;
        _window?.ResetSession(); RefreshState();
    }
    public async Task StopAsync()
    {
        _stopping = true; _targetTimer.Stop(); _meterTimer.Stop(); _keyboard?.Dispose(); Dismiss(); await _work;
        while (_tasks.Count > 0)
        {
            await Task.WhenAll(_tasks.ToArray());
            // 完了TaskのObserve継続がまだDispatcher待ちでも、同期ループで占有しない。
            await Task.Yield();
        }
        foreach (var cancellation in _retired) cancellation.Dispose();
        _recognitionSlots.Dispose(); _window?.CloseForExit();
    }
}

using EchoType.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace EchoType.Windows;

/// <summary>OS全体の字幕をUIAで観測する。PCM入力APIとして扱わず、操作入力には使用しない。</summary>
public static class WindowsCaptionComparison
{
    private static readonly SemaphoreSlim SourceGate = new(1, 1);
    private static readonly object Sync = new();
    private static string _owner = "";
    public static string Owner => Volatile.Read(ref _owner);
    public static string BusyMessage => $"Win LiveCaptions UIAは{(Owner is { Length: > 0 } label ? "「" + label + "」" : "別の処理")}で使用中です。";
    private static void ReleaseSource()
    {
        Volatile.Write(ref _owner, "");
        SourceGate.Release();
    }
    private static Observation? _live;
    private static Observation? _recent;
    public static AsrResult CaptureRecording(Recording recording)
    {
        lock (Sync)
        {
            var observation = _live ?? _recent;
            if (observation is null || observation.Kind != recording.AudioSource.Kind ||
                observation.Device != recording.AudioSource.DeviceId && observation.Device != recording.DeviceName &&
                !(observation.Kind == AudioSourceKind.Microphone && (observation.Device.Length == 0 || recording.AudioSource.DeviceId.EndsWith(":" + observation.Device, StringComparison.Ordinal))) ||
                DateTimeOffset.UtcNow - observation.Last > TimeSpan.FromMinutes(2) || observation.DeviceChanges?.Changed == true)
                return Failure("録音時の字幕観測がありません。録音から再認識してください。", 0);
            var ranges = recording.RetainedRanges.Length > 0 ? recording.RetainedRanges : [new RetainedAudioRange(0, (int)(recording.SourceSeconds * 24000))];
            var parts = new List<string>();
            foreach (var range in ranges)
            {
                var start = recording.StartedAt.AddSeconds(range.StartSample / 24000d);
                var end = start.AddSeconds(range.SampleCount / 24000d);
                if (observation.Interference.Any(t => t >= start && t <= end)) return Failure("録音時の字幕に別音源が混在しているため採用しません。", 0);
                var result = observation.Timeline.Read(start, end);
                if (result.Coverage != CaptionTimelineCoverage.Complete) return Failure(result.Status, 0);
                if (result.Text.Length > 0) parts.Add(result.Text);
            }
            return new(AsrEngines.WindowsCaptions, string.Join("\n", parts), 0) { InputVerified = false, ComputeProfile = "録音保持区間のUIA観測" };
        }
    }
    internal sealed class ReplayInterval(DateTimeOffset start, DateTimeOffset end)
    { public readonly DateTimeOffset Start = start; public DateTimeOffset End = end; }
    private static readonly List<ReplayInterval> Replays = [];
    public static bool Suppresses(DateTimeOffset at)
    { lock (Sync) return Replays.Any(interval => at >= interval.Start && at <= interval.End); }
    internal static ReplayInterval BeginReplaySuppression(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        lock (Sync)
        {
            var interval = new ReplayInterval(start, end); Replays.Add(interval);
            if (Replays.Count > 128) Replays.RemoveRange(0, Replays.Count - 128);
            return interval;
        }
    }
    internal static void EndReplaySuppression(ReplayInterval interval, DateTimeOffset end)
    {
        if (end < interval.Start) throw new ArgumentOutOfRangeException(nameof(end));
        lock (Sync) interval.End = end;
    }
    public static bool Available => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe"));
    public static bool LiveReady(AudioSourceKind kind, AppSettings settings)
    {
        lock (Sync) return _live is { Ready: true } value && value.Kind == kind &&
            value.Device == (kind == AudioSourceKind.Playback ? settings.PlaybackDeviceId : settings.DeviceName) &&
            value.DeviceChanges?.Changed != true && DateTimeOffset.UtcNow - value.Last < TimeSpan.FromSeconds(2);
    }
    public static string LiveText(AudioSourceKind kind, DateTimeOffset start, DateTimeOffset end)
    {
        lock (Sync)
        {
            if (_live is not { Ready: true } value || value.Kind != kind) return "";
            var result = value.Timeline.Read(start, end);
            return result.Coverage == CaptionTimelineCoverage.Complete ? result.Text : "";
        }
    }
    public static string LivePreviewText(AudioSourceKind kind, DateTimeOffset start, DateTimeOffset at)
    {
        lock (Sync)
        {
            if (_live is not { Ready: true } value || value.Kind != kind || value.Last < start || at - value.Last > TimeSpan.FromSeconds(2)) return "";
            var result = value.Timeline.Read(start, at < value.Last ? at : value.Last);
            return result.Coverage == CaptionTimelineCoverage.Complete ? result.Text : "";
        }
    }
    public static async Task ObserveGlobalAsync(Action<WindowsCaptionSnapshot> updated, CancellationToken ct)
    {
        if (!await SourceGate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new InvalidOperationException(BusyMessage);
        Volatile.Write(ref _owner, "上部のOS字幕");
        try
        {
            using var lease = await CaptionProcessLease.AcquireAsync(ct, waitMilliseconds: 2000);
            WindowsLiveCaptionReader.Open();
            await WindowsLiveCaptionReader.ReadAsync(updated, ct);
        }
        finally { ReleaseSource(); }
    }

    private sealed class Observation(AudioSourceKind kind, string device)
    {
        public readonly ObservationTimeline Timeline = new();
        public readonly AudioSourceKind Kind = kind;
        public readonly string Device = device;
        public readonly List<DateTimeOffset> Interference = [];
        public DateTimeOffset Last;
        public bool Ready;
        public DefaultAudioDeviceChangeTracker? DeviceChanges;
        public string Session = "";
        public bool Microphone;
    }

    /// <summary>非Readyの欠測を、復帰前に終わる比較でも失わない時刻付き観測。</summary>
    internal sealed class ObservationTimeline
    {
        private readonly object _sync = new();
        private readonly CaptionTimeline _timeline = new();
        private readonly CaptionElementTimeline _elements = new();
        private bool _elementMode;
        private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _gaps = [];
        private DateTimeOffset? _last, _gapStarted, _discardedBefore;
        private int _revision;

        public void Observe(WindowsCaptionSnapshot snapshot)
        {
            lock (_sync)
            {
                if (_last is { } last && snapshot.ObservedAt <= last) return;
                if (!snapshot.Ready)
                {
                    // 前回の正常観測後から、今回の欠測検出までの間も保証できない。
                    _gapStarted ??= _last?.AddTicks(1) ?? snapshot.ObservedAt;
                }
                else
                {
                    if (_gapStarted is { } start)
                    {
                        _gaps.Add((start, snapshot.ObservedAt));
                        _gapStarted = null; _revision++;
                    }
                    if (snapshot.Elements is { } elements)
                    {
                        _elementMode = true;
                        _elements.Observe(snapshot.ObservedAt, snapshot.Session + ":" + _revision, elements);
                    }
                    else _timeline.Observe(snapshot.ObservedAt, snapshot.Text, snapshot.Session + ":" + _revision);
                }
                _last = snapshot.ObservedAt;
                // 本文と同じ2分を上限とし、極端に多い欠測も固定件数に制限する。
                var cutoff = snapshot.ObservedAt.AddMinutes(-2);
                var remove = _gaps.TakeWhile(gap => gap.End < cutoff).Count();
                remove = Math.Max(remove, _gaps.Count - 1024);
                if (remove > 0)
                {
                    _discardedBefore = _gaps[remove - 1].End.AddTicks(1);
                    _gaps.RemoveRange(0, remove);
                }
            }
        }

        public CaptionTimelineResult Read(DateTimeOffset start, DateTimeOffset end)
        {
            if (end < start) throw new ArgumentException("字幕の終了時刻は開始時刻以降にしてください。");
            lock (_sync)
            {
                if (_discardedBefore is { } discarded && start < discarded)
                    return new("", "指定時間帯の字幕観測は保持期間外です。", CaptionTimelineCoverage.Unavailable, discarded, _last);
                var result = _elementMode ? _elements.Read(start, end) : _timeline.Read(start, end);
                var gaps = _gaps.Where(gap => gap.Start <= end && gap.End >= start)
                    .Select(gap => new CaptionMissingRange(gap.Start, gap.End, "字幕を取得できませんでした。")).ToList();
                if (_gapStarted is { } open && open <= end) gaps.Add(new(open, end, "字幕を取得できませんでした。"));
                return gaps.Count == 0 ? result : result with { Text = _elementMode ? result.Text : "", Coverage = CaptionTimelineCoverage.Partial,
                    Status = "字幕を取得できない時間帯が含まれるため採用しません。", MissingRanges = [..result.MissingRanges, ..gaps] };
            }
        }
    }

    public static string DeviceError(AppSettings settings, AudioSourceKind kind)
    {
        using var devices = new MMDeviceEnumerator();
        if (kind == AudioSourceKind.Playback)
        {
            using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return output.ID == settings.PlaybackDeviceId ? "" : "OS字幕はWindowsの既定出力を使用します。選択した再生デバイスと一致していません。";
        }
        using var input = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        if (settings.DeviceNumber == -1) return "";
        if (settings.DeviceNumber == -2)
        {
            using var communications = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (communications.ID == input.ID) return "";
        }
        else
        {
            var name = settings.DeviceName;
            if (name.Length == 0) name = InputDevice.Resolve(RecordingDevices.InputDevices(), settings.DeviceNumber, "").Name;
            var matches = 0; var isDefault = false;
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (device)
                {
                    // WinMM名の31文字制限は一意に対応する場合だけ許す。
                    if (device.FriendlyName == name || name.Length == 31 && device.FriendlyName.StartsWith(name, StringComparison.Ordinal))
                    { matches++; isDefault |= device.ID == input.ID; }
                }
            if (matches == 1 && isDefault) return "";
        }
        return "OS字幕はWindowsの既定マイクを使用します。EchoTypeの入力マイクと一致していません。";
    }

    public static async Task ObserveLiveAsync(AudioSourceKind kind, AppSettings settings, Action<WindowsCaptionSnapshot> updated, CancellationToken ct, string? owner = null)
    {
        if (!await SourceGate.WaitAsync(TimeSpan.FromSeconds(2), ct))
            throw new InvalidOperationException(BusyMessage);
        Volatile.Write(ref _owner, owner ?? (kind == AudioSourceKind.Microphone ? "入力の認識" : "再生の認識"));
        WindowsCaptionConfiguration? configuration = null;
        CaptionProcessLease? processLease = null;
        var observation = new Observation(kind, kind == AudioSourceKind.Playback ? settings.PlaybackDeviceId : settings.DeviceName);
        try
        {
            processLease = await CaptionProcessLease.AcquireAsync(ct, waitMilliseconds: 2000);
            using var deviceChanges = DefaultAudioDeviceChangeTracker.Start(kind == AudioSourceKind.Microphone,
                kind == AudioSourceKind.Microphone && settings.DeviceNumber == -2);
            observation.DeviceChanges = deviceChanges;
            var error = DeviceError(settings, kind); if (error.Length > 0) throw new InvalidOperationException(error);
            WindowsLiveCaptionReader.Open();
            configuration = await ConfigureWithRecoveryAsync(kind == AudioSourceKind.Microphone, ct);
            observation.Session = configuration.Session; observation.Microphone = kind == AudioSourceKind.Microphone;
            lock (Sync) _live = observation;
            using var devices = new MMDeviceEnumerator();
            using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            await WindowsLiveCaptionReader.ReadAsync(snapshot =>
            {
                var interfering = kind == AudioSourceKind.Microphone && !deviceChanges.Changed && output.AudioMeterInformation.MasterPeakValue > .002f;
                lock (Sync)
                {
                    observation.Last = snapshot.ObservedAt; observation.Ready = snapshot.Ready;
                    observation.Timeline.Observe(snapshot);
                    if (interfering || Suppresses(snapshot.ObservedAt))
                        observation.Interference.Add(snapshot.ObservedAt);
                    observation.Interference.RemoveAll(t => t < snapshot.ObservedAt.AddMinutes(-2));
                }
                updated(snapshot);
            }, ct, everySnapshot: true);
        }
        finally
        {
            lock (Sync) if (ReferenceEquals(_live, observation)) { _recent = observation; _live = null; }
            try { await RestoreAsync(configuration); } finally { processLease?.Dispose(); ReleaseSource(); }
        }
    }

    public static async Task<AsrResult> RecognizeAsync(SpeechSegment segment, AppSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (segment.CaptionSnapshot is { } snapshot) return snapshot;
        var watch = Stopwatch.StartNew();
        if (segment.CaptionReplay) return await ReplayAsync(segment, ct);
        var deviceError = DeviceError(settings, segment.AudioSource.Kind);
        if (deviceError.Length > 0) return Failure(deviceError, watch.Elapsed.TotalSeconds);
        Observation? selected; lock (Sync) selected = _live;
        if ((selected is null || selected.Kind != segment.AudioSource.Kind) && Owner.Length > 0)
            return Failure(BusyMessage, watch.Elapsed.TotalSeconds);
        if (selected is not null)
        {
            var observedConfiguration = await WindowsLiveCaptionReader.ConfigureAsync(null, ct);
            if (observedConfiguration.Session != selected.Session || observedConfiguration.Microphone != selected.Microphone)
                return Failure("Windows字幕のマイク設定またはプロセスが変わりました。比較を選び直してください。", watch.Elapsed.TotalSeconds);
        }
        var end = segment.SegmentedAt ?? segment.StartedAt.AddSeconds(segment.Seconds);
        for (var retry = 0; retry < 12; retry++)
        {
            ct.ThrowIfCancellationRequested();
            lock (Sync)
            {
                if (_live is not { } observation || observation.Kind != segment.AudioSource.Kind)
                    return Failure("この音源のWindows字幕観測が開始されていません。競合・既定デバイス・字幕の準備状態を確認してください。", watch.Elapsed.TotalSeconds);
                if (observation.Device != (segment.AudioSource.Kind == AudioSourceKind.Playback ? settings.PlaybackDeviceId : settings.DeviceName))
                    return Failure("字幕の観測デバイスが切り替わりました。新しい発言から比較します。", watch.Elapsed.TotalSeconds);
                if (observation.DeviceChanges is { Changed: true })
                    return Failure("字幕観測中にWindowsの既定デバイスが変更されました。元に戻した場合も比較を選び直してください。", watch.Elapsed.TotalSeconds);
                if (observation.Last >= end)
                {
                    if (!observation.Ready) return Failure("Windowsライブキャプションの初期設定・日本語を確認してください。", watch.Elapsed.TotalSeconds);
                    if (observation.Interference.Any(t => t >= segment.StartedAt && t <= end))
                        return Failure("同じ時間帯に再生音があり、マイク字幕との区別を保証できないため採用しません。", watch.Elapsed.TotalSeconds);
                    var result = observation.Timeline.Read(segment.StartedAt, end);
                    return CaptionResult(result, watch.Elapsed.TotalSeconds, "UIA要素・行 / 150ms観測（音声時刻ではなく観測時刻）");
                }
            }
            await Task.Delay(150, ct);
        }
        return Failure("指定区間の字幕観測が追いついていません。", watch.Elapsed.TotalSeconds);
    }

    private static async Task<AsrResult> ReplayAsync(SpeechSegment segment, CancellationToken ct)
    {
        var watchStart = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        if (!await SourceGate.WaitAsync(TimeSpan.FromSeconds(2), ct))
            return Failure(BusyMessage, watch.Elapsed.TotalSeconds);
        Volatile.Write(ref _owner, segment.CaptionUse.Length > 0 ? segment.CaptionUse : "録音の認識比較");
        WindowsCaptionConfiguration? configuration = null;
        ReplayInterval? interval = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? reader = null;
        CaptionProcessLease? processLease = null;
        try
        {
            processLease = await CaptionProcessLease.AcquireAsync(ct, waitMilliseconds: 2000);
            using var deviceChanges = DefaultAudioDeviceChangeTracker.Start(watchCapture: false);
            WindowsLiveCaptionReader.Open();
            configuration = await ConfigureWithRecoveryAsync(false, ct);
            var timeline = new ObservationTimeline();
            var baseline = new ReplayBaseline();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var last = DateTimeOffset.MinValue; var changed = DateTimeOffset.MinValue; var previous = ""; var sync = new object();
            var latestReady = false;
            reader = WindowsLiveCaptionReader.ReadAsync(snapshot =>
            {
                lock (sync)
                {
                    baseline.Observe(snapshot);
                    timeline.Observe(snapshot);
                    if (AppContext.GetData("EchoType.Captions.SnapshotLog") is string snapshotLog)
                        File.AppendAllText(snapshotLog, System.Text.Json.JsonSerializer.Serialize(snapshot) + Environment.NewLine);
                    last = snapshot.ObservedAt; latestReady = snapshot.Ready;
                    if (!snapshot.Ready) return;
                    if (snapshot.Text != previous) { changed = last; previous = snapshot.Text; }
                    first.TrySetResult();
                }
            }, stop.Token, everySnapshot: true);
            var ready = first.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (await Task.WhenAny(reader, ready) == reader) await reader;
            await ready;
            using var wave = new WaveFileReader(new MemoryStream(segment.Wave, false));
            if (wave.TotalTime.TotalSeconds > 65) throw new InvalidOperationException("OS字幕比較は65秒以内の分割済み録音に対応します。");
            using var devices = new MMDeviceEnumerator(); using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (deviceChanges.Changed)
                return Failure("比較の準備中にWindowsの既定出力が変更されました。再比較してください。", watch.Elapsed.TotalSeconds);
            using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) => { if (e.Exception is { } error) finished.TrySetException(error); else finished.TrySetResult(); };
            output.Init(wave);
            // デバイス準備後にも観測を続ける。最短2秒・最終更新後1秒を満たす最新表示を
            // 再生開始と同じロック内で固定し、準備中の追記・訂正を今回へ持ち込まない。
            var baselineDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            DateTimeOffset started = default;
            while (DateTimeOffset.UtcNow < baselineDeadline)
            {
                await Task.Delay(150, ct);
                if (reader.IsCompleted) await reader;
                lock (sync)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (!baseline.TryBegin(now, out var fresh)) continue;
                    if (deviceChanges.Changed) break;
                    timeline = fresh!;
                    interval = BeginReplaySuppression(now, now.AddSeconds(wave.TotalTime.TotalSeconds + 10));
                    started = DateTimeOffset.UtcNow;
                    output.Play();
                    break;
                }
            }
            if (started == default) return Failure("既存のOS字幕が安定していないか既定出力が変更されました。他の音声を止めてから再比較してください。", watch.Elapsed.TotalSeconds);
            try { await finished.Task.WaitAsync(wave.TotalTime + TimeSpan.FromSeconds(5), ct); }
            finally { output.Stop(); }
            var ended = DateTimeOffset.UtcNow;
            var deadline = ended.AddSeconds(4);
            var settled = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(150, ct);
                if (reader.IsCompleted) await reader;
                lock (sync) if (latestReady && last >= ended.AddSeconds(1) && last - changed >= TimeSpan.FromMilliseconds(750)) { settled = true; break; }
            }
            CaptionTimelineResult result;
            DateTimeOffset lastTextChange;
            var stabilityConfirmed = settled ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            lock (sync) { result = timeline.Read(started, last < ended ? ended : last); lastTextChange = changed; }
            var tailWait = (DateTimeOffset.UtcNow - ended).TotalSeconds;
            if (!settled) result = result with { Coverage = CaptionTimelineCoverage.Partial, Status = "字幕の終了待ちが上限に達しました。部分本文は採用しません。",
                MissingRanges = [..result.MissingRanges, new(ended, DateTimeOffset.UtcNow, "終了時の字幕が安定していません。")] };
            var endConfiguration = await WindowsLiveCaptionReader.ConfigureAsync(null, ct);
            using var currentDevice = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (deviceChanges.Changed || endConfiguration.Session != configuration.Session || endConfiguration.Microphone != false || currentDevice.ID != device.ID)
                return Failure("比較中にOS字幕のマイク設定・デバイス・プロセスが変わったため採用しません。", watch.Elapsed.TotalSeconds);
            return CaptionResult(result, watch.Elapsed.TotalSeconds, $"UIA要素・行 / 150ms / 再生 {wave.TotalTime.TotalSeconds:F2}秒 / 既定出力: {device.FriendlyName}") with
            { CaptionReplaySeconds = (ended - started).TotalSeconds, CaptionSetupSeconds = (started - watchStart).TotalSeconds,
                CaptionTailWaitSeconds = tailWait, CaptionPlaybackEndedAt = ended,
                CaptionLastTextChangeAt = lastTextChange >= started ? lastTextChange : null,
                CaptionStabilityConfirmedAt = stabilityConfirmed,
                // 安定確認の750msや最低1秒の待機は、最後の本文更新までの遅延へ加算しない。
                CaptionTextSettleSeconds = settled && result.Coverage == CaptionTimelineCoverage.Complete && lastTextChange >= started
                    ? Math.Max(0, (lastTextChange - ended).TotalSeconds) : null };
        }
        catch (TimeoutException ex) when (processLease is null)
        {
            return Failure(ex.Message, watch.Elapsed.TotalSeconds);
        }
        finally
        {
            if (interval is not null) EndReplaySuppression(interval, DateTimeOffset.UtcNow.AddSeconds(2));
            stop.Cancel();
            try { if (reader is not null) await reader; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { try { await RestoreAsync(configuration); } finally { processLease?.Dispose(); ReleaseSource(); } }
        }
    }

    /// <summary>再生前に遅着した旧字幕を取り込み、安定した最新表示を今回の除外基準にする。</summary>
    internal sealed class ReplayBaseline
    {
        private WindowsCaptionSnapshot? _latest;
        private DateTimeOffset? _readyFrom;
        private DateTimeOffset _changed;

        public void Observe(WindowsCaptionSnapshot snapshot)
        {
            if (_latest is { } latest && snapshot.ObservedAt <= latest.ObservedAt) return;
            if (!snapshot.Ready) _readyFrom = null;
            else
            {
                if (_latest is not { Ready: true } previous || previous.Session != snapshot.Session ||
                    snapshot.ObservedAt - previous.ObservedAt > TimeSpan.FromSeconds(2)) _readyFrom = snapshot.ObservedAt;
                if (_readyFrom == snapshot.ObservedAt || !SameDisplay(_latest!, snapshot)) _changed = snapshot.ObservedAt;
            }
            _latest = snapshot;
        }

        public bool TryBegin(DateTimeOffset now, out ObservationTimeline? timeline)
        {
            timeline = null;
            if (_latest is not { Ready: true } latest || _readyFrom is not { } readyFrom ||
                latest.ObservedAt - readyFrom < TimeSpan.FromSeconds(2) ||
                latest.ObservedAt - _changed < TimeSpan.FromSeconds(1) || now < latest.ObservedAt ||
                now - latest.ObservedAt > TimeSpan.FromMilliseconds(300)) return false;
            timeline = new();
            timeline.Observe(latest);
            return true;
        }

        private static bool SameDisplay(WindowsCaptionSnapshot left, WindowsCaptionSnapshot right) =>
            left.Session == right.Session && left.Text == right.Text &&
            (left.Elements is null && right.Elements is null || left.Elements is { } a && right.Elements is { } b &&
                a.Length == b.Length && a.Zip(b).All(pair => pair.First.Id == pair.Second.Id && pair.First.Text == pair.Second.Text));
    }

    private static AsrResult Failure(string error, double seconds) => new(AsrEngines.WindowsCaptions, "", seconds, error) { InputVerified = false };
    private static AsrResult CaptionResult(CaptionTimelineResult result, double seconds, string profile) =>
        new(AsrEngines.WindowsCaptions, result.Coverage == CaptionTimelineCoverage.Complete ? result.Text : "", seconds,
            result.Coverage == CaptionTimelineCoverage.Complete ? "" : result.Status)
        { InputVerified = false, ComputeProfile = profile, CaptionCoverage = result.Coverage.ToString(),
            CaptionMissingRanges = result.MissingRanges, RejectedText = result.Coverage == CaptionTimelineCoverage.Partial ? result.Text : "" };
    private static async Task<WindowsCaptionConfiguration> ConfigureWithRecoveryAsync(bool microphone, CancellationToken ct)
    {
        var original = await WindowsLiveCaptionReader.ConfigureAsync(null, ct);
        if (original.Microphone is null) throw new InvalidOperationException("Windows字幕の現在のマイク設定を確認できません。");
        if (original.Microphone == microphone) return original;
        try
        {
            var result = await WindowsLiveCaptionReader.ConfigureAsync(microphone, ct);
            if (result.Session != original.Session) throw new InvalidOperationException("設定中にWindows字幕が再起動しました。");
            return result with { OriginalMicrophone = original.Microphone };
        }
        catch (Exception ex)
        {
            try
            {
                var current = await WindowsLiveCaptionReader.ConfigureAsync(null, CancellationToken.None);
                if (current.Session == original.Session && current.Microphone == microphone)
                    await WindowsLiveCaptionReader.ConfigureAsync(original.Microphone, CancellationToken.None);
            }
            catch (Exception restore) { throw new InvalidOperationException("Windows字幕の設定変更に失敗し、元のマイク設定への復元も確認できません: " + restore.Message, ex); }
            throw;
        }
    }
    private static async Task RestoreAsync(WindowsCaptionConfiguration? configuration)
    {
        if (configuration?.OriginalMicrophone is not { } original || configuration.Microphone == original) return;
        // 利用者が途中で変えた場合は上書きしない。同一OSプロセスで当アプリの変更が残るときだけ戻す。
        var current = await WindowsLiveCaptionReader.ConfigureAsync(null, CancellationToken.None);
        if (current.Session == configuration.Session && current.Microphone == configuration.Microphone)
            await WindowsLiveCaptionReader.ConfigureAsync(original, CancellationToken.None);
    }
}

using EchoType.Core;

namespace EchoType.Windows;

/// <summary>UIスレッドでCOMデバイス探索をせず、未確認時も別音源の字幕を採用しない。</summary>
public sealed class CaptionDeviceMonitor
{
    private readonly object _sync = new();
    private readonly Dictionary<AudioSourceKind, (string Key, bool Matches, DateTimeOffset At)> _states = [];
    private readonly HashSet<AudioSourceKind> _busy = [];
    public bool Matches(AppSettings settings, AudioSourceKind kind)
    {
        var key = $"{settings.DeviceNumber}:{settings.DeviceName}:{settings.PlaybackDeviceId}";
        lock (_sync)
        {
            var known = _states.TryGetValue(kind, out var state) && state.Key == key;
            if ((!known || DateTimeOffset.UtcNow - state.At >= TimeSpan.FromSeconds(1)) && _busy.Add(kind))
                _ = Task.Run(() =>
                {
                    var matches = false;
                    try { matches = WindowsCaptionComparison.DeviceError(settings, kind).Length == 0; }
                    catch (Exception) { /* デバイス探索失敗も即時認識だけ代替する。 */ }
                    finally { lock (_sync) { _states[kind] = (key, matches, DateTimeOffset.UtcNow); _busy.Remove(kind); } }
                });
            return known && state.Matches;
        }
    }
}

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;

namespace EchoType.Windows;

/// <summary>既定デバイスの往復変更を通知で記憶する。コールバック内ではCOM操作や待機をしない。</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class DefaultAudioDeviceChangeTracker(bool watchCapture, bool watchCommunicationsCapture = false)
    : IMMNotificationClient, IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private bool _registered;
    private int _changed, _disposed;
    public bool Changed => Volatile.Read(ref _changed) != 0;

    // コンストラクターは純粋な通知判定だけ。実デバイスへの購読は本番の入口に限定する。
    public static DefaultAudioDeviceChangeTracker Start(bool watchCapture, bool watchCommunicationsCapture = false)
    {
        var tracker = new DefaultAudioDeviceChangeTracker(watchCapture, watchCommunicationsCapture);
        try
        {
            tracker._enumerator = new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(tracker._enumerator.RegisterEndpointNotificationCallback(tracker));
            tracker._registered = true;
            return tracker;
        }
        catch { tracker.Dispose(); throw; }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // 再生字幕は常に既定出力、マイク字幕はそれに既定入力が加わる。
        // IDが元に戻っても一度観測した変更を取り消さない。
        if (flow == DataFlow.Render && role == Role.Console ||
            flow == DataFlow.Capture && watchCapture &&
            (role == Role.Console || watchCommunicationsCapture && role == Role.Communications))
            Interlocked.Exchange(ref _changed, 1);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string deviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var enumerator = _enumerator; _enumerator = null;
        if (enumerator is null) return;
        try { if (_registered) enumerator.UnregisterEndpointNotificationCallback(this); }
        finally { enumerator.Dispose(); }
    }
}

using System.Security.Principal;

namespace EchoType.Windows;

/// <summary>他プロセスとの字幕設定競合を防ぐ。所有スレッドを固定し、異常終了時はOSの放棄Mutexとして回復する。</summary>
internal sealed class CaptionProcessLease : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    public static Task<CaptionProcessLease> AcquireAsync(CancellationToken ct, string? testName = null, int waitMilliseconds = 20000)
    {
        // OS字幕は DesktopAgent と同じ Windows ユーザー内で共有する。
        var name = testName ?? @"Local\DesktopAgent.WindowsCaptions." + WindowsIdentity.GetCurrent().User!.Value;
        var ready = new TaskCompletionSource<CaptionProcessLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Factory.StartNew(() =>
        {
            Mutex? mutex = null;
            var owned = false; CaptionProcessLease? lease = null;
            try
            {
                mutex = new Mutex(false, name);
                try { owned = WaitHandle.WaitAny([mutex, ct.WaitHandle], waitMilliseconds) == 0; }
                catch (AbandonedMutexException) { owned = true; }
                ct.ThrowIfCancellationRequested();
                if (!owned) throw new TimeoutException("Win LiveCaptions UIAは別のプロセスで使用中です。");
                lease = new(); ready.TrySetResult(lease); lease._release.Wait();
            }
            catch (OperationCanceledException) { ready.TrySetCanceled(ct); }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { if (owned) mutex!.ReleaseMutex(); mutex?.Dispose(); lease?._release.Dispose(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return ready.Task;
    }
    private int _disposed;
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _release.Set(); }
}

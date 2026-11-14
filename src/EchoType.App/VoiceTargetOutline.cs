using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EchoType.App;

/// <summary>物理座標で追従する、フォーカスとクリックを奪わない入力先の枠。</summary>
internal sealed class VoiceTargetOutline : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Window? _window;
    private VoiceInputTarget? _target;
    private bool _updating;
    public event Action<VoiceInputTarget, Rect>? BoundsUpdated;
    public VoiceTargetOutline() => _timer.Tick += async (_, _) => await UpdateAsync();
    public void SetTarget(VoiceInputTarget? target)
    {
        _target = target; _window?.Hide();
        if (target is null) _timer.Stop(); else { _timer.Start(); _ = UpdateAsync(); }
    }
    private async Task UpdateAsync()
    {
        if (_updating || _target is null) return;
        _updating = true; var target = _target;
        try
        {
            var bounds = await Task.Run(target.HighlightBounds);
            if (!ReferenceEquals(_target, target)) return;
            BoundsUpdated?.Invoke(target, bounds);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) { _window?.Hide(); return; }
            if (_window is null)
            {
                _window = new Window { Title = "音声入力ターゲット枠", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
                    Topmost = true, IsHitTestVisible = false, Width = 1, Height = 1,
                    Content = new Border { BorderBrush = Brushes.Red, BorderThickness = new Thickness(3), Background = Brushes.Transparent } };
                _window.SourceInitialized += (_, _) =>
                {
                    var hwnd = new WindowInteropHelper(_window).Handle;
                    SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x20 | 0x80);
                };
            }
            var handle = new WindowInteropHelper(_window).EnsureHandle();
            SetWindowPos(handle, new nint(-1), (int)Math.Floor(bounds.X) - 2, (int)Math.Floor(bounds.Y) - 2,
                (int)Math.Ceiling(bounds.Width) + 4, (int)Math.Ceiling(bounds.Height) + 4, 0x10);
            _window.Show();
        }
        catch { if (ReferenceEquals(_target, target)) _window?.Hide(); }
        finally { _updating = false; }
    }
    public void Dispose() { _target = null; _timer.Stop(); _window?.Close(); _window = null; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

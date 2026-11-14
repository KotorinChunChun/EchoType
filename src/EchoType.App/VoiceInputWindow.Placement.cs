using EchoType.Core;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EchoType.App;

public partial class VoiceInputWindow
{
    private bool _autoPlacing;
    private bool _mouseWindowMove;
    private HwndSource? _moveSource;
    private void HandleUserMoveMessage(int message, nint wParam)
    {
        if (message == 0xA1) _mouseWindowMove = wParam == 2; // タイトルバーのマウス押下。
        if (message == 0x216 && _mouseWindowMove) _controller.PinForCurrentDisplay();
        if (message is 0x232 or 0xA2) _mouseWindowMove = false;
    }
    private nint UserMoveHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        HandleUserMoveMessage(message, wParam); return 0;
    }
    private bool _followingTarget;
    private Rect _observedTargetBounds = Rect.Empty;
    private int _observedGap = -1, _placementVersion;
    private readonly SemaphoreSlim _placementGate = new(1, 1);
    private double _preferredWidth, _preferredHeight;
    private readonly DispatcherTimer _sizeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private void InitializePlacement(VoiceInputSettings settings)
    {
        SourceInitialized += (_, _) =>
        {
            _moveSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _moveSource?.AddHook(UserMoveHook);
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _mouseWindowMove = false; };
        Closed += (_, _) => { _moveSource?.RemoveHook(UserMoveHook); _moveSource = null; };
        _preferredWidth = Width = settings.WindowWidth; _preferredHeight = Height = settings.WindowHeight;
        _outline.BoundsUpdated += FollowTarget;
        SizeChanged += (_, _) =>
        {
            if (_autoPlacing || !IsLoaded || WindowState != WindowState.Normal) return;
            _preferredWidth = Width; _preferredHeight = Height; _sizeSaveTimer.Stop(); _sizeSaveTimer.Start();
        };
        _sizeSaveTimer.Tick += (_, _) => SavePreferredSize();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ++_placementVersion; SavePreferredSize(); } _observedTargetBounds = Rect.Empty; };
        Closed += (_, _) => _sizeSaveTimer.Stop();
    }
    private async void FollowTarget(VoiceInputTarget target, Rect bounds)
    {
        if (!IsVisible || SettingsOpen || _autoPlacing || _followingTarget || bounds.IsEmpty || !ReferenceEquals(target, _highlightTarget)) return;
        var gap = _controller.Settings.TargetGap;
        if (bounds == _observedTargetBounds && gap == _observedGap) return;
        _observedTargetBounds = bounds; _observedGap = gap;
        var dpi = VisualTreeHelper.GetDpi(this);
        var padded = bounds; padded.Inflate(gap * dpi.DpiScaleX, gap * dpi.DpiScaleY);
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var rect) ||
            !padded.IntersectsWith(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top))) return;
        _followingTarget = true;
        try { await PlaceNearTargetAsync(target); }
        catch (Exception ex) { SetStatus("入力欄を避ける配置に失敗しました: " + ex.Message); }
        finally { _followingTarget = false; }
    }
    private void SavePreferredSize()
    {
        _sizeSaveTimer.Stop();
        try { _controller.SaveWindowSize(_preferredWidth, _preferredHeight); } catch (Exception ex) { SetStatus(ex.Message); }
    }
    public async Task PlaceNearTargetAsync(VoiceInputTarget? target)
    {
        if (!IsVisible) return;
        var version = ++_placementVersion;
        await _placementGate.WaitAsync();
        try
        {
        if (!IsVisible || version != _placementVersion) return;
        var captured = target;
        Rect bounds;
        try { bounds = target is null ? Rect.Empty : await Task.Run(target.HighlightBounds); }
        catch { bounds = Rect.Empty; }
        if (!IsVisible || version != _placementVersion || !ReferenceEquals(_highlightTarget, captured)) return;
        var areas = new List<VoiceWindowArea>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref ScreenRect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) areas.Add(new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        }, 0);
        if (areas.Count == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var obstacle = bounds.IsEmpty ? new VoiceWindowArea(Left * dpi.DpiScaleX, Top * dpi.DpiScaleY, 0, 0) : new VoiceWindowArea(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var placement = VoiceWindowLayout.Find(areas, obstacle, _preferredWidth * dpi.DpiScaleX, _preferredHeight * dpi.DpiScaleY, _controller.Settings.TargetGap * dpi.DpiScaleX);
        if (placement is null)
        {
            var work = areas.OrderBy(a => Math.Abs(a.X - obstacle.X) + Math.Abs(a.Y - obstacle.Y)).First();
            placement = new(work.X, work.Y, Math.Min(work.Width, _preferredWidth * dpi.DpiScaleX), Math.Min(work.Height, _preferredHeight * dpi.DpiScaleY));
            SetStatus("対象と重ならない空き領域がないため、画面内に配置しました。");
        }
        var p = placement.Value;
        _autoPlacing = true;
        try
        {
            WindowState = WindowState.Normal;
            MinWidth = 0; MinHeight = 0;
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, 0, (int)p.X, (int)p.Y, (int)p.Width, (int)p.Height, 0x14);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!IsVisible || version != _placementVersion || !ReferenceEquals(_highlightTarget, captured)) return;
            // モニター間移動のDPI変更後にも作業領域の物理サイズを守る。
            var destinationDpi = VisualTreeHelper.GetDpi(this);
            MinWidth = Math.Min(240, p.Width / destinationDpi.DpiScaleX);
            MinHeight = Math.Min(180, p.Height / destinationDpi.DpiScaleY);
            SetWindowPos(hwnd, 0, (int)p.X, (int)p.Y, (int)p.Width, (int)p.Height, 0x14);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        finally { _autoPlacing = false; }
        }
        finally { _placementGate.Release(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public ScreenRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref ScreenRect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out ScreenRect rect);
}

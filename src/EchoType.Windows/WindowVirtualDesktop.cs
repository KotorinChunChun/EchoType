using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EchoType.Windows;

internal static class WindowVirtualDesktop
{
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint window, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(nint window, in Guid id);
    }
    private static T Use<T>(Func<IManager, T> action, T fallback)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), true)!);
            return action((IManager)instance!);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException) { return fallback; }
        finally { if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
    }
    public static Guid Get(nint window) => Use(m => m.GetWindowDesktopId(window, out var id) >= 0 ? id : Guid.Empty, Guid.Empty);
    public static string Move(nint window, Guid id) => Use(m =>
    {
        if (m.GetWindowDesktopId(window, out var current) >= 0 && current == id) return "同じデスクトップ";
        var error = m.MoveWindowToDesktop(window, in id);
        return error >= 0 ? "保存デスクトップへ移動" : $"復元不可: 0x{error:X8}（現在のデスクトップを維持）";
    }, "COM未利用: 現在のデスクトップを維持");
    public static int? Number(Guid id)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is byte[] ids)
                for (var offset = 0; offset + 16 <= ids.Length; offset += 16)
                    if (new Guid(ids.AsSpan(offset, 16)) == id) return offset / 16 + 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return null; // 番号は参考値。復元には公開APIのGUIDを使う。
    }
}

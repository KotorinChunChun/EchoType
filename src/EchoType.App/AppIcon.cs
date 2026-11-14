namespace EchoType.App;

internal static class AppIcon
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/EchoType.App;component/Assets/EchoType.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("アプリアイコンを読み込めませんでした。");
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        // ストリームを閉じた後もNotifyIconが使える、所有権の独立したIconを返す。
        return (System.Drawing.Icon)icon.Clone();
    }
}

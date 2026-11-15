namespace EchoType.App;

internal static class WindowTitle
{
    public static string Version { get; } = typeof(WindowTitle).Assembly.GetName().Version!.ToString(3);

    public static string Create(string? page = null) => string.IsNullOrEmpty(page)
        ? $"EchoType - ver {Version}"
        : $"{page} - EchoType - ver {Version}";
}

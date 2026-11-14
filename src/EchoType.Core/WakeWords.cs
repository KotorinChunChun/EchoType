namespace EchoType.Core;

public static class WakeWords
{
    public static string[] Parse(string value) => value.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

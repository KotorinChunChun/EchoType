using EchoType.Core;
using System.Text.Json;

namespace EchoType.Windows;

public static class WhisperModelStore
{
    private const string SharedRoot = "C:/AI/Models/Speech/Whisper/Shared";
    private static readonly Lazy<Dictionary<string, string[]>> Models = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Asr", "whisper-models.json")))!);

    public static string? MachineRoot(AgentPaths paths) => IsDefault(paths) && !paths.HasModelOverride ? SharedRoot : null;

    public static string UserRoot(AgentPaths paths) => IsDefault(paths)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotorinToys", "Models", "Speech", "Whisper")
        : Path.Combine(paths.Model, "shared-whisper");

    private static bool IsDefault(AgentPaths paths) => string.Equals(paths.Root, AgentPaths.DefaultRoot, StringComparison.OrdinalIgnoreCase);

    public static bool HasWeights(AgentPaths paths, string engine) =>
        FindPublished(paths, engine) is not null || HasLegacyWeights(paths, engine);

    internal static string? FindPublished(AgentPaths paths, string engine)
    {
        if (paths.HasModelOverride || !Models.Value.TryGetValue(AsrEngines.ModelId(engine), out var model)) return null;
        foreach (var root in new[] { MachineRoot(paths), UserRoot(paths) })
        {
            if (root is null) continue;
            var directory = Path.Combine(root, model[0], model[1]);
            try
            {
                AgentPaths.RequireLocalPath(directory, true);
                var marker = Path.Combine(directory, "ready.json");
                AgentPaths.RequireLocalPath(marker);
                using var metadata = JsonDocument.Parse(File.ReadAllText(marker));
                var entry = metadata.RootElement;
                if (!entry.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var value) || value != 1 ||
                    entry.GetProperty("repo").GetString() != model[0] ||
                    entry.GetProperty("revision").GetString() != model[1]) continue;
                var files = entry.GetProperty("files");
                if (!files.TryGetProperty("model.bin", out _) || !files.TryGetProperty("config.json", out _) ||
                    !files.TryGetProperty("tokenizer.json", out _)) continue;
                foreach (var item in files.EnumerateObject())
                {
                    if (Path.GetFileName(item.Name) != item.Name || item.Name.Contains(':') || item.Name is "." or "..")
                        throw new InvalidDataException("モデルのファイル名が無効です。");
                    var file = Path.Combine(directory, item.Name);
                    AgentPaths.RequireLocalPath(file);
                    if (!item.Value.TryGetInt64(out var size) || size <= 0 || new FileInfo(file).Length != size)
                        throw new InvalidDataException("モデルのファイル長が一致しません。");
                }
                return directory;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or JsonException or KeyNotFoundException) { }
        }
        return null;
    }

    private static bool HasLegacyWeights(AgentPaths paths, string engine)
    {
        var directory = Path.Combine(paths.RecognitionModels, "asr", AsrEngines.ModelId(engine));
        var marker = Path.Combine(directory, "ready.json");
        if (!LocalAsrWorker.HasWeights(directory, false) || !File.Exists(Path.Combine(directory, "config.json")) || !File.Exists(marker)) return false;
        if (paths.HasModelOverride) return true;
        if (!Models.Value.TryGetValue(AsrEngines.ModelId(engine), out var model)) return false;
        try
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(marker));
            return metadata.RootElement.GetProperty("repo").GetString() == model[0] &&
                metadata.RootElement.GetProperty("revision").GetString() == model[1];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
}

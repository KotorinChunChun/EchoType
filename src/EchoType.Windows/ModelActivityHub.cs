using EchoType.Core;

namespace EchoType.Windows;

/// <summary>同じ保存先のサービスだけが状況を共有する。通常プロファイルと試験プロファイルは混ぜない。</summary>
public static class ModelActivityHub
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, WeakReference<ModelActivityRegistry>> Roots = new(StringComparer.OrdinalIgnoreCase);
    public static ModelActivityRegistry For(AgentPaths paths)
    {
        lock (Sync)
        {
            if (Roots.TryGetValue(paths.Root, out var entry) && entry.TryGetTarget(out var found)) return found;
            if (Roots.Count > 128)
                foreach (var key in Roots.Where(x => !x.Value.TryGetTarget(out _)).Select(x => x.Key).ToArray()) Roots.Remove(key);
            var registry = new ModelActivityRegistry(); Roots[paths.Root] = new(registry); return registry;
        }
    }
}

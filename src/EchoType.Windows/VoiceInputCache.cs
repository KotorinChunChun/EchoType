namespace EchoType.Windows;

/// <summary>画面セッションだけの暗号化音声。閉じた後の遅延書込みを拒否する。</summary>
public sealed class VoiceInputCache : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<string> _files = [];
    private bool _closed;
    private long _bytes;
    public string DirectoryPath { get; }
    public VoiceInputCache(AgentPaths paths)
    {
        DirectoryPath = Path.Combine(paths.Root, "cache", "voice-input", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath); AgentPaths.RequireLocalPath(DirectoryPath, true);
    }
    public string Save(int number, byte[] wave)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_bytes + wave.Length > 512L * 1024 * 1024) throw new InvalidOperationException("一時録音が512MBを超えました。編集文を確定して画面を閉じてください。");
            var file = Path.Combine(DirectoryPath, $"{number:D6}.wav.dpapi");
            if (_files.Contains(file)) throw new InvalidOperationException("同じ録音番号は保存できません。");
            SecureStore.AtomicWrite(file, SecureStore.Protect(wave)); _files.Add(file); _bytes += wave.Length; return file;
        }
    }
    public byte[] Read(string file)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_files.Contains(file)) throw new InvalidOperationException("この画面の録音ではありません。");
            return SecureStore.Unprotect(File.ReadAllBytes(file));
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return; _closed = true;
            foreach (var file in _files) File.Delete(file);
            _files.Clear(); Directory.Delete(DirectoryPath); // 所有している空のセッションフォルダーだけを削除する。
        }
    }
}

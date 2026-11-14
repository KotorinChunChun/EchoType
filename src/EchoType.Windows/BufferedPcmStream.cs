namespace EchoType.Windows;

/// <summary>認識エンジンの読み取りを待機させる有限長 PCM パイプ。溢れたときは時刻をずらさず停止。</summary>
public sealed class BufferedPcmStream(int capacity = 48000 * 5) : Stream
{
    private readonly object _sync = new();
    private readonly Queue<byte[]> _buffers = new();
    private int _queued, _offset;
    private bool _complete;
    private long _position;
    public bool Offer(byte[] bytes)
    {
        lock (_sync)
        {
            if (_complete) return false;
            if (_queued + bytes.Length > capacity) { _complete = true; _buffers.Clear(); _queued = 0; Monitor.PulseAll(_sync); return false; }
            _buffers.Enqueue(bytes); _queued += bytes.Length; Monitor.PulseAll(_sync); return true;
        }
    }
    public void Complete() { lock (_sync) { _complete = true; Monitor.PulseAll(_sync); } }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        lock (_sync)
        {
            var read = 0;
            while (read < count)
            {
                while (_buffers.Count == 0 && !_complete) Monitor.Wait(_sync);
                if (_buffers.Count == 0) break;
                var first = _buffers.Peek(); var size = Math.Min(count - read, first.Length - _offset);
                Array.Copy(first, _offset, buffer, offset + read, size); _offset += size; _queued -= size; read += size;
                if (_offset == first.Length) { _buffers.Dequeue(); _offset = 0; }
            }
            _position += read; return read;
        }
    }
    protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // System.Speech の COM ラッパーは非seek入力でも Length / Position を読む。
    public override long Length => long.MaxValue;
    public override long Position { get { lock (_sync) return _position; } set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            var target = origin == SeekOrigin.Current ? _position + offset : offset;
            if (origin == SeekOrigin.End || target != _position) throw new NotSupportedException("ライブ音声は巻き戻せません。");
            return _position;
        }
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

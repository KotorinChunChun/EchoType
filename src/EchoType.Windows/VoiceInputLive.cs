using System.Speech.Recognition;
using System.Speech.AudioFormat;

namespace EchoType.Windows;

public sealed record VoiceInputLiveResult(string Text, string Error = "");
public interface IVoiceInputLive : IDisposable
{
    Task Stopped => Task.CompletedTask;
    event Action<string>? Updated;
    void Feed(byte[] pcm);
    Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct);
}

/// <summary>1回の押下に専用のWindows認識ストリーム。マイクを直接開かない。</summary>
public sealed class VoiceInputLive : IVoiceInputLive
{
    private readonly BufferedPcmStream _stream = new();
    private readonly RecognitionPcmShortener _shortener = new();
    private readonly object _sync = new();
    private readonly List<string> _final = [];
    private string _partial = "", _error = "";
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _start;
    private SpeechRecognitionEngine? _engine;
    private bool _disposed;
    public event Action<string>? Updated;
    public VoiceInputLive() => _start = Task.Run(Start);
    private void Start()
    {
        try
        {
            var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "ja")
                ?? throw new InvalidOperationException("日本語のWindows音声認識が未導入です。");
            var engine = new SpeechRecognitionEngine(info);
            lock (_sync)
            {
                if (_disposed) { engine.Dispose(); return; }
                _engine = engine;
            }
            engine.LoadGrammar(WindowsSpeechGrammar.Create(info.Culture));
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(300);
            engine.BabbleTimeout = TimeSpan.Zero;
            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(100);
            engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(100);
            engine.SpeechHypothesized += (_, e) => Accept(e.Result.Text, false, WindowsSpeechGrammar.Confidence(e.Result));
            engine.SpeechRecognized += (_, e) => Accept(e.Result.Text, true, WindowsSpeechGrammar.Confidence(e.Result));
            engine.RecognizeCompleted += (_, e) => { lock (_sync) { if (e.Error is not null) _error = e.Error.Message; } _done.TrySetResult(); };
            engine.SetInputToAudioStream(_stream, new SpeechAudioFormatInfo(24000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch (Exception ex) { lock (_sync) _error = ex.Message; _done.TrySetResult(); }
    }
    private void Accept(string text, bool final, float confidence)
    {
        string display;
        lock (_sync)
        {
            if (_disposed || final && confidence <= 0 || !text.Any(char.IsLetterOrDigit)) return;
            if (final) { _final.Add(text); _partial = ""; } else _partial = text;
            display = string.Join("", _final) + _partial;
        }
        Updated?.Invoke(display);
    }
    public void Feed(byte[] pcm)
    {
        var shortened = _shortener.Push(pcm);
        if (shortened.Length > 0 && !_stream.Offer(shortened)) { lock (_sync) _error = "Windowsの途中認識が入力に追いつきません。全文認識は継続します。"; }
    }
    public async Task<VoiceInputLiveResult> CompleteAsync(CancellationToken ct)
    {
        var tail = _shortener.Push([], complete: true);
        if (tail.Length > 0) _stream.Offer(tail);
        _stream.Complete();
        try { await _done.Task.WaitAsync(TimeSpan.FromSeconds(3), ct); }
        catch (TimeoutException) { lock (_sync) _error = "Windowsの途中認識の確定が時間切れになりました。全文認識は継続します。"; }
        lock (_sync) return new(string.Join("", _final) + _partial, _error);
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _shortener.Dispose();
        _stream.Complete();
        _ = _start.ContinueWith(_ =>
        {
            try { _engine?.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
            finally { _engine?.Dispose(); _stream.Dispose(); }
        }, TaskScheduler.Default);
    }
}

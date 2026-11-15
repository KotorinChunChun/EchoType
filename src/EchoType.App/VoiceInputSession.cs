using EchoType.Core;
using EchoType.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EchoType.App;

internal sealed class VoiceInputSession(AgentPaths paths)
{
    public CancellationTokenSource Cancellation { get; } = new();
    public VoiceInputCache Cache { get; } = new(paths);
    public int RecordingSequence { get; set; }
    public ObservableCollection<VoiceInputLogEntry> Entries { get; } = [];
    public bool Closed { get; set; }
    public int Pending => Entries.Count(e => !e.Finished);
}

public sealed class VoiceInputLogEntry : INotifyPropertyChanged
{
    public required int Number { get; init; }
    public int? RetrySourceNumber { get; init; }
    public int RecordingNumber => RetrySourceNumber ?? Number;
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public string Time => Started.ToString("HH:mm:ss");
    public double Seconds { get; set; }
    public string Status { get; set; } = "録音中";
    public string LiveText { get; set; } = "";
    public string LiveEngine { get; set; } = "windows";
    public string LiveError { get; set; } = "";
    public string CachePath { get; set; } = "";
    public string FileName => Path.GetFileName(CachePath);
    public bool CanReplay => CachePath.Length > 0;
    public AsrResult[] Results { get; set; } = [];
    public SpeechSegment? Metadata { get; set; }
    public VoiceInputSettings? RecognitionSettings { get; set; }
    public bool DraftHandled { get; set; }
    public bool Finished { get; set; }
    public bool LiveFinished { get; set; }
    public string Details => (LiveEngine.Length == 0 ? "" : $"{LiveEngine} 途中認識: {LiveText}\n{LiveError}\n") + string.Join("\n", Results.Select(r => $"{RecognitionSettings?.FinalSpeech().Model}: 待ち {r.PendingSeconds + r.QueueSeconds:F2}秒 / 認識 {r.RecognitionSeconds ?? r.ProcessingSeconds:F2}秒 / {r.Text} {r.Error}"));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

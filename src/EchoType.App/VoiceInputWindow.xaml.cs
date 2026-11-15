using EchoType.Core;
using EchoType.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoType.App;

public partial class VoiceInputWindow : Window
{
    private readonly VoiceInputController _controller;
    private VoiceInputSettingsWindow? _settingsWindow;
    public bool SettingsOpen => _settingsWindow?.IsVisible == true;
    public void CloseSettings() => _settingsWindow?.Close();
    public VoiceInputCard[] PreviewCards { get; }
    private bool _closing, _picking;
    private bool _initializing = true, _refreshingDevices, _refreshingAutomatic, _automaticLayout;
    private readonly VoiceTargetOutline _outline = new();
    private VoiceInputTarget? _highlightTarget;
    private AudioMeter _displayMeter = new(0, 0, []);
    public VoiceInputWindow(VoiceInputController controller, VoiceInputSettings settings)
    {
        InitializeComponent(); _controller = controller;
        UnsentDrafts.ItemsSource = controller.UnsentDraftHistory;
        InitializePlacement(settings);
        StatusText.Text = settings.GetRecordingShortcut().Label() + "を1秒以上押したまま話し、離すと文字起こしします。";
        CapturePanel.IsExpanded = settings.CapturePanelExpanded;
        ShowAutomaticSettings(settings);
        ReadCurrentText.IsChecked = settings.ReplaceTargetText;
        CaretPosition.Text = settings.ReplaceTargetText ? "置換予定文字列" : "送信予定文字列";
        PreviewCards = Enumerable.Range(0, 8).Select(i => new VoiceInputCard(settings.Models.ElementAtOrDefault(i) ?? "", controller.ModelSpeedOrder)).ToArray();
        foreach (var card in PreviewCards) card.EngineChanged += () =>
        {
            if (_initializing) return;
            try { controller.SaveSettings(controller.Settings with { Models = PreviewCards.Select(c => c.EngineId).ToArray() }); }
            catch (Exception ex) { ShowProfileSettings(controller.Settings); SetStatus(ex.Message); }
        };
        Cards.ItemsSource = PreviewCards;
        FormattingModel.ItemsSource = VoiceInputSettings.FormattingModels; FormattingModel.SelectedItem = settings.FormattingModel;
        FormattingEffort.ItemsSource = VoiceInputSettings.FormattingEfforts; FormattingEffort.SelectedItem = settings.FormattingEffort;
        RefreshDevices(settings.Recognition);
        _initializing = false;
        ShowMeter(null);
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; _controller.Dismiss(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _controller.Dismiss(); } };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.IsRepeat || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _controller.AppendFirstShortcut();
            else if (ConfirmButton.IsEnabled) await _controller.ConfirmAsync();
        };
        Loaded += (_, _) => UpdateCaret(); Editor.SizeChanged += (_, _) => { UpdateCaret(); QueueDiff(); };
        BeforeText.SizeChanged += (_, _) => QueueDiff();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ShowAutomaticSettings(_controller.Settings);
            _outline.SetTarget(IsVisible ? _highlightTarget : null);
        };
        Closed += (_, _) => _outline.Dispose();
        foreach (var group in new FrameworkElement[] { TargetGroup, CapturePanel, ComparisonViewport, DraftGroup, DraftActions, StatusText })
            group.SizeChanged += (_, _) => UpdateMinimumLayoutHeight();
    }
    private void RefreshDevices(AppSettings settings)
    {
        _refreshingDevices = true;
        try
        {
        var devices = RecordingDevices.InputDevices(); DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(d => d.Number == settings.DeviceNumber && (settings.DeviceName.Length == 0 || d.Name == settings.DeviceName))
            ?? devices.FirstOrDefault(d => d.Name == settings.DeviceName);
        }
        finally { _refreshingDevices = false; }
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        try { _controller.ForgetDeviceSelection(); RefreshDevices(_controller.Settings.Recognition); ShowMeter(null); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _refreshingDevices || DeviceCombo.SelectedItem is not EchoType.Windows.InputDevice device) return;
        try { _controller.SelectDevice(device); ShowMeter(null); }
        catch (Exception ex) { RefreshDevices(_controller.Settings.Recognition); SetStatus(ex.Message); }
    }
    public void ShowMeter(AudioMeter? meter, string deviceName = "", bool recording = true)
    {
        _displayMeter = meter ?? new(0, 0, []);
        LevelBar.Value = _displayMeter.Db + 96;
        LevelBar.Foreground = _displayMeter.Peak >= .98 ? Brushes.IndianRed : new SolidColorBrush(Color.FromRgb(22, 130, 110));
        LevelText.Text = $"入力 {_displayMeter.Db:F1} dBFS / peak {_displayMeter.Peak:P0}" + (_displayMeter.Peak >= .98 ? " 飽和" : "");
        ActualDeviceText.Text = meter is null ? "録音待ち / " + (DeviceCombo.SelectedItem is EchoType.Windows.InputDevice device ? device.Name : "デバイス未選択") : (recording ? "● 録音中 / " : "入力モニター / ") + deviceName;
        ActualDeviceText.ToolTip = ActualDeviceText.Text;
        DrawWave();
    }
    private void Wave_SizeChanged(object sender, SizeChangedEventArgs e) => DrawWave();
    private void DrawWave()
    {
        if (WaveCanvas is null || WaveLine is null) return;
        var width = WaveCanvas.ActualWidth; var height = WaveCanvas.ActualHeight;
        var points = new PointCollection(); var waveform = _displayMeter.Waveform;
        var gain = WaveformScale.DisplayGain(waveform);
        if (waveform.Length == 0) { points.Add(new(0, height / 2)); points.Add(new(width, height / 2)); }
        else for (var i = 0; i < waveform.Length; i++) points.Add(new(width * i / Math.Max(1, waveform.Length - 1), height / 2 - Math.Clamp(waveform[i] * gain, -1, 1) * height * .47));
        WaveLine.Points = points; WaveCanvas.ToolTip = $"波形表示倍率 ×{gain:F1}（録音音量は変更しません）";
    }
    public void ShowTarget(VoiceInputTarget? target, string error = "")
    {
        _highlightTarget = target; _outline.SetTarget(IsVisible ? target : null);
        TargetPath.Text = target is null ? "" : target.ApplicationName + " | " + target.UiaPath; TargetPath.ToolTip = target is null ? error : TargetPath.Text;
    }
    public void SetStatus(string message) => StatusText.Text = message;
    public void SetBusy(bool busy)
    {
        SetActivity(false, 0, busy);
    }
    public void SetActivity(bool recording, int pending, bool exclusive)
    {
        _busy = exclusive; _recording = recording;
        RecordingTint.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        ReadCurrentText.IsEnabled = !exclusive && !recording;
        UnsentDrafts.IsEnabled = !exclusive && !recording;
        SettingsButton.IsEnabled = !exclusive && !recording && pending == 0;
        Editor.IsReadOnly = exclusive; FormattingModel.IsEnabled = FormattingEffort.IsEnabled = !exclusive;
        TargetButton.IsEnabled = !exclusive && !recording; DevicePanel.IsEnabled = !exclusive && !recording;
        foreach (var card in PreviewCards) { card.SetBusy(exclusive); card.SetSelectionLocked(exclusive || recording || pending > 0); }
        UpdateConfirm();
    }
    public void BindLog(System.Collections.IEnumerable? entries) { if (!ReferenceEquals(QueueLog.ItemsSource, entries)) QueueLog.ItemsSource = entries; }
    public void ShowLive(string text, bool final)
    {
        var engine = AsrEngines.Get(_controller.Settings.LiveEngine).Name;
        LiveLabel.Text = final ? $"{engine} 確定（この回の長押し音声）" : _controller.Settings.LiveEngine == AsrEngines.WindowsCaptions ? engine : "Windows 速報（認識できた文字から即時更新）";
        LiveText.Text = text;
    }
    public void ResetResults()
    {
        foreach (var card in PreviewCards) card.Show(null);
    }
    public void ResetSession()
    {
        ResetFormattingDiff(); DraftText = ""; ShowTarget(null);
        ComparisonViewport.ScrollToTop(); OuterViewport.ScrollToTop();
    }
    public void Result(AsrResult result)
    {
        foreach (var card in PreviewCards.Where(c => c.EngineId == result.EngineId)) card.Show(result);
        UpdateSpeeds();
    }
    public void UpdateSpeeds()
    {
        var max = PreviewCards.Max(c => c.Seconds);
        foreach (var card in PreviewCards) card.SetSpeed(max);
    }
    public void CloseForExit() { _closing = true; Close(); }
    private bool _busy, _recording;
    public string DraftText { get => Editor.Text; set { Editor.Text = value; Editor.CaretIndex = Editor.Text.Length; } }
    public int DraftCaretIndex => Editor.CaretIndex;
    public string SelectedFormattingModel => FormattingModel.SelectedItem as string ?? "gpt-6-luna";
    public string SelectedFormattingEffort => FormattingEffort.SelectedItem as string ?? "low";
    public bool EditorHasFocus => Editor.IsKeyboardFocusWithin;
    private void Editor_Changed(object sender, TextChangedEventArgs e) { UpdateConfirm(); UpdateCaret(); QueueDiff(); }
    private void UpdateConfirm()
    {
        if (_controller is null || ConfirmButton is null || FormatButton is null || SimpleFormatButton is null) return;
        FormatButton.IsEnabled = SimpleFormatButton.IsEnabled = !_busy && !_recording && !string.IsNullOrWhiteSpace(Editor.Text);
        ConfirmButton.IsEnabled = !_busy && !_recording && (_controller.Settings.ReplaceTargetText ? _controller.ReplacementReady : !string.IsNullOrWhiteSpace(Editor.Text));
    }
    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateCaret();
    private void Editor_Scrolled(object sender, ScrollChangedEventArgs e) { UpdateCaret(); QueueDiff(); }
    private void UpdateCaret()
    {
        if (Editor is null || CaretMarker is null || CaretPosition is null) return;
        var line = Math.Max(0, Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex));
        var start = Math.Max(0, Editor.GetCharacterIndexFromLineIndex(line));
        CaretMarker.ToolTip = $"{line + 1}行 {Editor.CaretIndex - start + 1}列";
        var rect = Editor.GetRectFromCharacterIndex(Editor.CaretIndex);
        CaretMarker.Visibility = rect.IsEmpty ? Visibility.Hidden : Visibility.Visible;
        if (rect.IsEmpty) return;
        Canvas.SetLeft(CaretMarker, rect.X); Canvas.SetTop(CaretMarker, rect.Y); CaretMarker.Height = Math.Max(18, rect.Height);
    }
    public void Append(VoiceInputCard card) => _controller.Append(card);
    public bool InsertDraft(string text)
    {
        var caret = Editor.CaretIndex;
        if (Editor.Text.Length + text.Length > Editor.MaxLength) { SetStatus("編集欄は16000文字までです。"); return false; }
        Editor.Text = Editor.Text.Insert(caret, text);
        Editor.CaretIndex = caret + text.Length; Editor.Focus(); UpdateCaret(); return true;
    }
    private void Play_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: VoiceInputLogEntry entry }) _controller.Play(entry); }
    private void StopPlayback_Click(object sender, RoutedEventArgs e) => _controller.StopPlayback();
    private void Append_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VoiceInputCard card }) Append(card);
    }
    private void Delete_Click(object sender, RoutedEventArgs e) { _controller.ClearCandidates(); SetStatus("完了済みの候補だけを消去しました。処理中・録音中の音声は継続します。"); }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _settingsWindow = new VoiceInputSettingsWindow(_controller, PreviewCards.Select(c => c.EngineId).ToArray()) { Owner = this };
        try { _settingsWindow.ShowDialog(); } finally { _settingsWindow = null; }
    }
    public void ShowProfileSettings(VoiceInputSettings settings)
    {
        var initializing = _initializing; _initializing = true;
        try {
        for (var i = 0; i < PreviewCards.Length; i++) PreviewCards[i].EngineId = settings.Models.ElementAtOrDefault(i) ?? "";
        FormattingModel.SelectedItem = settings.FormattingModel; FormattingEffort.SelectedItem = settings.FormattingEffort;
        ShowAutomaticSettings(settings);
        } finally { _initializing = initializing; }
    }
    private void RememberCorrection_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.CanEditPersonalization) { SetStatus("処理完了後に個人辞書を開いてください。"); return; }
        var window = new PersonalDictionaryWindow(_controller, BeforeText.SelectedText, Editor.SelectedText) { Owner = this };
        window.ShowDialog(); _controller.RefreshPersonalization();
    }
    public void ShowReedit(string text) { ResetFormattingDiff(); DraftText = text; CaretPosition.Text = "置換予定文字列"; }
    private async void UndoInput_Click(object sender, RoutedEventArgs e) => await _controller.UndoLastInputAsync(false);
    private async void ReeditInput_Click(object sender, RoutedEventArgs e) => await _controller.UndoLastInputAsync(true);
    private async void RetryRecognition_Click(object sender, RoutedEventArgs e) { if (QueueLog.SelectedItem is VoiceInputLogEntry entry) await _controller.RetryRecognitionAsync(entry); }
    private void ProcessingDetails_Click(object sender, RoutedEventArgs e) => new VoiceTimingWindow(_controller) { Owner = this }.ShowDialog();
    private async void Confirm_Click(object sender, RoutedEventArgs e) => await _controller.ConfirmAsync();
    private async void Format_Click(object sender, RoutedEventArgs e) => await _controller.FormatAsync();
    private async void SimpleFormat_Click(object sender, RoutedEventArgs e) => await _controller.FormatAsync(true);
    private void CapturePanel_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || !ReferenceEquals(sender, e.Source)) return;
        try { _controller.SetCapturePanelExpanded(CapturePanel.IsExpanded); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    public void ShowFormattingDiff(string before, string after)
    {
        BeforeText.Text = before; BeforePane.Visibility = Visibility.Visible;
        BeforeColumn.Width = new GridLength(1, GridUnitType.Star); UpdateDraftLayout();
        DraftText = after; QueueDiff();
    }
    public bool HasFormattingDiff => BeforePane.Visibility == Visibility.Visible;
    public void ResetFormattingDiff()
    {
        BeforePane.Visibility = Visibility.Collapsed; BeforeColumn.Width = new GridLength(0); UpdateDraftLayout();
        BeforeText.Clear(); BeforeHighlights.Children.Clear(); AfterHighlights.Children.Clear();
    }
    private bool _diffQueued;
    private void Diff_Scrolled(object sender, ScrollChangedEventArgs e) => QueueDiff();
    private void QueueDiff()
    {
        if (BeforePane is null || BeforePane.Visibility != Visibility.Visible || _diffQueued) return;
        _diffQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            _diffQueued = false;
            if (!HasFormattingDiff) return;
            var diff = VoiceTextDiff.Compare(BeforeText.Text, Editor.Text);
            PaintDiff(BeforeText, BeforeHighlights, diff.Before, "#FFEBE9");
            PaintDiff(Editor, AfterHighlights, diff.After, "#DAFBE1");
        }));
    }
    private static void PaintDiff(TextBox box, Canvas canvas, bool[] changed, string color)
    {
        canvas.Children.Clear(); var text = box.Text; var logicalLine = 0; var scanned = 0;
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        for (var line = 0; line < box.LineCount; line++)
        {
            var index = box.GetCharacterIndexFromLineIndex(line); if (index < 0) continue;
            while (scanned < index) { if (text[scanned++] == '\n') logicalLine++; }
            if (logicalLine >= changed.Length || !changed[logicalLine]) continue;
            var rect = box.GetRectFromCharacterIndex(index);
            if (rect.IsEmpty || rect.Bottom < 0 || rect.Top > box.ActualHeight) continue;
            var stripe = new Border { Background = brush, Width = Math.Max(0, box.ActualWidth - 18), Height = Math.Max(18, rect.Height) };
            Canvas.SetTop(stripe, rect.Top); canvas.Children.Add(stripe);
        }
    }
    private void AutomaticInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _refreshingAutomatic) return;
        try { _controller.SetAutomaticInput(AutomaticInputToggle.IsChecked == true); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void AutomaticOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _refreshingAutomatic) return;
        try { _controller.SetAutomaticOptions(AutomaticFormattingToggle.IsChecked == true,
            _controller.TemporarilyPinned ? _controller.Settings.AutomaticConfirm : AutomaticConfirmToggle.IsChecked == true); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    public void ShowAutomaticSettings(VoiceInputSettings settings)
    {
        _refreshingAutomatic = true;
        try
        {
            AutomaticInputToggle.IsChecked = settings.AutomaticInput;
            AutomaticFormattingToggle.IsChecked = settings.AutomaticFormatting;
            AutomaticConfirmToggle.IsChecked = settings.AutomaticConfirm && !_controller.TemporarilyPinned;
            AutomaticFormattingToggle.IsEnabled = settings.AutomaticInput;
            AutomaticConfirmToggle.IsEnabled = settings.AutomaticInput && !_controller.TemporarilyPinned;
            _automaticLayout = settings.AutomaticInput;
            Cards.Visibility = CandidateHeading.Visibility = _automaticLayout ? Visibility.Collapsed : Visibility.Visible;
            LiveGroup.Visibility = _automaticLayout ? Visibility.Collapsed : Visibility.Visible;
            CapturePanel.Header = _automaticLayout ? "録音デバイス・波形" : "録音デバイス・波形・リアルタイム文字起こし";
            ComparisonRow.Height = _automaticLayout ? GridLength.Auto : new(1, GridUnitType.Star);
            DraftRow.Height = _automaticLayout ? new(1, GridUnitType.Star) : GridLength.Auto;
            UpdateDraftLayout();
        }
        finally { _refreshingAutomatic = false; }
    }
    private void UpdateDraftLayout()
    {
        var minimum = HasFormattingDiff ? 185 : 145;
        DraftGrid.MinHeight = _automaticLayout ? minimum : 0;
        DraftGrid.Height = _automaticLayout ? double.NaN : minimum;
        UpdateMinimumLayoutHeight();
    }
    private void UpdateMinimumLayoutHeight()
    {
        if (!_automaticLayout)
        {
            MainLayout.MinHeight = Math.Max(550, TargetGroup.ActualHeight + CapturePanel.ActualHeight + DraftGroup.ActualHeight + 160);
            return;
        }
        // Starで広がった編集欄の実高さを加算すると、最小高さが連続して増えるため必要寸法だけを使う。
        static double OuterHeight(FrameworkElement element) => element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        var minimum = OuterHeight(TargetGroup) + OuterHeight(CapturePanel) + OuterHeight(ComparisonViewport) +
            DraftGrid.MinHeight + OuterHeight(DraftActions) + OuterHeight(StatusText) +
            DraftGroup.Margin.Top + DraftGroup.Margin.Bottom + DraftGroup.Padding.Top + DraftGroup.Padding.Bottom;
        MainLayout.MinHeight = Math.Max(550, minimum);
    }
    private async void ReadCurrentText_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await _controller.SetReplaceTargetTextAsync(ReadCurrentText.IsChecked == true);
        CaretPosition.Text = ReadCurrentText.IsChecked == true ? "置換予定文字列" : "送信予定文字列";
        UpdateConfirm();
    }
    public void LoadTargetText(VoiceInputTarget target)
    {
        ResetFormattingDiff();
        DraftText = target.OriginalText;
        Editor.Select(Math.Clamp(target.SelectionStart, 0, Editor.Text.Length), Math.Clamp(target.SelectionLength, 0, Editor.Text.Length - Math.Clamp(target.SelectionStart, 0, Editor.Text.Length)));
        UpdateCaret(); UpdateConfirm();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _controller.Dismiss();
    private void Target_Down(object sender, MouseButtonEventArgs e)
    {
        _picking = TargetButton.CaptureMouse();
        if (_picking) { _outline.SetTarget(null); Mouse.OverrideCursor = Cursors.Cross; SetStatus("入力先の文字位置までドラッグして離してください。"); }
        e.Handled = true;
    }
    private async void Target_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        GetPhysicalCursorPos(out var p); _picking = false; TargetButton.ReleaseMouseCapture(); Mouse.OverrideCursor = null;
        await _controller.PickTargetAsync(new Point(p.X, p.Y)); e.Handled = true;
    }
    private void Target_LostCapture(object sender, MouseEventArgs e) { _picking = false; Mouse.OverrideCursor = null; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
}

public sealed class VoiceInputCard(string engine, string[]? speedOrder = null) : INotifyPropertyChanged
{
    public event Action? EngineChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AsrEngine[] Engines => [.. VoiceInputSettings.LocalEngines.OrderBy(e => Array.IndexOf(speedOrder ?? VoiceModelSpeed.Fallback, e.Id)), new("", "未割り当て")];
    private string _engine = engine, _text = "";
    private bool _busy, _succeeded, _selectionLocked;
    public string EngineId { get => _engine; set { if (_engine == value || value is null) return; _engine = value; Show(null); EngineChanged?.Invoke(); } }
    public string Text { get => _text; set { _text = value; Notify(); } }
    public string Summary { get; private set; } = "録音待ち";
    public double Seconds { get; private set; }
    public double SpeedPercent { get; private set; }
    public bool Editable => !_busy && !_selectionLocked;
    public bool ReadOnly => true;
    public bool CanConfirm => !_busy && _succeeded && !string.IsNullOrWhiteSpace(Text);
    public bool CanClear => !_busy;
    public void SetBusy(bool busy) { _busy = busy; Notify(); }
    public void SetSelectionLocked(bool locked) { _selectionLocked = locked; Notify(); }
    public bool IsProcessing { get; private set; }
    public double CompletionPercent { get; private set; }
    public void ShowAccumulated(AsrResult[] results, VoiceInputProgress progress)
    {
        _text = string.Concat(results.Where(r => r.Succeeded).Select(r => r.Text.Trim()));
        _succeeded = _text.Length > 0; Seconds = progress.Seconds;
        IsProcessing = EngineId.Length > 0 && progress.Completed < progress.Total;
        CompletionPercent = progress.Total == 0 ? 0 : 100d * progress.Completed / progress.Total;
        Summary = EngineId.Length == 0 ? "未割り当て" : $"{progress.Total}件中 {progress.Completed}件完了 / 累計 {Seconds:F2}秒" + (progress.Failed > 0 ? $" / 失敗 {progress.Failed}件（ログ参照）" : "");
        Notify();
    }
    public void Show(AsrResult? result)
    {
        _text = result?.Text ?? ""; _succeeded = result?.Succeeded == true && _text.Length > 0;
        Seconds = result?.ProcessingSeconds ?? 0;
        Summary = result is null ? EngineId.Length == 0 ? "未割り当て" : "録音待ち" : $"{result.Status} / {result.ProcessingSeconds:F2}秒";
        IsProcessing = false; SpeedPercent = 0; CompletionPercent = result is null ? 0 : 100; Notify();
    }
    public void SetSpeed(double max) { SpeedPercent = max > 0 ? Seconds / max * 100 : 0; Notify(); }
    public void Consume() { _succeeded = false; Notify(); }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

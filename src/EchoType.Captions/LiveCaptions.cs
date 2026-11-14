using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

namespace EchoType.Captions;

/// <summary>OS字幕だけを読み取る。音声源や発言者を推測しない。</summary>
internal static class LiveCaptions
{
    public static int Run()
    {
        using var current = Process.GetCurrentProcess();
        while (true)
        {
            string text = "", status = "Windowsライブキャプションを開いてください。", session = "";
            var readyForAudio = false;
            var elements = new List<object>();
            try
            {
                foreach (var process in Process.GetProcessesByName("LiveCaptions"))
                {
                    using (process)
                    {
                        if (process.SessionId != current.SessionId) continue;
                        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe");
                        if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                        session = process.Id + ":" + process.StartTime.ToUniversalTime().Ticks;
                        var handle = CaptionConfiguration.CaptionWindow(process.Id);
                        var window = handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle);
                        var captions = window?.FindAll(TreeScope.Descendants, new AndCondition(
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
                        if (captions is null || captions.Count == 0)
                        {
                            var ready = window?.FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.AutomationIdProperty, "ReadyToCaptionTextBlock"));
                            status = ready is null ? "Windows側の初期設定・字幕言語を確認してください。" : "Windowsライブキャプション（OS字幕・音声待ち）";
                            readyForAudio = ready is not null;
                            continue;
                        }
                        var parts = new List<string>();
                        foreach (AutomationElement caption in captions)
                        {
                            if (parts.Count >= 32) throw new InvalidOperationException("字幕要素が多すぎます。");
                            var content = caption.Current.Name ?? "";
                            var source = "Name";
                            if (caption.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                            {
                                content = ((TextPattern)pattern).DocumentRange.GetText(16001);
                                source = "TextPattern";
                            }
                            content = content.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
                            if (content.Length > 16000) throw new InvalidOperationException("字幕本文が長すぎます。");
                            parts.Add(content);
                            elements.Add(new { Id = string.Join(".", caption.GetRuntimeId()), Text = content,
                                Lines = content.Split('\n'), Source = source });
                        }
                        text = string.Join("\n", parts);
                        if (text.Length > 16000) throw new InvalidOperationException("字幕本文が長すぎます。");
                        status = "Windowsライブキャプション（OS字幕）";
                        readyForAudio = true;
                        break;
                    }
                }
            }
            catch (Exception) { text = ""; elements.Clear(); readyForAudio = false; status = "Windows字幕を取得できません。Windows側のウィンドウを開き直してください。"; }
            // 毎回応答することで親がUIA停止を検出できる。ログファイルには書かない。
            Console.WriteLine(JsonSerializer.Serialize(new { Text = text, Status = status, ObservedAt = DateTimeOffset.UtcNow, Session = session, Ready = readyForAudio, Elements = elements }));
            Console.Out.Flush();
            Thread.Sleep(150);
        }
    }
}

using EchoType.Core;
using EchoType.Windows;
using NAudio.Wave;

internal static class VoiceInputTests
{
    public static void Add(List<(string Name, Func<Task> Test)> tests, string root)
    {
        tests.Add(("Windows AIは専用入力候補と途中モデルで保存復元できる", () =>
        {
            var settings = new VoiceInputSettings { LiveEngine = "windows-ai-speech", Models = ["windows-ai-speech"] };
            settings.Validate();
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-windows-ai")));
            store.SaveVoiceInputSettings(settings);
            var loaded = store.LoadVoiceInputSettings(); loaded.Validate();
            if (loaded.LiveEngine != "windows-ai-speech" || loaded.LocalOnly().Models.Single() != "windows-ai-speech" || VoiceInputSettings.LiveEngines.Single(e => e.Id == loaded.LiveEngine).Name != "Win AI Speech") throw new Exception("Windows AIの専用入力設定が失われました。");
            return Task.CompletedTask;
        }));
        tests.Add(("全文認識と左Ctrl＋左Shiftの初期値・既存選択の保持", () =>
        {
            var settings = new AppSettings(); var voice = new VoiceInputSettings();
            if (settings.PrimaryAsrEngine != "nemotron-3.5" || voice.GetRecordingShortcut() != new VoiceRecordingShortcut(VoiceModifierSide.Left, VoiceModifierSide.Left))
                throw new Exception("初期値が不正");
            var existing = System.Text.Json.JsonSerializer.Deserialize<VoiceInputSettings>("{\"TriggerKeys\":[91,162,160]}")!;
            if (existing.GetRecordingShortcut() != new VoiceRecordingShortcut(VoiceModifierSide.Left, VoiceModifierSide.Left, Windows: VoiceModifierSide.Left))
                throw new Exception("保存済みキーを変更");
            return Task.CompletedTask;
        }));
        tests.Add(("ヒントと同一の実文字・末尾改行を表示位置で区別し不明時は保持する", () =>
        {
            var hint = new System.Windows.Rect(2303, 1256, 110, 26);
            var overlapping = new System.Windows.Rect(2303, 1256, 1, 26);
            var nextLine = new System.Windows.Rect(2303, 1282, 1, 26);
            if (!EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([hint], [overlapping])) throw new Exception("実測した空欄ヒントを識別できない");
            if (EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([hint], [nextLine]) ||
                EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([hint], []) ||
                EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([], [overlapping]) ||
                EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([hint], [System.Windows.Rect.Empty]) ||
                EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([hint], [new(2413, 1256, 1, 26)]) ||
                EchoType.App.VoiceInputTarget.PlaceholderRangesOverlap([hint, nextLine], [overlapping]))
                throw new Exception("実入力または不明な文字範囲をヒントと誤判定");
            return Task.CompletedTask;
        }));
        tests.Add(("起動時準備は音声入力用Whisperを重複せず選び無効時は行わない", () =>
        {
            var settings = new VoiceInputSettings { LiveEngine = "whisper-turbo", Models = ["whisper-base", "windows", "qwen3-0.6b", "whisper-turbo", "kotoba-v2", ""] };
            if (!settings.WhisperWarmupModels().SequenceEqual(new[] { "whisper-base", "whisper-turbo", "kotoba-v2" })) throw new Exception("事前読み込み対象・優先順・重複の判定不正");
            if ((settings with { Enabled = false }).WhisperWarmupModels().Length != 0) throw new Exception("音声入力無効時に読み込み");
            if ((settings with { LiveEngine = "windows", Models = ["windows"] }).WhisperWarmupModels().Length != 0) throw new Exception("未選択のWhisperを読み込み");
            return Task.CompletedTask;
        }));
        tests.Add(("自動採用は先頭の有効モデルだけを実行し比較設定と読込順を保持する", () =>
        {
            var models = new[] { "", "whisper-turbo", "windows", "whisper-base", "whisper-base", "" };
            var manual = new VoiceInputSettings { Models = models, LiveEngine = "whisper-turbo" };
            if (!manual.RecognitionModels().SequenceEqual(new[] { "whisper-turbo", "windows", "whisper-base" }))
                throw new Exception("手動比較の空欄除外・重複除外・指定順が不正");
            var automatic = manual with { AutomaticInput = true };
            if (!automatic.RecognitionModels().SequenceEqual(new[] { "whisper-turbo" }) ||
                !automatic.WhisperWarmupModels().SequenceEqual(new[] { "whisper-turbo" }))
                throw new Exception("自動採用が先頭以外を認識または事前読込み");
            if ((automatic with { Models = ["", "windows", "whisper-base"] }).WhisperWarmupModels().Length != 0 ||
                (automatic with { Models = ["qwen3-0.6b", "whisper-base"] }).WhisperWarmupModels().Length != 0)
                throw new Exception("先頭がWhisperでない自動採用で副Whisperを読込み");
            if ((automatic with { Enabled = false }).WhisperWarmupModels().Length != 0)
                throw new Exception("無効な自動採用で事前読込み");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-single-engine")));
            store.SaveVoiceInputSettings(automatic);
            var restored = store.LoadVoiceInputSettings();
            if (!restored.Models.SequenceEqual(models) || !restored.AutomaticInput ||
                !restored.RecognitionModels().SequenceEqual(new[] { "whisper-turbo" }))
                throw new Exception("自動採用への切替が保存済み比較モデルを消去");
            var resumed = restored with { AutomaticInput = false };
            if (!resumed.RecognitionModels().SequenceEqual(manual.RecognitionModels()) || !resumed.Models.SequenceEqual(models))
                throw new Exception("自動採用オフで比較モデルが復元されない");
            return Task.CompletedTask;
        }));
        tests.Add(("自動整形と自動決定は旧設定でオンを維持し個別保存できる", () =>
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<VoiceInputSettings>("{}", JsonFormat.Options)!;
            if (!old.AutomaticFormatting || !old.AutomaticConfirm || old.AutomaticInput) throw new Exception("旧設定の初期値不正");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-automatic-stages")));
            foreach (var format in new[] { true, false }) foreach (var confirm in new[] { true, false })
            {
                store.SaveVoiceInputSettings(old with { AutomaticFormatting = format, AutomaticConfirm = confirm });
                var saved = store.LoadVoiceInputSettings();
                if (saved.AutomaticFormatting != format || saved.AutomaticConfirm != confirm || saved.AutomaticInput) throw new Exception("段階設定が独立していない");
            }
            return Task.CompletedTask;
        }));
        tests.Add(("音声自動入力は既定オフでオンオフを保存", () =>
        {
            if (new VoiceInputSettings().AutomaticInput) throw new Exception("自動入力が既定でオン");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-automatic")));
            store.SaveVoiceInputSettings(new() { AutomaticInput = true });
            if (!store.LoadVoiceInputSettings().AutomaticInput) throw new Exception("オンが保存されない");
            store.SaveVoiceInputSettings(store.LoadVoiceInputSettings() with { AutomaticInput = false });
            if (store.LoadVoiceInputSettings().AutomaticInput) throw new Exception("オフが保存されない");
            return Task.CompletedTask;
        }));
        tests.Add(("Windows標準だけを既定にし、選択済みモデルと置換モードを保存", () =>
        {
            var defaults = new VoiceInputSettings();
            if (!defaults.RecognitionModels().SequenceEqual(new[] { "windows" }) || defaults.LiveEngine != "windows" || defaults.WhisperWarmupModels().Length != 0)
                throw new Exception("既定モデルはWindows標準のみです。");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-replace-mode")));
            store.SaveVoiceInputSettings(new() { Models = ["windows", "qwen3-ja", "whisper-turbo", "", "", "", "", ""], ReplaceTargetText = true });
            var loaded = store.LoadVoiceInputSettings();
            if (!loaded.ReplaceTargetText || !loaded.Models.SequenceEqual(new[] { "windows", "qwen3-ja", "whisper-turbo", "", "", "", "", "" })) throw new Exception("置換保存・日本語特化モデルの保持不正");
            store.SaveVoiceInputSettings(loaded with { Models = ["whisper-small"], ReplaceTargetText = false });
            if (store.LoadVoiceInputSettings().ReplaceTargetText || store.LoadVoiceInputSettings().Models.Single() != "whisper-small") throw new Exception("個別モデル設定保持不正");
            return Task.CompletedTask;
        }));
        tests.Add(("旧標準整形プロンプトを更新し個別編集は保持", () =>
        {
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-prompt-upgrade")));
            store.SaveVoiceInputSettings(new() { FormattingPrompt = VoiceInputSettings.PreviousFormattingPrompt });
            if (store.LoadVoiceInputSettings().FormattingPrompt != VoiceInputSettings.DefaultFormattingPrompt) throw new Exception("旧標準が更新されない");
            store.SaveVoiceInputSettings(new() { FormattingPrompt = "独自の指示" });
            if (store.LoadVoiceInputSettings().FormattingPrompt != "独自の指示") throw new Exception("独自編集が失われた");
            return Task.CompletedTask;
        }));
        tests.Add(("対象を避ける配置は上下左右・別モニター・縮小・空きなしを判定", () =>
        {
            VoiceWindowArea work = new(0, 0, 1920, 1040);
            foreach (var target in new[] { new VoiceWindowArea(0, 0, 400, 300), new(1520, 0, 400, 300), new(0, 740, 400, 300), new(0, 300, 1920, 400) })
            {
                var p = VoiceWindowLayout.Find([work], target, 1080, 850) ?? throw new Exception("空き領域なし");
                if (p.Overlaps(target) || p.X < work.X || p.Y < work.Y || p.Right > work.Right || p.Bottom > work.Bottom) throw new Exception("重なり/はみ出し");
            }
            var other = VoiceWindowLayout.Find([work, new(-1280, -300, 1280, 1024)], work, 1080, 850);
            if (other is null || other.Value.Right > 0 || other.Value.Y < -300) throw new Exception("別モニター不正");
            if (VoiceWindowLayout.Find([work], work, 1080, 850) is not null) throw new Exception("空きなしを検出しない");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-size")));
            store.SaveVoiceInputSettings(new() { WindowWidth = 990, WindowHeight = 710, TriggerKeys = [0xA5, 0xA3], SentenceEndings = ["だよ"] });
            var loaded = store.LoadVoiceInputSettings();
            if (loaded.WindowWidth != 990 || loaded.WindowHeight != 710 || !loaded.TriggerKeys.SequenceEqual(new[] { 0xA5, 0xA3 }) || loaded.SentenceEndings.Single() != "だよ") throw new Exception("サイズ・キー・ルール保存不正");
            return Task.CompletedTask;
        }));
        tests.Add(("入力欄との間隔を保存し境界モニターでも余白を確保", () =>
        {
            if (new VoiceInputSettings().TargetGap != 32 || System.Text.Json.JsonSerializer.Deserialize<VoiceInputSettings>("{}")!.TargetGap != 32) throw new Exception("間隔の初期値が不正");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-gap")));
            store.SaveVoiceInputSettings(new() { TargetGap = 80 });
            if (store.LoadVoiceInputSettings().TargetGap != 80) throw new Exception("間隔を保存できない");
            foreach (var gap in new[] { 0, 32, 200 })
            {
                VoiceWindowArea target = new(1800, 450, 120, 200);
                var padded = new VoiceWindowArea(target.X - gap, target.Y - gap, target.Width + gap * 2, target.Height + gap * 2);
                var p = VoiceWindowLayout.Find([new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)], target, 1080, 850, gap);
                if (p is null || p.Value.Overlaps(padded)) throw new Exception("モニター境界で余白が重なる");
            }
            foreach (var gap in new[] { -1, 201 })
            {
                var rejected = false; try { (new VoiceInputSettings { TargetGap = gap }).Validate(); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("不正な間隔を許可");
            }
            return Task.CompletedTask;
        }));
        tests.Add(("左右8修飾キーの選択を完全一致で判定し余分なキーと短押しを破棄", () =>
        {
            foreach (var modifier in VoiceInputSettings.ModifierKeys)
            {
                var g = new VoiceModifierGesture(); g.Configure(VoiceRecordingShortcut.FromModifiers([modifier.Key])); var keys = new HashSet<int>();
                g.Update(keys.Contains, 0); keys.Add(modifier.Key);
                if (g.Update(keys.Contains, 1) != VoiceInputGesture.Change.Started) throw new Exception("キー開始不正");
                keys.Clear(); if (g.Update(keys.Contains, 1001) != VoiceInputGesture.Change.Accepted) throw new Exception("キー確定不正");
                keys.Add(modifier.Key); g.Update(keys.Contains, 2000); keys.Add(0x1B);
                if (g.Update(keys.Contains, 3100) != VoiceInputGesture.Change.Discarded) throw new Exception("余分なキーを採用");
            }
            var settings = new VoiceInputSettings { TriggerKeys = [] }; var rejected = false;
            try { settings.Validate(); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("キーなしで保存可能");
            return Task.CompletedTask;
        }));
        tests.Add(("録音の主キー・左右条件の保存と再アームを確認", () =>
        {
            var shortcut = new VoiceRecordingShortcut(VoiceModifierSide.Either, VoiceModifierSide.None, VoiceModifierSide.Both, MainKey: 0x87);
            var gesture = new VoiceModifierGesture(); gesture.Configure(shortcut);
            var keys = new HashSet<int>();
            VoiceInputGesture.Change Update(long ms, params int[] pressed)
            {
                keys.Clear(); keys.UnionWith(pressed);
                return gesture.Update(keys.Contains, ms);
            }
            if (Update(0) != VoiceInputGesture.Change.None || Update(10, 0xA3, 0xA4, 0x87) != VoiceInputGesture.Change.None ||
                Update(20, 0xA3, 0xA4, 0xA5, 0x87) != VoiceInputGesture.Change.Started ||
                Update(1019, 0x87) != VoiceInputGesture.Change.Discarded ||
                Update(1020, 0xA2, 0xA4, 0xA5, 0x87) != VoiceInputGesture.Change.None)
                throw new Exception("左右条件または短押し後の再アーム不正");
            Update(1030);
            if (Update(1100, 0xA2, 0xA3, 0xA4, 0xA5, 0x87) != VoiceInputGesture.Change.Started ||
                Update(2100, 0xA5, 0x87) != VoiceInputGesture.Change.Accepted ||
                Update(2110, 0xA2, 0xA4, 0xA5, 0x87) != VoiceInputGesture.Change.None)
                throw new Exception("1秒境界または確定後の再アーム不正");
            Update(2120);
            if (Update(2200, 0xA2, 0xA4, 0xA5, 0x87) != VoiceInputGesture.Change.Started ||
                Update(3300, 0xA2, 0xA4, 0xA5, 0x87, 'K') != VoiceInputGesture.Change.Discarded)
                throw new Exception("指定外の主キーを採用");

            var mainOnly = new VoiceRecordingShortcut(VoiceModifierSide.None, VoiceModifierSide.None, MainKey: 'Z');
            gesture.Configure(mainOnly);
            Update(3400);
            if (Update(3410, 'Z') != VoiceInputGesture.Change.Started || Update(4410) != VoiceInputGesture.Change.Accepted)
                throw new Exception("主キー単独を設定できない");

            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-recording-shortcut")));
            store.SaveVoiceInputSettings(new() { TriggerKeys = [0xA2, 0xA3], RecordingShortcut = shortcut });
            if (store.LoadVoiceInputSettings().GetRecordingShortcut() != shortcut ||
                VoiceRecordingShortcut.FromModifiers([0xA2, 0xA3]).Control != VoiceModifierSide.Both)
                throw new Exception("新形式の保存または旧左右同時指定の互換性が不正");
            try { (new VoiceInputSettings { RecordingShortcut = mainOnly with { MainKey = 0x1B } }).Validate(); }
            catch (InvalidOperationException) { return Task.CompletedTask; }
            throw new Exception("不正な主キーを設定できた");
        }));
        tests.Add(("候補末尾の句点ルールは編集可能で共有語彙とCLIは本体設定を使う", () =>
        {
            var settings = new VoiceInputSettings();
            foreach (var text in new[] { "そうです", "お願いします", "できますか", "確認してください" }) if (settings.CompleteCandidate(text) != text + "。") throw new Exception("終了語の句点不足");
            foreach (var text in new[] { "そうです。", "できますか？", "途中の", "" }) if (settings.CompleteCandidate(text) != text) throw new Exception("不要な句点");
            var custom = settings with { SentenceEndings = ["だよ"] };
            if (custom.CompleteCandidate("そうだよ") != "そうだよ。" || custom.CompleteCandidate("そうです") != "そうです") throw new Exception("ルール変更不正");
            var shared = settings.WithShared(new() { AsrVocabulary = "本体語彙", CodexExecutable = "本体CLI" });
            if (shared.Recognition.AsrVocabulary != "本体語彙" || shared.Recognition.CodexExecutable != "本体CLI") throw new Exception("本体設定が反映されない");
            return Task.CompletedTask;
        }));
        tests.Add(("追記の句点は未付与時だけ補いエフォート3段階を保存", () =>
        {
            if (VoiceInputSettings.AppendSentence("文 ") != "文。" || VoiceInputSettings.AppendSentence("文。\r\n") != "文。" || VoiceInputSettings.AppendSentence(" ") != "") throw new Exception("句点補完不正");
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-effort")));
            foreach (var effort in VoiceInputSettings.FormattingEfforts)
            {
                store.SaveVoiceInputSettings(new() { FormattingEffort = effort, SimpleFormattingPrompt = "原意を維持" });
                if (store.LoadVoiceInputSettings().FormattingEffort != effort || store.LoadVoiceInputSettings().SimpleFormattingPrompt != "原意を維持") throw new Exception("整形設定の保存不正");
            }
            return Task.CompletedTask;
        }));
        tests.Add(("整形差分は共通行を保ち追加・削除・置換行を色分けする", () =>
        {
            var diff = VoiceTextDiff.Compare("先頭\r\n旧文\r\n共通\r\n末尾", "先頭\n新文\n共通\n追加\n末尾");
            if (!diff.Before.SequenceEqual(new[] { false, true, false, false }) || !diff.After.SequenceEqual(new[] { false, true, false, true, false })) throw new Exception("差分不正");
            var same = VoiceTextDiff.Compare("同じ", "同じ"); if (same.Before.Any(x => x) || same.After.Any(x => x)) throw new Exception("同一文に差分あり");
            var deleted = VoiceTextDiff.Compare("一\n二\n三", "一\n三");
            if (!deleted.Before.SequenceEqual(new[] { false, true, false }) || deleted.After.Any(x => x)) throw new Exception("削除行不正");
            return Task.CompletedTask;
        }));
        tests.Add(("即時表示の認識は1秒ごとに最新の音声を使い中止できる", async () =>
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seconds = new System.Collections.Concurrent.ConcurrentQueue<double>();
            using var live = new VoiceInputAccurateLive(async (segment, ct) =>
            {
                seconds.Enqueue(segment.Seconds); started.TrySetResult(); await first.Task.WaitAsync(ct);
                return new("whisper-turbo", $"認識{segment.Seconds}", .1);
            });
            live.Feed(new byte[24000]); await Task.Delay(30);
            if (!seconds.IsEmpty) throw new Exception("文脈の短い音声を認識しました");
            live.Feed(new byte[24000]); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            live.Feed(new byte[48000 * 6]); first.SetResult();
            var result = await live.CompleteAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            if (result.Text != "認識7" || seconds.Any(s => s is not (1 or 7))) throw new Exception("途中の仮説を連結または古い音声を逐次処理しました");
            using var longLive = new VoiceInputAccurateLive((segment, _) => Task.FromResult(new AsrResult("whisper-turbo", $"区間{segment.Seconds}", .1)));
            longLive.Feed(new byte[48000 * 70]);
            if ((await longLive.CompleteAsync(CancellationToken.None)).Text != "区間30区間30区間10") throw new Exception("長い押下音声の確定区間を落としました");
            using var cancel = new VoiceInputAccurateLive(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new("whisper-turbo", "", 0); });
            cancel.Feed(new byte[48000 * 3]); cancel.Dispose(); await cancel.Stopped.WaitAsync(TimeSpan.FromSeconds(3));
        }));
        tests.Add(("音声入力キャッシュは暗号化往復と閉鎖後の書込み拒否・削除を行う", () =>
        {
            var cache = new VoiceInputCache(new AgentPaths(Path.Combine(root, "voice-cache")));
            var wave = RecordingDevices.ToWave(new byte[48000]); var file = cache.Save(1, wave);
            if (File.ReadAllBytes(file).SequenceEqual(wave) || !cache.Read(file).SequenceEqual(wave)) throw new Exception("暗号化不正");
            cache.Dispose(); if (Directory.Exists(cache.DirectoryPath)) throw new Exception("一時音声が残りました");
            var denied = false; try { cache.Save(2, wave); } catch (ObjectDisposedException) { denied = true; }
            if (!denied) throw new Exception("閉じた画面へ書き込みました");
            return Task.CompletedTask;
        }));
        tests.Add(("ライブ認識への音声供給は録音中だけで待機・停止後は渡さない", async () =>
        {
            var input = new FakeInput(); using var capture = new VoiceInputCapture(input, 2);
            var frames = 0; capture.Frame += _ => frames++; capture.Start(); input.Feed(new byte[2400]);
            await capture.StopAsync(false); input.Feed(new byte[2400]);
            var monitorInput = new FakeInput(); using var monitor = new VoiceInputCapture(monitorInput, 2, true);
            monitor.Frame += _ => frames++; monitor.Start(); monitorInput.Feed(new byte[2400]);
            if (frames != 1) throw new Exception("押下外の音声が認識へ渡りました");
        }));
        tests.Add(("音声入力の現在キー状態は長押し解放・短押し・取消後の再押下を判定", () =>
        {
            var g = new VoiceInputGesture(); g.Reset();
            if (g.Update(true, true, false, 0) != VoiceInputGesture.Change.None) throw new Exception("起動中の押下を採用");
            g.Update(false, false, false, 1);
            if (g.Update(true, true, false, 10) != VoiceInputGesture.Change.Started || g.Update(false, false, false, 1010) != VoiceInputGesture.Change.Accepted) throw new Exception("長押し解放不正");
            g.Update(true, true, false, 2000);
            if (g.Update(false, false, false, 2999) != VoiceInputGesture.Change.Discarded) throw new Exception("短押しを採用");
            g.Update(true, true, false, 3000);
            if (g.Update(true, true, true, 4100) != VoiceInputGesture.Change.Discarded) throw new Exception("他キーを採用");
            if (g.Update(true, true, false, 4200) != VoiceInputGesture.Change.None) throw new Exception("取消後に再開");
            g.Update(false, false, false, 4300);
            if (g.Update(true, true, false, 4400) != VoiceInputGesture.Change.Started) throw new Exception("監視が復帰しない");
            return Task.CompletedTask;
        }));
        tests.Add(("音声入力はローカルモデルだけを許可しCLIパス空欄と専用整形設定を保存", () =>
        {
            var settings = new VoiceInputSettings(); settings.Validate();
            if (settings.Recognition.CodexExecutable != "" || settings.FormattingModel != "gpt-6-luna") throw new Exception("既定値不正");
            var old = settings with { Models = ["gpt-4o-transcribe", "whisper-base+llm"], Recognition = settings.Recognition with { CloudTranscription = true } };
            var denied = false; try { old.Validate(); } catch (InvalidOperationException) { denied = true; }
            if (!denied) throw new Exception("クラウド認識が許可されました");
            old.LocalOnly().Validate();
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-format")));
            store.SaveVoiceInputSettings(settings with { FormattingPrompt = "箇条書きで明瞭な指示にしてください。" });
            if (store.LoadVoiceInputSettings().Recognition.CodexExecutable != "" || store.LoadVoiceInputSettings().FormattingPrompt != "箇条書きで明瞭な指示にしてください。") throw new Exception("専用設定の保存不正");
            return Task.CompletedTask;
        }));
        tests.Add(("待機モニターは波形だけを保持し上限を超えても録音データを作らない", async () =>
        {
            var input = new FakeInput(); using var capture = new VoiceInputCapture(input, 2, monitorOnly: true);
            var limits = 0; capture.LimitReached += () => limits++; capture.Start();
            for (var i = 0; i < 200; i++) input.Feed(Enumerable.Repeat((byte)10, 2400).ToArray());
            if (capture.Meter().Peak <= 0 || limits != 0 || capture.Meter().Waveform.Length > 4800) throw new Exception("モニター不正");
            if (await capture.StopAsync(true) is not null) throw new Exception("待機音声が録音されました");
        }));
        tests.Add(("音声入力は左キーのみ・1秒境界・他キー併用を判定", () =>
        {
            var g = new VoiceInputGesture();
            void Expect(VoiceInputGesture.Change expected, int key, bool down, long time)
            { if (g.Key(key, down, time) != expected) throw new Exception("押下判定が不正です。"); }
            Expect(VoiceInputGesture.Change.None, 0xA2, true, 0);
            Expect(VoiceInputGesture.Change.Started, 0xA0, true, 1);
            Expect(VoiceInputGesture.Change.Discarded, 0xA2, false, 1000);
            g.Key(0xA0, false, 1001);
            g.Key(0xA0, true, 2000);
            Expect(VoiceInputGesture.Change.Started, 0xA2, true, 2000);
            Expect(VoiceInputGesture.Change.Accepted, 0xA0, false, 3000);
            g.Key(0xA2, false, 3001);
            g.Key(0xA3, true, 4000); g.Key(0xA0, true, 4001);
            Expect(VoiceInputGesture.Change.None, 0xA2, true, 4002);
            g.Key(0xA3, false, 4100); g.Key(0xA0, false, 4100); g.Key(0xA2, false, 4100);
            g.Key(0xA0, true, 5000); g.Key(0xA2, true, 5000);
            Expect(VoiceInputGesture.Change.Discarded, 0x53, true, 6500);
            return Task.CompletedTask;
        }));
        tests.Add(("音声入力設定は共通認識設定を上書きしない", () =>
        {
            using var store = new SecureStore(new AgentPaths(Path.Combine(root, "voice-settings")));
            store.SaveSettings(new() { DeviceName = "通常マイク", AsrVocabulary = "通常語彙" });
            var own = new VoiceInputSettings { Models = ["whisper-base", "windows"], Recognition = new() { DeviceName = "専用マイク" } };
            store.SaveVoiceInputSettings(own);
            if (store.LoadSettings().DeviceName != "通常マイク" ||
                store.LoadVoiceInputSettings().Recognition.DeviceName != "専用マイク") throw new Exception("設定が混在しました。");
            return Task.CompletedTask;
        }));
        tests.Add(("音声入力は押下音声を保持し短押しを破棄・上限で通知", async () =>
        {
            var input = new FakeInput(); using var capture = new VoiceInputCapture(input, 2);
            var limits = 0; capture.LimitReached += () => limits++;
            capture.Start(); input.Feed(new byte[48000 * 3]);
            var segment = await capture.StopAsync(true);
            if (segment?.Seconds != 2 || limits != 1) throw new Exception("最大録音長が不正です。");
            var shortInput = new FakeInput(); using var shortCapture = new VoiceInputCapture(shortInput, 2);
            shortCapture.Start(); shortInput.Feed(new byte[2000]);
            if (await shortCapture.StopAsync(false) is not null) throw new Exception("短押し音声が認識に渡りました。");
        }));
        tests.Add(("音声入力の波形計測は録音PCMを変えず停止後のレベルを消す", async () =>
        {
            var input = new FakeInput(); using var capture = new VoiceInputCapture(input, 10);
            var pcm = Enumerable.Range(0, 2400).SelectMany(i => BitConverter.GetBytes((short)(i % 2 == 0 ? 800 : -800))).ToArray();
            var original = pcm.ToArray(); capture.Start();
            for (var i = 0; i < 105; i++) input.Feed(pcm[..2400]);
            var meter = capture.Meter();
            if (meter.Rms <= 0 || meter.Peak <= 0 || meter.Waveform.Length != 4800) throw new Exception("音量・波形履歴上限が不正です。");
            meter.Waveform[0] = 99;
            if (capture.Meter().Waveform[0] == 99) throw new Exception("表示波形の参照が共有されています。");
            var result = await capture.StopAsync(true);
            using var reader = new WaveFileReader(new MemoryStream(result!.Wave));
            var firstFrame = new byte[2400]; reader.ReadExactly(firstFrame);
            if (!firstFrame.SequenceEqual(original[..2400]) || !pcm.SequenceEqual(original)) throw new Exception("表示倍率でPCMが変更されています。");
            if (capture.Meter().Waveform.Length != 0 || capture.Meter().Rms != 0) throw new Exception("停止した録音の波形が残っています。");
        }));
    }
    private sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(24000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Feed(byte[] bytes) => DataAvailable?.Invoke(this, new(bytes, bytes.Length));
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new());
        public void Dispose() { }
    }
}

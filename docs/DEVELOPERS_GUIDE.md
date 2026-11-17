# ビルドと開発検証

PowerShell 7 と .NET 10 SDK を使います。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
pwsh -NoProfile -File dev/scripts/build.ps1
```

出力先は `release/v0.1.0` です。依存を取得済みでネット接続できない場合は `-Offline` を指定します。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
pwsh -NoProfile -File dev/scripts/build.ps1 -Configuration Debug
.\release\Debug\EchoType.exe
```

通常の起動は同じホットキーを使う旧機能の停止後に行います。画面確認には非表示デスクトップを使う検証コマンドを使用できます。右辺の試験出力はテストのビルド出力下に生成されます。

```powershell
dotnet build tests/EchoType.Tests
$test = '.\tests\EchoType.Tests\bin\Debug\net10.0-windows\EchoType.Tests.exe'
& $test
& $test --isolated-app (Resolve-Path 'release/v0.1.0/EchoType.exe').Path
& $test --isolated queue
& $test --isolated focus
# 操作中の作業がないWindowsの対話デスクトップで実行する
& $test --confirm-interactive
```

## 保存先とモデル

設定の「登録済みの接続」の［追加］で接続名・方式・URL・必要なAPIキーを登録します。確定と即時表示では登録した接続を個別に選択し、同じ接続で別々のモデルを指定できます。TranscriptionServerの既定URLは `http://127.0.0.1:8769/v1/audio/transcriptions` です。TranscriptionServerのモデルは取得した一覧で選びます。サーバーの起動準備が完了した後は［モデル一覧を更新］を押してください。

「Windows標準（デバッグ）」は確定と即時表示の接続欄から直接選択できます。OSにある日本語の認識エンジンを使用します。検証用WAVでの認識は `& $test --windows-speech-smoke $wav` で確認できます。

ループバックではHTTP、別のPCへはHTTPSを使います。認識の受付状態とlocal／lan認証の一時トークンは `/api/health` で確認し、`/v1/models` の文字起こし候補を表示します。認識時は選んだモデルIDを変更せずに `/v1/audio/transcriptions` へ送ります。一時トークンは設定に保存しません。

### 合成音声が送信予定欄に入ることを確認する

試験用に1〜10秒の24kHz・16bit・モノラルPCM WAVを用意します。サーバーのモデル一覧から導入済みの文字起こしモデルのIDを選びます。テストは非表示デスクトップと隔離設定を使い、録音キーと合成WAVを通常の録音処理へ渡します。合成WAVの認識結果が送信予定欄に反映された時点で終了します。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
dotnet build tests/EchoType.Tests
$test = '.\tests\EchoType.Tests\bin\Debug\net10.0-windows\EchoType.Tests.exe'
$endpoint = 'http://127.0.0.1:8769/v1/audio/transcriptions'
$model = '<モデル一覧から選んだID>'
$wav = (Resolve-Path '<試験用の合成WAV>').Path
& $test --transcription-server-smoke $endpoint $model $wav
```

製品データは `%LOCALAPPDATA%\EchoType` に保存し、SQLite の本文・設定・辞書と一時音声は Windows の同一ユーザー用 DPAPI で保護します。`--data-root` は別の EchoType 環境を指定します。移行時の `--source-root` は旧版のデータルートを指定します。`--smoke-ui` は試験専用データルートを要求し、マイクとグローバルキー監視を停止した状態で画面を検証して終了します。

入力先監視時は Win32 のフォーカス情報を確認し、UIA による本文の取得や前面化は明示操作時に行います。対話デスクトップ試験では検証用の別プロセスに作った編集欄をターゲットに指定し、決定による実書き込みと別窓への誤入力防止を確認します。

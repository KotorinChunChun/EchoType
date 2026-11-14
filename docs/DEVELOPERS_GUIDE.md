# ビルドと開発検証

PowerShell 7、.NET 10 SDK、Visual Studio の C++ x64 ビルドツールと Windows SDK を使います。字幕 DLL 補助はビルド機に対象となる Windows ライブキャプションのコア DLL と NuGet の Speech SDK ヘッダーを必要とします。対象の DLL 版は実行時のハッシュ検査で確認します。

```powershell
cd C:\develop\EchoType
pwsh -NoProfile -File dev/scripts/build.ps1
```

出力先は `release/v0.1.0` です。Windows AI 音声、ライブキャプション UIA、字幕 DLL の補助実行ファイルも同梱します。依存を取得済みでネット接続できない場合は `-Offline` を指定します。

```powershell
cd C:\develop\EchoType
pwsh -NoProfile -File dev/scripts/build.ps1 -Configuration Debug
.\release\Debug\EchoType.App.exe --open
```

通常の起動は同じホットキーを使う旧機能の停止後に行います。画面確認には非表示デスクトップを使う検証コマンドを使用できます。右辺の試験出力はテストのビルド出力下に生成されます。

```powershell
dotnet build tests/EchoType.Tests
$test = '.\tests\EchoType.Tests\bin\Debug\net10.0-windows\EchoType.Tests.exe'
& $test
& $test --isolated-app (Resolve-Path 'release/v0.1.0/EchoType.App.exe').Path
& $test --isolated queue
& $test --isolated focus
```

## 保存先とモデル

製品データは `%LOCALAPPDATA%\EchoType` に保存し、SQLite の本文・設定・辞書と一時音声は Windows の同一ユーザー用 DPAPI で保護します。`--data-root` は別の EchoType 環境を指定します。移行時の `--source-root` は旧版のデータルートを指定します。`--smoke-ui` は試験専用データルートを要求し、マイクとグローバルキー監視を停止した状態で画面を検証して終了します。

Python 系のローカルモデルと対応する環境は `--model-root` で指定したフォルダーから読みます。旧 DesktopAgent の `model` または同じ階層構成の共有モデルを指定できます。整形の一時ファイルと診断は EchoType 側に保存します。EchoType 専用に導入する場合は `pwsh -NoProfile -File dev/scripts/setup-asr.ps1 -Models whisper-base,whisper-turbo` を使います。Haas は既存の共通フォルダーを参照し、導入が必要なら `dev/scripts/setup-haas.ps1` を使います。

ライブキャプション UIA は Windows 全体の字幕を観測します。DesktopAgent と同一のユーザー別排他制御を使います。音源一致を保証できないため自動採用の先頭モデルには使えません。入力先監視時は Win32 のフォーカス情報だけを確認し、UIA による本文の取得や前面化は明示操作時に限定します。

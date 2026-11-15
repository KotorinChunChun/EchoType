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

製品データは `%LOCALAPPDATA%\EchoType` に保存し、SQLite の本文・設定・辞書と一時音声は Windows の同一ユーザー用 DPAPI で保護します。`--data-root` は別の EchoType 環境を指定します。移行時の `--source-root` は旧版のデータルートを指定します。`--smoke-ui` は試験専用データルートを要求し、マイクとグローバルキー監視を停止した状態で画面を検証して終了します。

入力先監視時は Win32 のフォーカス情報を確認し、UIA による本文の取得や前面化は明示操作時に行います。対話デスクトップ試験では検証用の別プロセスに作った編集欄をターゲットに指定し、決定による実書き込みと別窓への誤入力防止を確認します。

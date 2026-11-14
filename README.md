# EchoType

Windows のアプリに音声で入力します。通知領域に常駐し、修飾キーの長押しで録音します。途中表示と確定後の複数モデル比較、候補の編集、整形、入力先への追記・置換、個人辞書、アプリ別設定を備えています。

DesktopAgent の専用音声入力を分離した v0.1.0 です。初期キーは左 Windows＋左 Ctrl＋左 Shift。保存済み設定を移行した場合はそのキーを使います。

## 起動

DesktopAgent の専用音声入力を無効にしてから通常起動してください。ビルド済みの実行先は `release/v0.1.0/EchoType.App.exe` です。起動後は通知領域からウィンドウ、設定、終了を選べます。初回に画面を表示する場合は `--open` を指定します。

```powershell
cd C:\develop\EchoType
$oldModels = Join-Path $env:LOCALAPPDATA 'DesktopAgent\model'
.\release\v0.1.0\EchoType.App.exe --model-root $oldModels
```

`--model-root` は既存モデルと対応する Python 環境を使う場合に指定します。省略時は EchoType の専用モデルフォルダーを使います。Haas は `C:/AI/Models/Speech/Haas` を参照します。

## 旧設定の移行

EchoType の通常起動前に、旧版と同じ Windows ユーザーで一度実行します。移行するのは専用音声入力設定、アプリ別設定、個人辞書、推論設定、共通語彙、Codex CLI のパスです。移行後は元の設定と独立して保存されます。

```powershell
cd C:\develop\EchoType
& .\release\v0.1.0\EchoType.App.exe --migrate-desktop-agent | Out-Host
if ($LASTEXITCODE -ne 0) { throw "移行に失敗しました。終了コード $LASTEXITCODE" }
```

移行は旧データベースを読み取り専用で開き、同じ ID の既存 EchoType 設定を保持します。移行中は常駐・録音・ホットキーを起動しません。旧版の未送信履歴はプロセス終了で消えるため、必要な下書きは旧版の「未送信」から呼び戻し、切替前に手元へ控えてください。EchoType でも未送信履歴は実行中のメモリに保持します。

認識音声はローカルで処理します。Codex CLI による整形を選ぶと、編集本文と整形指示・関連する辞書がその接続へ送られます。

[ビルドと開発検証](docs/DEVELOPERS_GUIDE.md) · [開発記録](dev/README.md)

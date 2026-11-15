# EchoType

Windows のアプリに音声で入力します。通知領域に常駐し、修飾キーの長押しで録音します。途中表示と確定後の複数モデル比較、候補の編集、整形、入力先への追記・置換、個人辞書、アプリ別設定を備えています。

DesktopAgent の専用音声入力を分離した v0.1.0 です。初期キーは左 Ctrl＋左 Shift です。保存済みのキー設定は引き続き使えます。設定で Ctrl、Shift、Alt、Windows の左右の条件と、英字または F1〜F24 の主キーを選べます。キーを1秒以上押して離すと録音を確定します。指定したキーは入力先のアプリにも届きます。

## 起動

DesktopAgent の専用音声入力を無効にしてから通常起動してください。開発版のビルド先は `release/v0.1.0/EchoType.exe` です。手動起動では音声入力画面を開き、`--startup` では通知領域に常駐します。通知領域から音声入力、設定、音声認識モデル、一時停止／再開、終了を選べます。一時停止すると録音と処理が中止され、未送信の下書きは実行中の履歴に残ります。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
$oldModels = Join-Path $env:LOCALAPPDATA 'DesktopAgent\model'
.\release\v0.1.0\EchoType.exe --model-root $oldModels
```

手動で最初に画面を開いたとき、選択中の Whisper モデルに不足があれば準備を案内します。初期設定では Whisper base と turbo を使います。「準備する」で不足する重みと EchoType 専用の実行環境を用意できます。「あとで」を選んだ後は通知領域の「音声認識モデル」から開き直せます。取得にはインターネット接続が必要です。

検証済みの Whisper の重みは `C:/AI/Models/Speech/Whisper/Shared` で共用できます。保存できない場合は `%LOCALAPPDATA%/KotorinToys/Models/Speech/Whisper` を使います。`--model-root` は既存の重みと対応する Python 環境を指定するためのオプションです。Haas は `C:/AI/Models/Speech/Haas` を参照します。

## 旧設定の移行

EchoType の通常起動前に、旧版と同じ Windows ユーザーで一度実行します。移行するのは専用音声入力設定、アプリ別設定、個人辞書、推論設定、共通語彙、Codex CLI のパスです。移行後は元の設定と独立して保存されます。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
& .\release\v0.1.0\EchoType.exe --migrate-desktop-agent | Out-Host
if ($LASTEXITCODE -ne 0) { throw "移行に失敗しました。終了コード $LASTEXITCODE" }
```

移行は旧データベースを読み取り専用で開き、同じ ID の既存 EchoType 設定を保持します。移行中は常駐・録音・ホットキーを起動しません。旧版の未送信履歴はプロセス終了で消えるため、必要な下書きは旧版の「未送信」から呼び戻し、切替前に手元へ控えてください。EchoType でも未送信履歴は実行中のメモリに保持します。

認識音声はローカルで処理します。Codex CLI による整形を選ぶと、編集本文と整形指示・関連する辞書がその接続へ送られます。

[ビルドと開発検証](docs/DEVELOPERS_GUIDE.md) · [開発記録](dev/README.md)

## 使用言語とライブラリ

- 使用言語はC#、XAML、Python（ローカル音声認識）です。
- .NET 10、WPF、Windows Forms。録音にはNAudio、設定の保存にはMicrosoft.Data.Sqlite、モデルの実行にはONNX Runtimeを使います。Pythonで動く認識方式にはfaster-whisperやsherpa-onnxがあります。
- 依存定義と資料 [Windows側の依存定義](src/EchoType.Windows/EchoType.Windows.csproj)・[音声認識の依存定義](src/EchoType.Windows/Asr/requirements.txt)・[高速認識の依存定義](src/EchoType.Windows/Asr/requirements-fast.txt)

## ライセンス

本アプリの独自コードと文書には[MITライセンス](LICENSE)が適用されます。第三者のコンポーネントや素材には、それぞれのライセンスが適用されます。

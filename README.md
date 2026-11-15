# EchoType

Windows のアプリに音声で入力します。通知領域に常駐し、修飾キーの長押しで録音します。任意の即時表示と確定文字の編集、整形、入力先への追記・置換、個人辞書、アプリ別設定を備えています。

DesktopAgent の専用音声入力を分離した v0.1.0 です。初期キーは左 Ctrl＋左 Shift です。保存済みのキー設定は引き続き使えます。設定で Ctrl、Shift、Alt、Windows の左右の条件と、英字または F1〜F24 の主キーを選べます。キーを1秒以上押して離すと録音を確定します。指定したキーは入力先のアプリにも届きます。

## 起動

DesktopAgent の専用音声入力を無効にしてから通常起動してください。開発版のビルド先は `release/v0.1.0/EchoType.exe` です。手動起動では音声入力画面を開き、`--startup` では通知領域に常駐します。画面を閉じるか最小化すると通知領域へ格納されます。通知領域から音声入力、設定、一時停止／再開、終了を選べます。一時停止すると録音と処理が中止され、未送信の下書きは実行中の履歴に残ります。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
.\release\v0.1.0\EchoType.exe
```

配布版の起動には [.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0) が必要です。未導入の場合はセットアップで案内します。

## 音声認識を設定する

通知領域の「設定」で確定モデルの接続方式、文字起こしAPIのURL、モデル名を指定します。同じPCで speech.cpp を起動した場合のURLの例は `http://127.0.0.1:8080/v1/audio/transcriptions` です。外部のOpenAI互換APIを使う場合はAPIキーの有無に合わせて接続方式を選びます。「接続テスト」で設定を確認できます。

キーを離すと指定先で文字起こしが始まり、結果が編集欄に入ります。入力欄へターゲットをドラッグして指定し、「決定」を押すと入力します。接続・認識・入力に失敗したときは画面に理由が表示されます。

「即時表示」をオンにすると、録音デバイス欄に途中の文字と即時表示モデル名が出ます。即時表示と確定には別々の接続先とモデルを設定できます。speech.cppで2つの異なる認識モデルを使う場合は、それぞれのサーバーを別ポートで起動します。

## 旧設定の移行

EchoType の通常起動前に、旧版と同じ Windows ユーザーで一度実行します。移行するのは専用音声入力設定、アプリ別設定、個人辞書、推論設定、共通語彙、Codex CLI のパスです。移行後は元の設定と独立して保存されます。

```powershell
cd C:\develop\KotorinToys\tools\EchoType
& .\release\v0.1.0\EchoType.exe --migrate-desktop-agent | Out-Host
if ($LASTEXITCODE -ne 0) { throw "移行に失敗しました。終了コード $LASTEXITCODE" }
```

移行は旧データベースを読み取り専用で開き、同じ ID の既存 EchoType 設定を保持します。移行中は常駐・録音・ホットキーを起動しません。旧版の未送信履歴はプロセス終了で消えるため、必要な下書きは旧版の「未送信」から呼び戻し、切替前に手元へ控えてください。EchoType でも未送信履歴は実行中のメモリに保持します。

録音音声は設定した音声認識先へ送ります。Codex CLI による整形を選ぶと、編集本文と整形指示・関連する辞書がその接続へ送られます。

[ビルドと開発検証](docs/DEVELOPERS_GUIDE.md) · [開発記録](dev/README.md)

## 使用言語とライブラリ

- 使用言語はC#とXAMLです。
- .NET 10、WPF、Windows Formsを使用します。録音にはNAudio、設定の保存にはMicrosoft.Data.Sqliteを使います。 [依存定義](src/EchoType.Windows/EchoType.Windows.csproj)

## ライセンス

本アプリの独自コードと文書には[MITライセンス](LICENSE)が適用されます。第三者のコンポーネントや素材には、それぞれのライセンスが適用されます。

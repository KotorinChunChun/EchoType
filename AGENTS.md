# EchoType 開発ルール

開発・配布の共通方針は C:/develop/AGENTS.md に従う。PowerShell は pwsh を使用し、文書とコミットメッセージは日本語で記録する。

- 目的別の入口は README.md、docs/DEVELOPERS_GUIDE.md、dev/README.md、dev/RELEASE_RULE.md。現作業に必要な資料を参照する。
- 複数フェーズの実装は dev の imp に進捗を記録し、各フェーズのビルドと必要な試験を通してからコミットする。完了した版の資料は dev/history/vX.Y.Z/ に移す。
- dev/private と dev/temp、実際の設定・辞書・音声、成果物は Git から除外する。試験は合成のデータと分離したプロファイルを使う。
- 通常の入力先監視は Win32 のフォーカス情報に限定する。UIA での本文・選択範囲の取得と対象の前面化は明示操作に限定する。前景・セッションが変化した場合は遅着の結果を破棄する。フォーカス診断に本文や要素名を記録しない。
- 音声認識は利用者が設定した speech.cpp サーバーまたは OpenAI 互換 API を使用する。Codex CLI を使う文章整形は別の経路として扱う。
- 新規アプリ情報は C:/develop/default/dev/アプリ一覧.md と DevLauncher の同梱一覧原本に記録する。個人設定は変更しない。

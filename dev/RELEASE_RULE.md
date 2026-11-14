# 配布手順

PowerShell 7 で `pwsh -NoProfile -File dev/scripts/build.ps1` を実行します。`release/v0.1.0` のフォルダー全体がローカル配布物です。必要な版番号は `Directory.Build.props` で管理します。配布物、モデル、実データは Git の対象外です。

[開発者ガイド](../docs/DEVELOPERS_GUIDE.md) の契約試験と分離デスクトップでの起動試験を確認してから引き渡します。署名・インストール・外部への成果物公開はそれぞれ別の工程です。

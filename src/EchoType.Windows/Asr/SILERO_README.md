# 同梱する発話検出モデル

Silero VAD v6（MIT）、faster-whisper v1.2.1が公開するONNX版を使用。

- 取得元: https://github.com/SYSTRAN/faster-whisper/blob/v1.2.1/faster_whisper/assets/silero_vad_v6.onnx
- 元の開発元: https://github.com/snakers4/silero-vad
- SHA-256: `4CBF549B8326F60F80F2536D9EEFEB450A9ABE83365A098031C89719F1BE17D2`
- 入力は16kHz、512サンプルと直前64サンプルの文脈、初期h/cを発話候補ごとにゼロへ戻す。
- マイク・再生・ローカル認識・クラウド認識で同じ判定を使用。認識へ渡す元のWAVは編集しない。
- Pythonや文字起こしモデルの追加導入なしで動作する。
- ライセンス原文は [Silero-VAD-MIT.txt](Silero-VAD-MIT.txt) を参照してください。

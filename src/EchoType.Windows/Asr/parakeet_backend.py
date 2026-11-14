"""Parakeet JAのCTC int8版をローカルPCMから認識する。音声は保存しない。"""
from pathlib import Path

import numpy as np


SAMPLE_RATE = 16000
MODEL_FILES = {"model": "model.int8.onnx", "tokens": "tokens.txt"}


class ParakeetRecognizer:
    def __init__(self, directory: Path, device: str, threads: int):
        if device == "cuda":
            raise RuntimeError("Parakeet JA CTC int8のCUDA実行は未対応です。CPUを指定してください。")
        if device != "cpu":
            raise ValueError("Parakeet JA CTC int8のデバイスはcpuを指定してください。")
        if isinstance(threads, bool) or not isinstance(threads, int) or threads < 1:
            raise ValueError("Parakeetのスレッド数は1以上の整数を指定してください。")
        import sherpa_onnx

        directory = Path(directory)
        self._recognizer = sherpa_onnx.OfflineRecognizer.from_nemo_ctc(
            **{key: str(directory / name) for key, name in MODEL_FILES.items()},
            num_threads=threads,
            sample_rate=SAMPLE_RATE,
            feature_dim=80,
            decoding_method="greedy_search",
            debug=False,
            provider="cpu",
        )

    def transcribe(self, audio: np.ndarray, sample_rate: int = SAMPLE_RATE) -> str:
        recognizer = self._recognizer
        if recognizer is None:
            raise RuntimeError("Parakeetの認識器は既に閉じられています。")
        if sample_rate != SAMPLE_RATE:
            raise ValueError("Parakeetには16kHzへ変換済みの音声を渡してください。")
        waveform = np.asarray(audio)
        if waveform.ndim != 1 or not np.issubdtype(waveform.dtype, np.floating):
            raise ValueError("Parakeetには有限値のモノラル浮動小数点PCMを渡してください。")
        with np.errstate(over="ignore", invalid="ignore"):
            waveform = np.ascontiguousarray(waveform, dtype=np.float32)
        if not np.all(np.isfinite(waveform)):
            raise ValueError("Parakeetには有限値のモノラル浮動小数点PCMを渡してください。")
        if waveform.size == 0:
            return ""
        # 公式のoffline NeMo CTC APIと同じく、録音全体を1つのstreamで認識する。
        stream = recognizer.create_stream()
        stream.accept_waveform(SAMPLE_RATE, waveform)
        recognizer.decode_stream(stream)
        text = stream.result.text
        if not isinstance(text, str):
            raise RuntimeError("Parakeetの認識結果が文字列ではありません。")
        return text

    def close(self):
        # Python参照の解放でnative recognizerを解放する。複数回呼び出し可能。
        self._recognizer = None

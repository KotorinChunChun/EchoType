"""ReazonSpeech k2-v2をローカルの16kHz PCMから認識する。音声は保存しない。"""
from pathlib import Path

import numpy as np


SAMPLE_RATE = 16000
MAX_CHUNK_SAMPLES = 25 * SAMPLE_RATE
PAD_SAMPLES = int(0.9 * SAMPLE_RATE)
_SEARCH_SAMPLES = 3 * SAMPLE_RATE
_FRAME_SAMPLES = int(0.02 * SAMPLE_RATE)
_SILENCE_FRAMES = 10  # 200ms以上の静かな区間を境界候補にする。
_SILENCE_POWER = 0.01 ** 2  # 正規化PCMでRMSが-40dBFS以下。
MODEL_FILES = {
    "encoder": "encoder-epoch-99-avg-1.int8.onnx",
    "decoder": "decoder-epoch-99-avg-1.onnx",
    "joiner": "joiner-epoch-99-avg-1.int8.onnx",
    "tokens": "tokens.txt",
}


def _quiet_boundary(audio: np.ndarray, limit: int) -> int:
    """上限直前3秒の最後の無音区間を選ぶ。PCMの除去や重複は行わない。"""
    begin = limit - _SEARCH_SAMPLES
    frames = audio[begin:limit].reshape(-1, _FRAME_SAMPLES)
    powers = np.mean(np.square(frames, dtype=np.float64), axis=1)
    quiet = powers <= _SILENCE_POWER
    run_start = None
    boundary = limit
    for index, is_quiet in enumerate(quiet):
        if is_quiet and run_start is None:
            run_start = index
        if run_start is not None and (not is_quiet or index == len(quiet) - 1):
            run_end = index + 1 if is_quiet else index
            if run_end - run_start >= _SILENCE_FRAMES:
                # 上限まで静かな場合は、そのまま上限で区切れる。
                boundary = limit if run_end == len(quiet) else (
                    begin + (run_start + run_end) * _FRAME_SAMPLES // 2)
            run_start = None
    return boundary


def split_audio(audio: np.ndarray):
    """全サンプルを一度だけ覆う、最大25秒の半開区間を順番に返す。"""
    start = 0
    while start < len(audio):
        end = min(start + MAX_CHUNK_SAMPLES, len(audio))
        if end < len(audio):
            end = _quiet_boundary(audio, end)
        yield start, end
        start = end


def _provider(device: str) -> str:
    if device == "cpu":
        return "cpu"
    if device != "cuda":
        raise ValueError("ReazonSpeechのデバイスはcpuまたはcudaを指定してください。")
    # このint8-fp32経路はCPU用。CUDA要求をCPUへ黙って切り替えない。
    raise RuntimeError("ReazonSpeech k2-v2のCUDA実行は未対応です。CPUを指定してください。")


class ReazonRecognizer:
    def __init__(self, directory: Path, device: str, threads: int):
        if isinstance(threads, bool) or not isinstance(threads, int) or threads < 1:
            raise ValueError("ReazonSpeechのスレッド数は1以上の整数を指定してください。")
        provider = _provider(device)
        import sherpa_onnx

        directory = Path(directory)
        self._recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
            **{key: str(directory / name) for key, name in MODEL_FILES.items()},
            num_threads=threads,
            sample_rate=SAMPLE_RATE,
            feature_dim=80,
            decoding_method="greedy_search",
            provider=provider,
        )

    def transcribe(self, audio: np.ndarray, sample_rate: int = SAMPLE_RATE) -> str:
        recognizer = self._recognizer
        if recognizer is None:
            raise RuntimeError("ReazonSpeechの認識器は既に閉じられています。")
        if sample_rate != SAMPLE_RATE:
            raise ValueError("ReazonSpeechには16kHzへ変換済みの音声を渡してください。")
        waveform = np.asarray(audio, dtype=np.float32)
        if waveform.ndim != 1 or not np.all(np.isfinite(waveform)):
            raise ValueError("ReazonSpeechには有限値のモノラルPCMを渡してください。")
        texts = []
        for start, end in split_audio(waveform):
            # 公式pkg/k2-asrと同じ前後各0.9秒。短い末尾区間も省略しない。
            padded = np.pad(waveform[start:end], (PAD_SAMPLES, PAD_SAMPLES))
            stream = recognizer.create_stream()
            stream.accept_waveform(SAMPLE_RATE, padded)
            recognizer.decode_stream(stream)
            text = stream.result.text
            if not isinstance(text, str):
                raise RuntimeError("ReazonSpeechの認識結果が文字列ではありません。")
            texts.append(text)
        # 意図した繰り返し・空白・句読点を維持する。
        return "".join(texts)

    def close(self):
        # Python参照の解放でnative recognizerを解放する。複数回呼び出し可能。
        self._recognizer = None

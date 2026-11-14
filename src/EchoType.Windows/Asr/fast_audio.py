"""音声をメモリ上で日本語ASR共通の16kHz・mono float32に揃える。"""
import io
import math
import numpy as np
import soundfile as sf


def prepare_decoder(sample_rate=24000):
    """モデルの事前読込みで、本体の24kHz変換依存も準備する。音声・推論は不要。"""
    if sample_rate != 16000:
        from scipy.signal import resample_poly


def decode_wave(wave):
    audio, rate = sf.read(io.BytesIO(wave), dtype="float32", always_2d=True)
    if not 8000 <= rate <= 192000 or audio.shape[1] not in (1, 2) or not np.isfinite(audio).all():
        raise ValueError("入力音声形式が不正です")
    mono = audio.mean(axis=1, dtype=np.float32)
    if rate != 16000:
        # 16kHzの外部試験入力ではscipyを不要にする。本体の24kHz入力は
        # 従来と同じpolyphase変換を維持し、事前読込みで依存も準備できる。
        from scipy.signal import resample_poly
        divisor = math.gcd(rate, 16000)
        mono = resample_poly(mono, 16000 // divisor, rate // divisor)
    return np.ascontiguousarray(mono, dtype=np.float32)

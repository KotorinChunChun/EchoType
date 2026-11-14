"""GTCRN: ローカル・標準入出力のみ。原音をファイルへ書き出さない。"""
import base64, io, json, sys, wave
import numpy as np
import sherpa_onnx

config = sherpa_onnx.OfflineSpeechDenoiserConfig(model=sherpa_onnx.OfflineSpeechDenoiserModelConfig(
    gtcrn=sherpa_onnx.OfflineSpeechDenoiserGtcrnModelConfig(model=sys.argv[1]), num_threads=1, provider="cpu"))
if not config.validate():
    raise RuntimeError("GTCRNの設定が不正です")
denoiser = sherpa_onnx.OfflineSpeechDenoiser(config)
for line in sys.stdin:
    try:
        request = json.loads(line)
        with wave.open(io.BytesIO(base64.b64decode(request["wave"])), "rb") as src:
            if src.getnchannels() != 1 or src.getsampwidth() != 2:
                raise ValueError("16bitモノラルPCMが必要です")
            rate = src.getframerate()
            samples = np.frombuffer(src.readframes(src.getnframes()), dtype="<i2").astype(np.float32) / 32768
        result = denoiser(samples, rate)
        # 既存ASRの入力形式・時間軸を維持し、モデルの端数を原音の長さに合わせる。
        enhanced = np.asarray(result.samples)
        restored = np.interp(np.arange(len(samples)) * result.sample_rate / rate, np.arange(len(enhanced)), enhanced)
        pcm = (np.clip(restored, -1, 32767/32768) * 32768).astype("<i2").tobytes()
        dst = io.BytesIO()
        with wave.open(dst, "wb") as out:
            out.setnchannels(1); out.setsampwidth(2); out.setframerate(rate); out.writeframes(pcm)
        print(json.dumps({"wave":base64.b64encode(dst.getvalue()).decode(),"error":""}), flush=True)
    except Exception as exc:
        print(json.dumps({"wave":"","error":str(exc)}), flush=True)

"""固定したローカルASRのみ。音声はstdin/stdout内で扱い、ディスクへ書かない。"""
import base64
import io
import json
import os
from pathlib import Path
import sys
import time
import hashlib
import urllib.request
import shutil
import contextlib
import re
import threading
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED
from fast_models import MODELS as FAST_MODELS, has_weights as has_fast_weights, runtime_directory

if os.name == "nt":
    # パッケージのVC++ DLLを、別フォルダーのPythonで動くネイティブ拡張からも参照する。
    _app_local_dlls = os.add_dll_directory(str(Path(__file__).resolve().parent.parent))

WHISPER_MODELS = json.loads(Path(__file__).with_name("whisper-models.json").read_text(encoding="utf-8"))
MODELS = {
    **WHISPER_MODELS,
    "qwen3-ja": ("neosophie/Qwen3-ASR-1.7B-JA", "987bda160f2dabfa6757550bcff7cdda2ba0648c"),
    "qwen3-0.6b": ("Qwen/Qwen3-ASR-0.6B", "5eb144179a02acc5e5ba31e748d22b0cf3e303b0"),
    "qwen3-1.7b": ("Qwen/Qwen3-ASR-1.7B", "7278e1e70fe206f11671096ffdd38061171dd6e5"),
}
QWEN_MODELS = frozenset(("qwen3-ja", "qwen3-0.6b", "qwen3-1.7b"))


def safe_failure(error, stage):
    """例外原文・入力・パスを返さず、固定の原因コードとCUDA DLL名だけ返す。"""
    message = str(error).lower()
    libraries = sorted(set(re.findall(r"\b(?:cublas|cudnn|cudart)[a-z0-9_]*\.dll\b", message)))[:8]
    if libraries and any(word in message for word in ("not found", "cannot", "could not", "failed", "unable")):
        code, hint = "cuda-library-unavailable", "CUDA実行DLLを読み込めません（" + ", ".join(libraries) + "）。GPU実行環境を確認してください。"
    elif "out of memory" in message:
        code, hint = "memory-exhausted", "推論用メモリが不足しています。常駐モデル数を減らしてください。"
    elif "compute type" in message and any(word in message for word in ("support", "invalid")):
        code, hint = "compute-unsupported", "選択した計算精度に対応していません。CUDA計算精度を確認してください。"
    elif "cuda" in message or "cudnn" in message or "cublas" in message:
        code, hint = "cuda-error", "GPU処理に失敗しました。GPUドライバーとCUDA実行環境を確認してください。"
    else:
        code, hint = "engine-error", "モデル・実行環境を確認してください。"
    return {"error": type(error).__name__ + ": " + hint,
            "diagnostic": {"code": code, "stage": stage, "libraries": libraries}}


def has_model_weights(directory, engine):
    """単一重み、またはindexの全分割重みが同じモデルディレクトリに揃っているか確認する。"""
    if engine in FAST_MODELS:
        return has_fast_weights(directory, engine)
    if engine not in MODELS:
        return False
    directory = Path(directory)
    try:
        root = directory.resolve(strict=True)

        def nonempty(name):
            path = directory / name
            # MSIXの仮想化では同一ディレクトリでもresolve後の表記が異なるため、実体で比較する。
            return path.is_file() and root.samefile(path.resolve(strict=True).parent) and path.stat().st_size > 0

        if engine not in QWEN_MODELS:
            return nonempty("model.bin")
        index = directory / "model.safetensors.index.json"
        if not index.exists():
            return nonempty("model.safetensors")
        if not nonempty(index.name):
            return False
        document = json.loads(index.read_text(encoding="utf-8"))
        weights = document.get("weight_map") if isinstance(document, dict) else None
        if not isinstance(weights, dict) or not weights:
            return False
        # 絶対パス、区切り文字、Windowsの代替ストリーム等を名前の段階で除外する。
        return all(isinstance(tensor, str) and tensor and isinstance(name, str) and
            re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*\.safetensors", name) is not None and nonempty(name)
            for tensor, name in weights.items())
    except (OSError, ValueError, RuntimeError):
        return False


def main():
    # 標準ストリームはWindowsの既定文字コードに依存させない。
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    if len(sys.argv) >= 3 and sys.argv[1] == "--download":
        from huggingface_hub import HfApi
        selected = sys.argv[3:] or list(MODELS)
        if any(engine not in MODELS for engine in selected):
            raise ValueError("未対応のモデルです")
        for engine, (repo, revision) in MODELS.items():
            if engine not in selected:
                continue
            print("モデル取得: " + engine, flush=True)
            directory = Path(sys.argv[2]) / engine
            directory.mkdir(parents=True, exist_ok=True)
            info = HfApi().model_info(repo, revision=revision, files_metadata=True)
            for item in info.siblings:
                name = item.rfilename
                if any(character in name for character in ("/", "\\", ":")) or not (name.endswith((".json", ".safetensors", ".jinja")) or name in ("model.bin", "merges.txt") or name.startswith(("vocabulary.", "LICENSE")) or name == "README.md"):
                    continue
                target = directory / name
                checksum = item.lfs.sha256 if item.lfs else None
                if target.exists() and target.stat().st_size == item.size:
                    with target.open("rb") as source:
                        valid = not checksum or hashlib.file_digest(source, "sha256").hexdigest() == checksum
                    if valid:
                        continue
                staging = target.with_suffix(target.suffix + ".partial")
                with urllib.request.urlopen(f"https://huggingface.co/{repo}/resolve/{revision}/{name}", timeout=120) as response, staging.open("wb") as output:
                    shutil.copyfileobj(response, output, 1024 * 1024)
                if staging.stat().st_size != item.size:
                    raise ValueError("モデルの長さが一致しません")
                if checksum:
                    with staging.open("rb") as source:
                        if hashlib.file_digest(source, "sha256").hexdigest() != checksum:
                            raise ValueError("モデルのハッシュが一致しません")
                staging.replace(target)
            if not has_model_weights(directory, engine):
                raise ValueError("モデルの重みが揃っていません")
            (directory / "ready.json").write_text(json.dumps({"repo": repo, "revision": revision}), encoding="utf-8")
        return
    if len(sys.argv) != 3 or sys.argv[1] != "--serve":
        raise ValueError("起動形式が不正です")
    root = Path(sys.argv[2])
    cache = {}
    try:
        # JSON専用fdを保存し、Pythonとネイティブのstdoutをプロセス全体でstderrへ分離する。
        try:
            protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
        except (AttributeError, io.UnsupportedOperation):
            protocol = sys.stdout  # メモリ内契約試験
        else:
            os.dup2(2, 1)
            from nemotron_backend import isolate_native_output
            isolate_native_output()
        with contextlib.redirect_stdout(sys.stderr):
            _serve(root, cache, protocol)
        if protocol is not sys.stdout and hasattr(protocol, "fileno"):
            try:
                protocol.close()
            except io.UnsupportedOperation:
                pass
    finally:
        for model in cache.values():
            if hasattr(model, "close"):
                model.close()
        cache.clear()


def _handle(root, cache, request, cache_lock, progress=None):
    stage = "request"
    try:
        operation = request.get("operation", "recognize")
        if operation not in ("recognize", "warmup"):
            raise ValueError("未対応の操作です")
        engine = request["engine"]
        variant = engine
        if engine in ("whisper-turbo-hotwords", "whisper-turbo-nohint"):
            engine = "whisper-turbo"
        if engine not in MODELS and engine not in FAST_MODELS:
            raise ValueError("未対応のモデルです")
        device = request.get("device", "cpu")
        if device not in ("cpu", "cuda"):
            raise ValueError("未対応のデバイスです")
        beam = int(request.get("beamSize", 5))
        threads = int(request.get("cpuThreads", 2))
        workers = int(request.get("parallelWorkers", 2))
        if workers not in (1, 2):
            raise ValueError("同じモデルの並列数は1または2です")
        compute = "int8" if device == "cpu" else request.get("cudaCompute", "float16")
        if not 1 <= beam <= 5 or not 1 <= threads <= 16 or compute not in ("int8", "float16", "int8_float16"):
            raise ValueError("認識の性能設定が不正です")
        # Nemotron C ABIのスレッド数とq8_0は固定。効かないWhisper設定だけの
        # 変更で、同じ常駐モデルを破棄・再読込みしない。
        directory = root / engine
        if engine in WHISPER_MODELS and (shared := request.get("whisperDirectory")):
            directory = Path(shared)
        key = (engine, device, "runtime", "q8_0") if engine == "nemotron-3.5" else (engine, device, threads, compute, workers if engine in MODELS and engine not in QWEN_MODELS else 1, directory)
        started = time.perf_counter()
        stage = "model-load"
        with cache_lock:
            reused = key in cache
            if key not in cache:
                if progress is not None:
                    progress("loading")
                for previous in cache.values():
                    if hasattr(previous, "close"):
                        previous.close()
                cache.clear()  # 1ワーカーにつき1モデル。設定変更で旧モデルを保持し続けない。
                if not has_model_weights(directory, engine):
                    raise ValueError("モデル未導入。setup-asr.ps1を実行してください")
                with contextlib.nullcontext():
                    if engine == "reazon-k2-v2":
                        from reazon_backend import ReazonRecognizer
                        cache[key] = ReazonRecognizer(directory, device, threads)
                    elif engine == "parakeet-ja-ctc":
                        from parakeet_backend import ParakeetRecognizer
                        cache[key] = ParakeetRecognizer(directory, device, threads)
                    elif engine == "nemotron-3.5":
                        from nemotron_backend import NemotronRecognizer
                        cache[key] = NemotronRecognizer(directory, runtime_directory(root, device), device, threads)
                    elif engine in QWEN_MODELS:
                        import torch
                        from qwen_asr import Qwen3ASRModel
                        torch.set_num_threads(threads)
                        cache[key] = Qwen3ASRModel.from_pretrained(str(directory),
                            dtype=torch.float32 if device == "cpu" else torch.float16,
                            device_map="cpu" if device == "cpu" else "cuda:0",
                            attn_implementation="sdpa", local_files_only=True,
                            max_inference_batch_size=1, max_new_tokens=1024)
                    else:
                        if device == "cuda":
                            from whisper_cuda import prepare
                            prepare()
                        from faster_whisper import WhisperModel
                        cache[key] = WhisperModel(str(directory), device=device,
                            compute_type=compute,
                            cpu_threads=threads, num_workers=workers, local_files_only=True)
        stage = "warmup" if operation == "warmup" else "recognition"
        observed = None
        if engine in MODELS and engine not in QWEN_MODELS:
            try:
                reported = getattr(getattr(cache[key], "model", None), "device", None)
                if reported in ("cpu", "cuda"):
                    observed = reported
            except Exception:
                pass  # 診断情報の取得失敗は認識結果に影響させない。
        if progress is not None:
            progress("recognizing" if operation != "warmup" else "ready", observed)
        if engine in FAST_MODELS:
            from fast_audio import decode_wave, prepare_decoder
            if operation == "warmup":
                # 本体の24kHz入力の初回変換を遅らせない。音声の捨て推論はしない。
                prepare_decoder()
        loaded = time.perf_counter() - started
        if operation == "warmup":
            # 無音のダミー認識はせず、モデルを載せたまま次の要求を待つ。
            return {"ready": True, "engine": engine, "reused": reused,
                "loadSeconds": loaded, "error": ""}
        wave = base64.b64decode(request["wave"], validate=True)
        if len(wave) > 8_000_000:
            raise ValueError("音声が長すぎます")
        recognition_started = time.perf_counter()
        with contextlib.nullcontext():
            if engine in FAST_MODELS:
                decoded = decode_wave(wave)
                decode_seconds = time.perf_counter() - recognition_started
                inference_started = time.perf_counter()
                text = cache[key].transcribe(decoded, 16000).strip()
                inference_seconds = time.perf_counter() - inference_started
            elif engine in QWEN_MODELS:
                import soundfile as sf
                audio, sample_rate = sf.read(io.BytesIO(wave), dtype="float32")
                results = cache[key].transcribe(audio=(audio, sample_rate), language="Japanese", context=request.get("prompt", ""))
                text = "".join(item.text for item in results).strip()
            else:
                segments, _ = cache[key].transcribe(io.BytesIO(wave), language="ja", beam_size=beam,
                    condition_on_previous_text=False, chunk_length=15 if engine == "kotoba-v2" else 30,
                    initial_prompt=(request.get("prompt", "") or None) if variant not in ("whisper-turbo-hotwords", "whisper-turbo-nohint") else None,
                    hotwords=(request.get("prompt", "") or None) if variant == "whisper-turbo-hotwords" else None, temperature=0,
                    vad_filter=False)
                text = "".join(s.text for s in segments).strip()
        result = {"text": text, "seconds": time.perf_counter() - started, "error": "",
            "modelReused": reused, "loadSeconds": loaded,
            "recognitionSeconds": time.perf_counter() - recognition_started,
            "audioHash": hashlib.sha256(wave).hexdigest().upper()}
        if engine in FAST_MODELS:
            result.update(decodeSeconds=decode_seconds, inferenceSeconds=inference_seconds)
    except Exception as error:
        # 入力・認識文・例外の内部パスをログに流さない。
        result = {"text": "", "seconds": 0, **safe_failure(error, stage)}
    return result


def _serve(root, cache, protocol=None):
    protocol = protocol or sys.stdout
    cache_lock, output_lock = threading.Lock(), threading.Lock()
    futures, previous_key = [], None

    def run(request):
        def progress(stage, observed=None):
            if request.get("notifyProgress") and "requestId" in request:
                with output_lock:
                    print(json.dumps({"requestId": request["requestId"], "progress": stage, "observedDevice": observed}), file=protocol, flush=True)
        result = _handle(root, cache, request, cache_lock, progress)
        if "requestId" in request:
            result["requestId"] = request["requestId"]
        with output_lock:
            print(json.dumps(result, ensure_ascii=False), file=protocol, flush=True)

    # 受付・音声保持は2件まで。旧IDなしクライアントの応答順も維持する。
    with ThreadPoolExecutor(max_workers=2, thread_name_prefix="asr") as executor:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                if not isinstance(request, dict):
                    raise ValueError("要求はオブジェクトが必要です")
            except (ValueError, TypeError):
                with output_lock:
                    print(json.dumps({"error": "不正な要求です"}), file=protocol, flush=True)
                continue
            engine = request.get("engine", "")
            model = "whisper-turbo" if engine in ("whisper-turbo-hotwords", "whisper-turbo-nohint") else engine
            key = (model, request.get("device", "cpu"), None if model == "nemotron-3.5" else request.get("cpuThreads", 2),
                   None if model == "nemotron-3.5" else request.get("cudaCompute", "float16"),
                   request.get("parallelWorkers", 2) if model in MODELS and model not in QWEN_MODELS else 1,
                   request.get("whisperDirectory") if model in WHISPER_MODELS else None)
            parallel = (model in MODELS and model not in QWEN_MODELS) or model == "nemotron-3.5"
            changed = key != previous_key
            if changed or not parallel or "requestId" not in request or request.get("operation") == "warmup":
                for future in futures:
                    future.result()
                futures.clear()
            previous_key = key
            # Windowsのscipy拡張の初回ロードを、stdin待ち中の別スレッドで
            # 行うと停止する場合がある。初期化・初回要求は受付側で完了し、
            # 以後の独立認識だけを共有モデルの処理スレッドへ渡す。
            if parallel and "requestId" in request and not changed and request.get("operation") != "warmup":
                for future in futures:
                    if future.done():
                        future.result()
                futures = [future for future in futures if not future.done()]
                if len(futures) >= 2:
                    done, _ = wait(futures, return_when=FIRST_COMPLETED)
                    for future in done:
                        future.result()
                    futures = [future for future in futures if not future.done()]
                futures.append(executor.submit(run, request))
            else:
                run(request)
        for future in futures:
            future.result()


if __name__ == "__main__":
    main()

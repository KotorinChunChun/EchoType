"""日本語ASRの固定配布物と、検証付きローカル導入。推論時は通信しない。"""
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

MODELS = {
    "reazon-k2-v2": {
        "repo": "reazon-research/reazonspeech-k2-v2",
        "revision": "291488c8151be24d7da4bf7af26e533fad96e407",
        "files": {
            "encoder-epoch-99-avg-1.int8.onnx": (154670139, "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d"),
            "decoder-epoch-99-avg-1.onnx": (11767836, "58b18211ae06265466bfa17172dab574df94f76c8bcb61a3640c28ba860e4124"),
            "joiner-epoch-99-avg-1.int8.onnx": (2696970, "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de"),
            "tokens.txt": (45754, "2c3ac659818a48a0c04010e0593bbc4d7c8a24a054340b01131499c05fd52def"),
        },
    },
    "parakeet-ja-ctc": {
        "repo": "csukuangfj/sherpa-onnx-nemo-parakeet-tdt_ctc-0.6b-ja-35000-int8",
        "revision": "bef18eb066808c90bd0f5df5be685767b0732de8",
        "files": {
            "model.int8.onnx": (655542604, "3addd00ef5bd1742078389e540b77394e4a508bdf2f4c9ad1b4a76d93e76598e"),
            "tokens.txt": (28557, "732f64c53909f2620c713f4106b487d92e6f54a6915b3cd3d1dbd32f9f4f392a"),
        },
    },
    "nemotron-3.5": {
        "repo": "nvidia/nemotron-3.5-asr-streaming-0.6b",
        "revision": "ea30d66debe3740a08b573244286791d423d6b3e",
        "files": {
            "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf": (742090464, "3fc991d3badad7277c11030a7519832cddaf2057aafed6d4b25147e953a070b1"),
        },
    },
}
RUNTIME_VERSION = "0.2.0"
RUNTIMES = {
    "cpu": (5528418, "82c80451086e86194aba9af19f800da77cd66d1c28d993d0efceffd6cdb49bb8"),
    "cuda": (102183465, "9ffe5e842c39a7d3211f3b2c3b3aa1bca8c0b2455d96d961ed5f22ec5108f98d"),
}


def runtime_directory(model_root, device):
    if device not in RUNTIMES:
        raise ValueError("未対応のデバイスです")
    return Path(model_root).parent / ("nemo-speech-" + RUNTIME_VERSION) / device / "bin"


def has_weights(directory, engine):
    try:
        root = Path(directory).resolve(strict=True)
        return all((root / name).is_file() and root.samefile((root / name).resolve(strict=True).parent)
                   and (root / name).stat().st_size == size
                   for name, (size, _) in MODELS[engine]["files"].items())
    except (KeyError, OSError, ValueError, RuntimeError):
        return False


def _download(url, target, size, checksum):
    def valid(path):
        if not path.is_file() or path.stat().st_size != size:
            return False
        with path.open("rb") as source:
            return hashlib.file_digest(source, "sha256").hexdigest() == checksum
    if valid(target):
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = target.with_name(target.name + ".partial")
    with urllib.request.urlopen(url, timeout=120) as response, staging.open("wb") as output:
        shutil.copyfileobj(response, output, 1024 * 1024)
    if not valid(staging):
        raise ValueError("配布ファイルの長さ・SHA256が一致しません")
    staging.replace(target)


def _install_runtime(root, device):
    name = f"nemo-speech-{RUNTIME_VERSION}-windows-x86_64-{device}"
    target = root.parent / ("nemo-speech-" + RUNTIME_VERSION) / device
    archive = target.parent / (name + ".zip")
    print("実行環境取得: " + name, flush=True)
    _download(f"https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v{RUNTIME_VERSION}/{name}.zip",
              archive, *RUNTIMES[device])
    target.mkdir(parents=True, exist_ok=True)
    # ハッシュを照合した固定ZIPから単一ルート以下だけを展開する。
    with zipfile.ZipFile(archive) as bundle:
        for item in bundle.infolist():
            parts = item.filename.replace("\\", "/").split("/")
            if parts[0] != name or any(part in ("..", ".") or ":" in part for part in parts):
                raise ValueError("実行環境アーカイブのパスが不正です")
            if item.is_dir():
                continue
            destination = target.joinpath(*parts[1:])
            destination.parent.mkdir(parents=True, exist_ok=True)
            with bundle.open(item) as source, destination.open("wb") as output:
                shutil.copyfileobj(source, output)
    if not (target / "bin" / "nemo_speech_asr_c.dll").is_file():
        raise ValueError("実行環境DLLがありません")


def download(root, engines):
    root = Path(root)
    for engine in engines:
        model = MODELS[engine]
        directory = root / engine
        directory.mkdir(parents=True, exist_ok=True)
        marker = directory / "ready.json"
        marker.unlink(missing_ok=True)
        for name, (size, checksum) in model["files"].items():
            print(f"モデル取得: {engine} / {name}", flush=True)
            _download(f"https://huggingface.co/{model['repo']}/resolve/{model['revision']}/{name}",
                      directory / name, size, checksum)
        if engine == "nemotron-3.5":
            for device in RUNTIMES:
                _install_runtime(root, device)
        if not has_weights(directory, engine):
            raise ValueError("モデル本体が揃っていません")
        marker.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    import sys
    download(Path(sys.argv[1]), sys.argv[2:])

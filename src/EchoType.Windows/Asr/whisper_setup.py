"""固定リビジョンのWhisper重みをアプリ共通の保存先へ公開する。"""

import argparse
import contextlib
import errno
import hashlib
import json
import msvcrt
from pathlib import Path
import shutil
import sys
import urllib.request
import uuid

from huggingface_hub import HfApi
from worker import WHISPER_MODELS


REQUIRED_FILES = {"config.json", "model.bin", "tokenizer.json"}
READY_SCHEMA = 1


def require_plain_path(path):
    if not path.is_absolute() or path.drive.startswith("\\\\"):
        raise ValueError("ローカルの絶対パスを指定してください。")
    for part in (path, *path.parents):
        if part.is_symlink() or part.is_junction():
            raise PermissionError("リンク経由で共有モデルを書き込めません。")


@contextlib.contextmanager
def model_lock(path):
    require_plain_path(path)
    with path.open("a+b") as handle:
        handle.seek(0)
        try:
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError("別のアプリがモデルを準備しています。完了後に再試行してください。") from error
        try:
            yield
        finally:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


def published(directory, repo, revision):
    try:
        require_plain_path(directory / "ready.json")
        metadata = json.loads((directory / "ready.json").read_text(encoding="utf-8"))
        files = metadata["files"]
        if metadata["schema"] != READY_SCHEMA or metadata["repo"] != repo or metadata["revision"] != revision or not REQUIRED_FILES <= files.keys():
            return False
        for name, size in files.items():
            if Path(name).name != name or ":" in name or name in (".", ".."):
                return False
            path = directory / name
            require_plain_path(path)
            if not path.is_file() or not isinstance(size, int) or size <= 0 or path.stat().st_size != size:
                return False
        return True
    except (OSError, ValueError, TypeError, KeyError, AttributeError):
        return False


def matches(path, info):
    if not path.is_file() or path.stat().st_size != info.size:
        return False
    with path.open("rb") as file:
        if info.lfs:
            return hashlib.file_digest(file, "sha256").hexdigest() == info.lfs.sha256
        # Hugging Faceの通常ファイルはGit blob ID、LFSはSHA-256で照合する。
        digest = hashlib.sha1(f"blob {info.size}\0".encode("ascii"), usedforsecurity=False)
        return hashlib.file_digest(file, lambda: digest).hexdigest() == info.blob_id


def candidate_directories(primary, legacy, engine, repo, revision):
    yield legacy / engine
    if primary is not None:
        hub_directory = "models--" + repo.replace("/", "--")
        for app in ("DesktopTalkie", "ScriptSync", "VoisonaTalkAI", "AudioMascot"):
            yield primary.parent / app / hub_directory / "snapshots" / revision
        if engine == "whisper-small":
            yield primary.parent / "DesktopTalkie" / "small"


def stage_file(staging, info, source_url, candidates):
    target = staging / info.rfilename
    require_plain_path(target)
    if matches(target, info):
        return
    partial = target.with_name(target.name + ".partial")
    require_plain_path(partial)
    for candidate in candidates:
        try:
            shutil.copyfile(candidate / info.rfilename, partial)
            if matches(partial, info):
                partial.replace(target)
                return
        except OSError:
            continue
    with urllib.request.urlopen(source_url, timeout=120) as source, partial.open("wb") as output:
        shutil.copyfileobj(source, output, 1024 * 1024)
    if not matches(partial, info):
        raise ValueError("取得したモデルファイルの検証に失敗しました。")
    partial.replace(target)


def prepare(store, primary, legacy, engine):
    repo, revision = WHISPER_MODELS[engine]
    destination = store / repo / revision
    require_plain_path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with model_lock(destination.with_name(f".{revision}.lock")):
        if published(destination, repo, revision):
            return
        print(engine, flush=True)
        model = HfApi().model_info(repo, revision=revision, files_metadata=True, token=False)
        files = {item.rfilename: item for item in model.siblings
                 if Path(item.rfilename).name == item.rfilename and ":" not in item.rfilename and item.size > 0
                 and (item.rfilename in REQUIRED_FILES or item.rfilename.endswith(".json")
                      or item.rfilename.startswith(("vocabulary.", "LICENSE")))}
        if model.sha != revision or not REQUIRED_FILES <= files.keys():
            raise ValueError("固定リビジョンの必要ファイルが取得できません。")
        staging = destination.with_name(f".{revision}.partial")
        require_plain_path(staging)
        staging.mkdir(exist_ok=True)
        candidates = [destination, *candidate_directories(primary, legacy, engine, repo, revision)]
        for name, info in files.items():
            stage_file(staging, info, f"https://huggingface.co/{repo}/resolve/{revision}/{name}", candidates)
        metadata = {"schema": READY_SCHEMA, "repo": repo, "revision": revision, "files": {name: info.size for name, info in files.items()}}
        marker = staging / "ready.json"
        require_plain_path(marker)
        marker.write_text(json.dumps(metadata), encoding="utf-8")
        if destination.exists():
            # 検証を完了するまで公開済みディレクトリには手を加えない。
            destination.rename(destination.with_name(f".{revision}.invalid-{uuid.uuid4().hex}"))
        staging.rename(destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--primary", type=Path)
    parser.add_argument("--fallback", type=Path, required=True)
    parser.add_argument("--legacy", type=Path, required=True)
    parser.add_argument("models", nargs="+", choices=WHISPER_MODELS)
    args = parser.parse_args()
    for engine in args.models:
        repo, revision = WHISPER_MODELS[engine]
        if any(published(root / repo / revision, repo, revision) for root in (args.primary, args.fallback) if root):
            continue
        if args.primary is not None:
            try:
                prepare(args.primary, args.primary, args.legacy, engine)
                continue
            except OSError as error:
                if not isinstance(error, (PermissionError, FileNotFoundError, NotADirectoryError, FileExistsError)) and error.errno not in (errno.ENOSPC, errno.EROFS):
                    raise
        prepare(args.fallback, args.primary, args.legacy, engine)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # ランチャーには例外種別だけを返す。外部URL・資格情報・ローカルパスは表示しない。
        print(type(error).__name__, file=sys.stderr, flush=True)
        raise SystemExit(1)

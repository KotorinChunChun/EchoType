"""Whisper専用venvのCUDA 12 DLLを子プロセス内だけで探索する。"""
import ctypes
import os
from pathlib import Path
import sys
import threading

_lock = threading.Lock()
_handles = []
_libraries = {}
_directories_prepared = False
_ready = False


def prepare():
    global _ready, _directories_prepared
    if sys.platform != "win32":
        return
    with _lock:
        if _ready:
            return
        packages = Path(sys.prefix) / "Lib" / "site-packages"
        directories = [packages / "nvidia" / "cublas" / "bin", packages / "ctranslate2"]
        directories = [directory for directory in directories if directory.is_dir()]
        # CTranslate2の遅延LoadLibraryとPython 3.8以降のDLL探索の両方を補完。
        # 設定はこのワーカーだけ。OS・親アプリ・他エンジンのPATHは変更しない。
        if not _directories_prepared:
            current = os.environ.get("PATH", "")
            added = []
            try:
                for directory in directories:
                    added.append(os.add_dll_directory(str(directory)))
            except OSError:
                for handle in added:
                    handle.close()
                raise RuntimeError("Could not prepare cublas64_12.dll search") from None
            _handles.extend(added)
            os.environ["PATH"] = os.pathsep.join([*(str(p) for p in directories), current])
            _directories_prepared = True
        # cuBLASはモデル読込み後の初回推論で必要になる。事前準備時に不足を検出する。
        for name in ("cublasLt64_12.dll", "cublas64_12.dll"):
            if name in _libraries:
                continue
            library = next((p / name for p in directories if (p / name).is_file()), None)
            try:
                _libraries[name] = ctypes.WinDLL(str(library) if library else name)
            except OSError:
                raise RuntimeError("Could not load " + name) from None
        _ready = True

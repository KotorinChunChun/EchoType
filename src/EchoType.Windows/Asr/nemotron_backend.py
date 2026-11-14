"""NeMo-Speech.cpp 0.2.0 の C ABI を使う、ローカル専用の常駐認識器。"""

from contextlib import contextmanager
import ctypes as ct
import os
from pathlib import Path
import sys
import threading

import numpy as np


MODEL_FILENAME = "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf"
DLL_FILENAME = "nemo_speech_asr_c.dll"
SDK_VERSION = "nemo-speech-asr 0.2.0"
_OUTPUT_LOCK = threading.RLock()
_OUTPUT_ISOLATED = False


def isolate_native_output():
    """専用ワーカーがfd 1を恒久的にstderrへ切り替えた後にだけ呼ぶ。"""
    global _OUTPUT_ISOLATED
    _OUTPUT_ISOLATED = True


# 公式 v0.2.0 include/nemo_speech/asr.h の自然アラインメントを維持する。
# https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/include/nemo_speech/asr.h
class _BackendConfig(ct.Structure):
    _fields_ = [("size", ct.c_size_t), ("gpu", ct.c_int32)]


class _ModelConfig(ct.Structure):
    _fields_ = [("size", ct.c_size_t), ("path", ct.c_char_p), ("name", ct.c_char_p)]


class _BatchingConfig(ct.Structure):
    _fields_ = [("size", ct.c_size_t), ("enable", ct.c_bool),
                *[(name, ct.c_int32) for name in ("max_batch_size", "max_queue_delay_us",
                  "max_queue_depth", "ingress_cohort_delay_us", "state_arena_slots")]]


class _RecognizerConfig(ct.Structure):
    _fields_ = [("size", ct.c_size_t),
                ("backend", ct.POINTER(_BackendConfig)),
                ("model", ct.POINTER(_ModelConfig)),
                *[(name, ct.c_void_p) for name in (
                    "streaming", "decoder", "vad", "endpointing", "postproc", "diar", "batching")]]


class _RecognitionOptions(ct.Structure):
    _fields_ = [("size", ct.c_size_t), ("request_id", ct.c_char_p),
                ("language_code", ct.c_char_p), ("interim_results", ct.c_bool),
                ("enable_word_time_offsets", ct.c_bool),
                ("enable_automatic_punctuation", ct.c_bool),
                ("verbatim_transcripts", ct.c_bool), ("profanity_filter", ct.c_bool),
                ("stop_history_eou_ms", ct.c_int32), ("speech_contexts", ct.c_void_p),
                ("speech_context_count", ct.c_size_t), ("max_alternatives", ct.c_int32),
                ("enable_speaker_diarization", ct.c_bool), ("max_speaker_count", ct.c_int32)]


def _init_config(kind):
    # backend/model/recognizer には公式の default 関数が存在しない。
    value = kind()
    value.size = ct.sizeof(kind)
    return value


@contextmanager
def _native_output_to_stderr():
    """C/C++ が stdout に出力してもワーカーの JSON 行を壊さない。"""
    if _OUTPUT_ISOLATED:
        yield
        return
    # 専用ワーカープロセス内のみで使用する。Python の redirect_stdout だけでは
    # CRT の stdout は変更されないため、ネイティブ呼出し中の fd 1 を切り替える。
    with _OUTPUT_LOCK:
        sys.stdout.flush()
        saved = os.dup(1)
        try:
            os.dup2(2, 1)
            yield
        finally:
            try:
                # 配布 DLL は共有 UCRT を使用する。遅延出力も復元前に stderr へ流す。
                if os.name == "nt":
                    crt = ct.CDLL("ucrtbase.dll", winmode=0x00000800)
                    crt.fflush.argtypes = [ct.c_void_p]
                    crt.fflush.restype = ct.c_int
                    crt.fflush(None)
            finally:
                os.dup2(saved, 1)
                os.close(saved)


def _local_file(directory: Path, filename: str) -> Path:
    root = Path(directory).resolve(strict=True)
    path = root / filename
    if str(root).startswith(("\\\\", "//")):
        raise ValueError("Nemotron のファイルはローカルドライブに配置してください。")
    resolved = path.resolve(strict=True)
    # MSIX の仮想パスと実体パスは同じディレクトリでも文字列が一致しない。
    if not root.samefile(resolved.parent) or not resolved.is_file():
        raise ValueError(f"Nemotron のファイルが指定フォルダー外を参照しています: {filename}")
    if resolved.stat().st_size == 0:
        raise ValueError(f"Nemotron のファイルが空です: {filename}")
    return resolved


def _load_library(runtime_directory: Path):
    if os.name != "nt" or ct.sizeof(ct.c_void_p) != 8:
        raise RuntimeError("Nemotron SDK は Windows x64 専用です。")
    path = _local_file(runtime_directory, DLL_FILENAME)
    search = os.add_dll_directory(str(path.parent))
    try:
        with _native_output_to_stderr():
            return ct.CDLL(str(path)), search
    except BaseException:
        search.close()
        raise


def _bind(library, name, arguments, result):
    function = getattr(library, "nemo_speech_asr_" + name)
    function.argtypes = arguments
    function.restype = result
    return function


class NemotronRecognizer:
    """音声はメモリのみ。認識器を共有し、認識結果は毎回解放する。"""

    def __init__(self, directory: Path, runtime_directory: Path, device: str, threads: int):
        self._lock = threading.RLock()
        self._condition = threading.Condition(self._lock)
        self._active = 0
        self._closing = False
        self._recognizer = ct.c_void_p()
        self._in_stream = False
        self._library = None
        self._search = None
        if device not in ("cpu", "cuda"):
            raise ValueError("Nemotron の device は cpu または cuda を指定してください。")
        if isinstance(threads, bool) or not isinstance(threads, int) or threads < 0:
            raise ValueError("Nemotron の threads は 0 以上の整数を指定してください。")
        self.device = device
        self.requested_threads = threads
        # C ABI 0.2.0 には threads 指定がない。内部値は min(8, hardware_concurrency)。
        self.threads = None
        model_path = _local_file(directory, MODEL_FILENAME)
        try:
            self._library, self._search = _load_library(runtime_directory)
            self._bind_api()
            version = self._version().decode("utf-8", errors="replace")
            if version != SDK_VERSION:
                raise RuntimeError(f"Nemotron SDK の版が不一致です: {version}")
            backend = _init_config(_BackendConfig)
            backend.gpu = 0 if device == "cuda" else -1
            model = _init_config(_ModelConfig)
            model.path = str(model_path).encode("utf-8")
            config = _init_config(_RecognizerConfig)
            config.backend = ct.pointer(backend)
            config.model = ct.pointer(model)
            # SDK v0.2.0は1認識器・1モデルで独立要求を処理する。CUDAだけ動的バッチ化。
            batching = _init_config(_BatchingConfig)
            batching.enable = device == "cuda"
            batching.max_batch_size = 2
            batching.max_queue_delay_us = 1000
            batching.max_queue_depth = 2
            batching.ingress_cohort_delay_us = 1000
            batching.state_arena_slots = 2
            config.batching = ct.cast(ct.pointer(batching), ct.c_void_p)
            with _native_output_to_stderr():
                self._check(self._create(ct.byref(config), ct.byref(self._recognizer)), "モデル読込み")
            if not self._recognizer:
                raise RuntimeError("Nemotron SDK が認識器を返しませんでした。")
            print("Nemotron SDK 0.2.0: threads は C API 未対応です。"
                  f"要求値={threads}、SDK既定=min(8,論理CPU数)を使用します。", file=sys.stderr)
        except BaseException:
            self.close()
            raise

    def _bind_api(self):
        library = self._library
        handle = ct.c_void_p
        out_handle = ct.POINTER(handle)
        self._version = _bind(library, "version", [], ct.c_char_p)
        self._last_error = _bind(library, "last_error", [], ct.c_char_p)
        self._options_default = _bind(library, "recognition_options_default", [], _RecognitionOptions)
        self._create = _bind(library, "create", [ct.POINTER(_RecognizerConfig), out_handle], ct.c_int)
        self._destroy = _bind(library, "destroy", [handle], None)
        self._recognize = _bind(library, "recognize_f32", [handle, ct.POINTER(_RecognitionOptions),
                                ct.POINTER(ct.c_float), ct.c_size_t, ct.c_int32, out_handle], ct.c_int)
        self._count = _bind(library, "result_alternative_count", [handle], ct.c_size_t)
        self._transcript = _bind(library, "result_transcript", [handle, ct.c_size_t], ct.c_char_p)
        self._result_destroy = _bind(library, "result_destroy", [handle], None)
        self._stream_create = _bind(library, "streaming_recognize",
                                   [handle, ct.POINTER(_RecognitionOptions), out_handle], ct.c_int)
        self._stream_push = _bind(library, "stream_push_f32",
                                 [handle, ct.POINTER(ct.c_float), ct.c_size_t, ct.c_int32], ct.c_int)
        self._stream_next = _bind(library, "stream_next", [handle, out_handle], ct.c_int)
        self._stream_finish = _bind(library, "stream_finish", [handle], ct.c_int)
        self._stream_close = _bind(library, "stream_close", [handle], None)
        self._is_final = _bind(library, "result_is_final", [handle], ct.c_bool)

    def _check(self, status, operation):
        if status != 0:
            # last_error は次の API 呼出しで変わり得るため、解放前にコピーする。
            detail = self._last_error()
            detail = detail.decode("utf-8", errors="replace") if detail else "詳細なし"
            raise RuntimeError(f"Nemotron {operation}に失敗しました (status={status}): {detail}")

    @staticmethod
    def _audio(audio: np.ndarray, sample_rate: int) -> np.ndarray:
        if not isinstance(audio, np.ndarray) or audio.ndim != 1 or audio.dtype != np.float32:
            raise ValueError("Nemotron の入力は mono float32 の numpy 配列が必要です。")
        if isinstance(sample_rate, bool) or not isinstance(sample_rate, int) or not 8000 <= sample_rate <= 96000:
            raise ValueError("Nemotron の入力サンプルレートは 8000〜96000 Hz が必要です。")
        if not np.isfinite(audio).all():
            raise ValueError("Nemotron の入力音声に非有限値が含まれています。")
        return np.ascontiguousarray(audio)

    def _options(self):
        options = self._options_default()
        options.language_code = b"ja-JP"
        options.enable_automatic_punctuation = True
        return options

    def _ensure_open(self):
        if not self._recognizer or self._closing:
            raise RuntimeError("Nemotron 認識器は終了済みです。")
        if self._in_stream:
            raise RuntimeError("Nemotron の通知コールバックから認識器を再操作できません。")

    def _result_text(self, result):
        if self._count(result) == 0:
            return ""
        text = self._transcript(result, 0)
        return text.decode("utf-8") if text else ""

    def transcribe(self, audio: np.ndarray, sample_rate: int = 16000) -> str:
        audio = self._audio(audio, sample_rate)
        with self._condition:
            self._ensure_open()
            if audio.size == 0:
                return ""
            self._active += 1
        try:
            with _native_output_to_stderr():
                options = self._options()
                result = ct.c_void_p()
                try:
                    self._check(self._recognize(self._recognizer, ct.byref(options),
                                audio.ctypes.data_as(ct.POINTER(ct.c_float)), audio.size,
                                sample_rate, ct.byref(result)), "文字起こし")
                    if not result:
                        raise RuntimeError("Nemotron SDK が認識結果を返しませんでした。")
                    return self._result_text(result)
                finally:
                    if result:
                        self._result_destroy(result)
        finally:
            with self._condition:
                self._active -= 1
                self._condition.notify_all()

    def transcribe_streaming(self, audio: np.ndarray, sample_rate: int = 16000,
                             on_update=None) -> str:
        """測定用: メモリ音声を 560ms ずつ渡す。実時間の待機は行わない。"""
        audio = self._audio(audio, sample_rate)
        if on_update is not None and not callable(on_update):
            raise ValueError("on_update は呼出し可能な関数を指定してください。")
        with self._lock:
            self._ensure_open()
            while self._active:
                self._condition.wait()
                self._ensure_open()
            if audio.size == 0:
                return ""
            stream = ct.c_void_p()
            committed = ""
            latest = ""
            self._in_stream = True

            def drain():
                nonlocal committed, latest
                while True:
                    result = ct.c_void_p()
                    with _native_output_to_stderr():
                        try:
                            self._check(self._stream_next(stream, ct.byref(result)), "逐次結果取得")
                            if not result:
                                return
                            text = self._result_text(result)
                            final = self._is_final(result)
                        finally:
                            if result:
                                self._result_destroy(result)
                    # 途中結果はその区間の全文。毎回連結すると同じ内容が重複する。
                    current = committed + text
                    if final:
                        committed = current
                    if current != latest:
                        latest = current
                        if on_update is not None:
                            on_update(current)

            try:
                with _native_output_to_stderr():
                    options = self._options()
                    options.interim_results = True
                    self._check(self._stream_create(self._recognizer, ct.byref(options),
                                ct.byref(stream)), "逐次認識開始")
                    if not stream:
                        raise RuntimeError("Nemotron SDK がストリームを返しませんでした。")
                chunk_samples = max(1, round(sample_rate * 0.560))
                for offset in range(0, audio.size, chunk_samples):
                    chunk = audio[offset:offset + chunk_samples]
                    with _native_output_to_stderr():
                        self._check(self._stream_push(stream,
                                    chunk.ctypes.data_as(ct.POINTER(ct.c_float)),
                                    chunk.size, sample_rate), "逐次音声入力")
                    drain()
                with _native_output_to_stderr():
                    self._check(self._stream_finish(stream), "逐次認識確定")
                drain()
                return committed
            finally:
                try:
                    if stream:
                        with _native_output_to_stderr():
                            self._stream_close(stream)
                finally:
                    self._in_stream = False

    def close(self):
        with self._condition:
            if self._in_stream:
                raise RuntimeError("Nemotron の通知コールバックから認識器を終了できません。")
            self._closing = True
            while self._active:
                self._condition.wait()
            try:
                if self._recognizer:
                    handle, self._recognizer = self._recognizer, ct.c_void_p()
                    with _native_output_to_stderr():
                        self._destroy(handle)
            finally:
                if self._search is not None:
                    search, self._search = self._search, None
                    search.close()

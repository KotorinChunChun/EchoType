param([string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'EchoType'), [string]$Python = 'python',
    [ValidateSet('whisper-turbo','kotoba-v2','whisper-base','whisper-small','qwen3-ja','qwen3-0.6b','qwen3-1.7b','whisper-large-v3','reazon-k2-v2','parakeet-ja-ctc','nemotron-3.5')]
    [string[]]$Models = @('whisper-turbo','kotoba-v2','whisper-base','whisper-small'),
    [switch]$WithGpuRuntime, [switch]$GpuRuntimeOnly)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'control') -Force | Out-Null
try { $setupLock = [IO.File]::Open((Join-Path $DataRoot 'control/asr-setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw '別のモデル導入が実行中です。完了後に再試行してください。' }
try {
$sourceRoot = Join-Path $PSScriptRoot '../../src/EchoType.Windows/Asr'
if (!(Test-Path $sourceRoot)) { $sourceRoot = Join-Path $PSScriptRoot 'Asr' }
function Install-WhisperGpuRuntime([string]$EnvironmentPython) {
    if (!(Test-Path -LiteralPath $EnvironmentPython)) { throw 'Whisper用Python環境がありません。先にWhisperモデルを導入してください。' }
    & $EnvironmentPython -c 'import sys,platform; assert sys.platform == "win32" and platform.machine() == "AMD64", "Windows x64が必要です"'
    if ($LASTEXITCODE) { throw 'Whisper GPUランタイムの対象はWindows x64です。' }
    & $EnvironmentPython -m pip install --disable-pip-version-check --only-binary=:all: --no-deps --require-hashes --index-url https://pypi.org/simple -r (Join-Path $sourceRoot 'requirements-cuda.txt')
    if ($LASTEXITCODE) { throw 'Whisper用CUDA 12 cuBLASの導入に失敗しました。' }
    & $EnvironmentPython -c 'import sys,pathlib; root=pathlib.Path(sys.prefix)/"Lib/site-packages/nvidia/cublas/bin"; assert all((root/name).is_file() for name in ["cublas64_12.dll","cublasLt64_12.dll"]), "cuBLAS DLLがありません"'
    if ($LASTEXITCODE) { throw 'Whisper用CUDA DLLの配置を確認できませんでした。' }
}
if ($GpuRuntimeOnly) {
    Install-WhisperGpuRuntime (Join-Path $DataRoot 'model/venv/Scripts/python.exe')
    Write-Output 'Whisper用CUDA 12 cuBLASの準備が完了しました。モデル本体は変更していません。'
    return
}
if ($Python -eq 'python') {
    $installedPython = Join-Path $env:LOCALAPPDATA 'Programs/Python/Python312/python.exe'
    if (Test-Path -LiteralPath $installedPython) { $Python = $installedPython }
}
if (!(Get-Command $Python -ErrorAction SilentlyContinue)) { throw 'Python 3.12以降が見つかりません。Pythonを導入してから再試行してください。' }
& $Python -c 'import sys; assert sys.version_info >= (3,12), "Python 3.12以降が必要です"'
if ($LASTEXITCODE) { throw 'Python 3.12以降が必要です。' }
function Ensure-AsrEnvironment([string]$EnvironmentRoot) {
    $environmentPython = Join-Path $EnvironmentRoot 'Scripts/python.exe'
    if ((Test-Path -LiteralPath $environmentPython) -and (Test-Path -LiteralPath (Join-Path $EnvironmentRoot 'pyvenv.cfg'))) {
        & $environmentPython -m pip --version | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
    }
    # 中断でpython.exeだけ作られた状態を修復する。正常な環境は再作成しない。
    & $Python -m venv $EnvironmentRoot
    if ($LASTEXITCODE) { throw "音声用Python環境の作成に失敗しました: $EnvironmentRoot" }
}
$modelRoot = Join-Path $DataRoot 'model/asr'
$venvRoot = Join-Path $DataRoot 'model/venv'
New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null
Write-Output "導入先: $modelRoot / モデル: $($Models -join ', ')"
foreach ($model in $Models) {
    $marker = Join-Path $modelRoot "$model/ready.json"
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
}
$qwenModels = @($Models | Where-Object { $_ -in @('qwen3-ja', 'qwen3-0.6b', 'qwen3-1.7b') })
$fastModels = @($Models | Where-Object { $_ -in @('reazon-k2-v2', 'parakeet-ja-ctc', 'nemotron-3.5') })
$whisperModels = @($Models | Where-Object { $_ -notin @($qwenModels + $fastModels) })
if ($fastModels.Count -gt 0) {
    $fastRoot = Join-Path $DataRoot 'model/fast-asr-venv'
    $fastPython = Join-Path $fastRoot 'Scripts/python.exe'
    Ensure-AsrEnvironment $fastRoot
    & $fastPython -m pip install --disable-pip-version-check -r (Join-Path $sourceRoot 'requirements-fast.txt')
    if ($LASTEXITCODE) { throw '高速ASR用ライブラリの導入に失敗しました。' }
    & $fastPython (Join-Path $sourceRoot 'fast_models.py') $modelRoot @fastModels
    if ($LASTEXITCODE) { throw '高速ASRモデル・実行環境の取得に失敗しました。' }
}
if ($whisperModels.Count -gt 0) {
    Ensure-AsrEnvironment $venvRoot
}
$asrPython = Join-Path $venvRoot 'Scripts/python.exe'
if ($qwenModels.Count -gt 0) {
    $qwenRoot = Join-Path $DataRoot 'model/qwen-venv'
    $qwenPython = Join-Path $qwenRoot 'Scripts/python.exe'
    Ensure-AsrEnvironment $qwenRoot
    & $qwenPython -m pip install --disable-pip-version-check 'torch==2.10.0' --index-url https://download.pytorch.org/whl/cpu
    if ($LASTEXITCODE) { throw 'Qwen用PyTorch CPU版の導入に失敗しました。' }
    & $qwenPython -m pip install --disable-pip-version-check -r (Join-Path $sourceRoot 'requirements-qwen.txt')
    if ($LASTEXITCODE) { throw 'Qwen音声認識ライブラリの導入に失敗しました。' }
    & $qwenPython (Join-Path $sourceRoot 'worker.py') --download $modelRoot @qwenModels
    if ($LASTEXITCODE) { throw 'Qwenモデルの取得に失敗しました。' }
}
if ($whisperModels.Count -eq 0) { Write-Output '指定モデルの準備が完了しました。'; return }
& $asrPython -m pip install --disable-pip-version-check -r (Join-Path $sourceRoot 'requirements.txt')
if ($LASTEXITCODE) { throw '音声認識ライブラリの導入に失敗しました。' }
if ($WithGpuRuntime) { Install-WhisperGpuRuntime $asrPython }
& $asrPython (Join-Path $sourceRoot 'worker.py') --download $modelRoot @whisperModels
if ($LASTEXITCODE) { throw '音声モデルの取得に失敗しました。' }
Write-Output 'ローカル音声モデルの準備が完了しました。'
} finally { $setupLock.Dispose() }

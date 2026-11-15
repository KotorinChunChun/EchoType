#Requires -Version 7
param([Parameter(Mandatory)][string]$SdkIncludePath, [string]$OutputDirectory=(Join-Path $PSScriptRoot '../bin'), [ValidateSet('Probe','BackendProbe')][string]$Source='Probe', [string]$WindowsCoreDll='C:\Windows\SystemApps\SxS\MicrosoftWindows.61869721.Livtop_cw5n1h2txyewy\LiveCaptions\Microsoft.CognitiveServices.Speech.core.dll')
$ErrorActionPreference='Stop'
$sdk=(Resolve-Path -LiteralPath $SdkIncludePath).Path
if(!(Test-Path "$sdk/c_api/speechapi_c_embedded_speech_config.h")) {throw '公式Speech SDKのbuild/native/includeを指定してください。'}
$vswhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -or !$vs) { throw 'Visual StudioのC++ x64ビルドツールが見つかりません。' }
$cl=Get-ChildItem "$vs/VC/Tools/MSVC/*/bin/Hostx64/x64/cl.exe" | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$msvc=Split-Path (Split-Path (Split-Path (Split-Path $cl)))
$kit="${env:ProgramFiles(x86)}/Windows Kits/10"
$kitVersion=Get-ChildItem "$kit/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
$out=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$taskInclude=@("$msvc/include", "$kit/Include/$kitVersion/ucrt", "$kit/Include/$kitVersion/shared", "$kit/Include/$kitVersion/um", "$kit/Include/$kitVersion/winrt", "$sdk/c_api", "$sdk/cxx_api")
$taskLib=@("$msvc/lib/x64", "$kit/Lib/$kitVersion/ucrt/x64", "$kit/Lib/$kitVersion/um/x64")
$oldInclude=$env:INCLUDE; $oldLib=$env:LIB
try {
    $env:INCLUDE=$taskInclude -join ';'; $env:LIB=$taskLib -join ';'
    $links=@()
    if($Source -eq 'BackendProbe') {
        # 実Windows DLLのexportsからimport LIBだけ生成。別版SDK DLLをコピーしない。
        $core=(Resolve-Path -LiteralPath $WindowsCoreDll).Path
        $dump=& (Join-Path (Split-Path $cl) 'dumpbin.exe') /exports $core
        if($LASTEXITCODE){throw 'exports取得失敗'}
        $names=@($dump | ForEach-Object {if($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)\s*$'){$Matches[1]}})
        if(!$names.Count){throw 'exportsが空です'}
        @('LIBRARY Microsoft.CognitiveServices.Speech.core.dll','EXPORTS')+$names | Set-Content "$out/WindowsSpeech.def" -Encoding ascii
        & (Join-Path (Split-Path $cl) 'lib.exe') /nologo /machine:x64 "/def:$out/WindowsSpeech.def" "/out:$out/WindowsSpeech.lib"
        if($LASTEXITCODE){throw 'import LIB生成失敗'}
        $links=@("$out/WindowsSpeech.lib",'delayimp.lib','/DELAYLOAD:Microsoft.CognitiveServices.Speech.core.dll')
    }
    & $cl /nologo /W4 /WX /EHsc /std:c++17 /utf-8 (Join-Path $PSScriptRoot "../src/$Source.cpp") "/Fo$out/$Source.obj" "/Fe$out/$Source.exe" /link kernel32.lib runtimeobject.lib ole32.lib @links
    if($LASTEXITCODE -ne 0) {throw "ビルド失敗: $LASTEXITCODE"}
} finally { $env:INCLUDE=$oldInclude; $env:LIB=$oldLib }
Write-Output "$out/$Source.exe"

param([string]$Root = 'C:/AI/Models/Speech/Haas')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$Root = [IO.Path]::GetFullPath($Root)
New-Item -ItemType Directory -Path $Root -Force | Out-Null
$setupLock = [IO.File]::Open((Join-Path $Root 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $marker = Join-Path $Root 'ready.json'
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
    function Fetch-Verified([string]$Url, [string]$File, [string]$Hash) {
        if ((Test-Path -LiteralPath $File) -and (Get-FileHash -LiteralPath $File).Hash -eq $Hash) { return }
        Write-Output "取得中: $([IO.Path]::GetFileName($File))"
        $part = $File + '.download'
        Invoke-WebRequest -Uri $Url -OutFile $part
        if ((Get-FileHash -LiteralPath $part).Hash -ne $Hash) { throw '取得ファイルのSHA-256が一致しません。' }
        Move-Item -LiteralPath $part -Destination $File -Force
    }
    $archive = Join-Path $Root 'speech-0.10.0-windows-x64-vulkan.zip'
    Fetch-Verified 'https://github.com/nyosegawa/speech.cpp/releases/download/v0.10.0/speech-0.10.0-windows-x64-vulkan.zip' $archive '2f88eb9e29242f1846a80bd2d418553bb0b17637254092b66b73803380a94ab4'
    $runtime = Join-Path $Root 'speech-0.10.0'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.Name -notin @('speech.exe','speech.dll','speech.h','speech.lib')) { throw '配布アーカイブの構成が想定外です。' }
            $destination = Join-Path $runtime $entry.Name
            $source = $entry.Open()
            try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source)) } finally { $source.Dispose() }
            if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination).Hash -ne $expected) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
            }
        }
    } finally { $zip.Dispose() }
    $model = Join-Path $Root 'Haas-616M-ja-v1.0-Q8_0.gguf'
    Fetch-Verified 'https://huggingface.co/sakasegawa/Haas-ja-v1.0-GGUF/resolve/6b965d5007ba780585de51549d57a8c1c0d67c7e/Haas-616M-ja-v1.0-Q8_0.gguf' $model 'cd5f60b2f1c411404b83fca4a65e3ce30a26d9e951b6a346529e65f22758afeb'
    & (Join-Path $runtime 'speech.exe') info $model
    if ($LASTEXITCODE -ne 0) { throw 'Haasのモデル形式を確認できませんでした。' }
    @{ version='0.10.0'; model='Haas-ja-v1.0'; modelSha256=(Get-FileHash -LiteralPath $model).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'ready.json') -Encoding utf8
    Write-Output 'HaasのWindows実行環境を準備しました。CPUまたはVulkan GPUを使用できます。'
} finally { $setupLock.Dispose() }

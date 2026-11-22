# ScrollVolume をビルドする（Windows 標準の .NET Framework 4.x コンパイラを使用、追加インストール不要）
# 使い方: .\build.ps1            → .\ScrollVolume.exe
#         .\build.ps1 -Out 先.exe → 指定した場所（起動中の exe を上書きできないときなど）
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'ScrollVolume.exe' }
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$null = New-Item -ItemType Directory -Force (Split-Path $Out)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu `
    "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    "/win32icon:$(Join-Path $root 'src\app.ico')" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$Out" @src
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Host "OK: $Out"
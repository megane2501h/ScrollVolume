# ScrollVolume をビルドする（Windows 標準の .NET Framework 4.x コンパイラを使用、追加インストール不要）
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu `
    "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    "/win32icon:$(Join-Path $root 'src\app.ico')" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$(Join-Path $root 'ScrollVolume.exe')" @src
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Host "OK: $(Join-Path $root 'ScrollVolume.exe')"

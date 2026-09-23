<#
    RTX Windows Updater - tek dosya EXE üretimi
    Çıktı: <E-mre_App>\RTX Windows Updater.exe

    Kullanım (E-mre_App klasöründe):
        powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1 -Version 1.1.0
#>
param(
    # Boş bırakılırsa .csproj içindeki <Version> kullanılır.
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$root    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\RtxWindowsUpdater\RtxWindowsUpdater.csproj'
$publish = Join-Path $root 'build\publish'
$ico     = Join-Path $root 'assets\E-mreLogo.ico'
$jpg     = Join-Path $root 'assets\E-mreLogo.jpg'

# 1) .NET 8 SDK'yı bul (PATH, kullanıcı klasörüne kurulmuş SDK veya Program Files)
$candidates = @()
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$candidates += (Join-Path $env:LOCALAPPDATA 'dotnet-sdk8\dotnet.exe')
$candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')

$dotnet = $null
foreach ($c in $candidates) {
    if ((Test-Path $c) -and (& $c --list-sdks 2>$null | Select-String '^8\.')) { $dotnet = $c; break }
}
if (-not $dotnet) {
    throw ".NET 8 SDK bulunamadı. https://dotnet.microsoft.com/download/dotnet/8.0 adresinden kurun veya 'winget install Microsoft.DotNet.SDK.8' çalıştırın."
}
Write-Host "dotnet: $dotnet"

# 2) Logo -> ICO (yoksa veya logo daha yeniyse oluştur)
if (-not (Test-Path $jpg)) { throw "Logo bulunamadı: $jpg" }
if (-not (Test-Path $ico) -or ((Get-Item $jpg).LastWriteTime -gt (Get-Item $ico).LastWriteTime)) {
    & (Join-Path $PSScriptRoot 'Create-Icon.ps1') -Source $jpg -Target $ico
}

# 3) Tek dosya, self-contained yayın (hedef bilgisayarda .NET kurulu olması gerekmez)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

$publishArgs = @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-o', $publish
)
if ($Version) {
    $clean = $Version.TrimStart('v', 'V')
    Write-Host "Sürüm: $clean"
    $publishArgs += "-p:Version=$clean"
}

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız (çıkış kodu $LASTEXITCODE)." }

# 4) EXE'yi E-mre_App kök klasörüne kopyala
$exe = Join-Path $publish 'RTX Windows Updater.exe'
if (-not (Test-Path $exe)) { throw "Yayın çıktısında EXE bulunamadı: $exe" }
$target = Join-Path $root 'RTX Windows Updater.exe'
Copy-Item $exe $target -Force

$size = [Math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host ""
Write-Host "Tamamlandı: $target ($size MB)" -ForegroundColor Green

<#
    E-mre Control Center - tek dosya EXE üretimi
    Çıktı: <proje klasörü (E-mre Control Center)>\E-mre Control Center.exe  (klasör, betiğin konumundan bulunur; adı önemli değildir)
           ve aynı EXE'nin kurulum adıyla kopyası: E-mre Control Center Setup.exe

    Kullanım (proje klasöründe):
        powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1 -Version 1.4.0
        powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1 -SignThumbprint <sertifika parmak izi>

    Kod imzalama (isteğe bağlı): Windows'un güvendiği bir kuruluştan alınmış kod imzalama sertifikası (USB anahtar veya
    Windows sertifika deposundaki sertifika) ile EXE SHA-256 + zaman damgasıyla imzalanır ve imza doğrulanır. UAC'deki
    "Yayıncı" alanı YALNIZCA bu imzadan gelir; EXE içindeki şirket adı bunu değiştirmez. Parametre verilmezse EXE imzasızdır.
#>
param(
    # Boş bırakılırsa .csproj içindeki <Version> kullanılır.
    [string]$Version = '',

    # Kod imzalama sertifikasının parmak izi (Kişisel/My deposunda: CurrentUser veya LocalMachine). Boşsa imzalanmaz.
    [string]$SignThumbprint = '',

    # Zaman damgası sunucusu (sertifika süresi dolsa da imza geçerli kalır; signtool ile RFC 3161). Sağlayıcının önerdiği kullanılabilir.
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$appName = 'E-mre Control Center'   # csproj <AssemblyName> ile aynı olmalı (EXE adı)

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

$exe = Join-Path $publish "$appName.exe"
if (-not (Test-Path $exe)) { throw "Yayın çıktısında EXE bulunamadı: $exe" }

# 4) (İsteğe bağlı) Kod imzalama. İmza doğrulanamazsa derleme hata verir ve EXE kök klasöre KOPYALANMAZ.
$signedBy = $null
if ($SignThumbprint) {
    $thumb = ($SignThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $stores = 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My'
    $cert = Get-ChildItem -Path $stores -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
    if (-not $cert) {
        $any = Get-ChildItem -Path $stores -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
        if ($any) { throw "Sertifika bulundu ama kod imzalama için kullanılamıyor (kod imzalama amaçlı değil veya özel anahtarına erişilemiyor): $($any.Subject)" }
        throw "Kod imzalama sertifikası bulunamadı (parmak izi $thumb). Sertifika Kişisel (My) depoda olmalı; USB anahtar kullanılıyorsa takılı olmalı."
    }
    if ($cert.NotAfter -lt (Get-Date)) { throw "Sertifikanın süresi dolmuş ($($cert.NotAfter)): $($cert.Subject)" }
    Write-Host "İmzalanıyor: $($cert.Subject)"

    # Windows SDK'daki signtool varsa o kullanılır (RFC 3161 zaman damgası + UAC'de görünen açıklama); yoksa PowerShell'in kendi komutu.
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if ($signtool) {
        $storeArgs = if ($cert.PSParentPath -like '*LocalMachine*') { @('/sm') } else { @() }
        & $signtool sign @storeArgs /sha1 $thumb /fd sha256 /tr $TimestampServer /td sha256 /d $appName $exe
        if ($LASTEXITCODE -ne 0) { throw "signtool imzalama başarısız (çıkış kodu $LASTEXITCODE)." }
    } else {
        $r = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer
        if ($r.Status -ne 'Valid') { throw "İmzalama başarısız: $($r.Status) - $($r.StatusMessage)" }
    }

    # Doğrulama: Windows'un gözünden geçerli imza, doğru sertifika ve zaman damgası. Aksi başarı sayılmaz.
    $sig = Get-AuthenticodeSignature -FilePath $exe
    if ($sig.Status -ne 'Valid') { throw "İmza doğrulanamadı: $($sig.Status) - $($sig.StatusMessage). EXE kopyalanmadı." }
    if ($sig.SignerCertificate.Thumbprint -ne $thumb) { throw "EXE beklenmeyen bir sertifikayla imzalı: $($sig.SignerCertificate.Subject)" }
    if (-not $sig.TimeStamperCertificate) { throw "Zaman damgası eklenemedi (sunucu: $TimestampServer). EXE kopyalanmadı." }
    $signedBy = $sig.SignerCertificate.Subject
}

# 5) EXE'yi proje kök klasörüne kopyala. Kurulum dosyası aynı EXE'dir: adında "Setup" geçtiği için kurulum ekranıyla açılır ve
#    kendini Program Files'a "E-mre Control Center.exe" olarak kurar (ayrı bir kurulum aracı gerekmez; imzalıysa imza da aynıdır).
$target = Join-Path $root "$appName.exe"
Copy-Item $exe $target -Force
$setup = Join-Path $root "$appName Setup.exe"
Copy-Item $exe $setup -Force
if ((Get-FileHash $target).Hash -ne (Get-FileHash $setup).Hash) { throw "Kurulum dosyası uygulama EXE'siyle aynı değil: $setup" }

$size = [Math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host ""
Write-Host "Tamamlandı: $target ($size MB)" -ForegroundColor Green
Write-Host "Kurulum dosyası: $setup (aynı EXE; çift tıklayınca kurulum ekranı açılır)" -ForegroundColor Green
if ($signedBy) {
    Write-Host "İmzalı: $signedBy (zaman damgalı, doğrulandı)" -ForegroundColor Green
} else {
    Write-Host "Not: EXE imzasız; UAC 'Yayıncı: Bilinmeyen' gösterir. İmzalamak için: -SignThumbprint <kod imzalama sertifikası parmak izi>" -ForegroundColor Yellow
}

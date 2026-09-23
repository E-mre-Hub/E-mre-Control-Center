# RTX Windows Updater

Windows 11 + NVIDIA RTX bilgisayarlar için güncelleme ve bakım merkezi. Winget, Windows Update, Microsoft Store,
NVIDIA sürücüsü ve Microsoft Defender güncellemelerini **gerçek sistem verileriyle** kontrol eder,
yalnızca kullanıcı onayıyla kurar, (onaylanırsa) Çöp Kutusu'nu boşaltır ve Windows'un kendi bakım araçlarını
(SFC, DISM CheckHealth, MRT hızlı tarama) çalıştırır.

- **İki çalışma şekli:** "Tümünü Kontrol Et / Tümünü Güncelle" veya kartları seçerek "Seçilenleri Kontrol Et /
  Seçilenleri Güncelle-Çalıştır". Seçilmeyen karta hiçbir şekilde dokunulmaz.
- **Sistem İşlemleri:** 9 kartın tamamı tek kategoride ve aynı yapıda: açıklama, gerçek durum, "?" bilgi kutusu,
  seçim kutusu ve kartın kendi işlem butonu ("Kontrol Et", "Tarama Başlat", "Kontrolü Başlat", "Hızlı Taramayı Başlat").

- Teknoloji: C# / .NET 8 / WPF, MVVM
- Çıktı: `RTX Windows Updater.exe`: tek dosya, self-contained (hedef bilgisayarda .NET kurulu olması gerekmez)
- Arayüz: siyah / koyu lacivert, neon mavi vurgular, gölgeli kartlar, animasyonlar, Segoe Fluent ikonları (emoji yok)

> Bu depo **özeldir (private)**. Yalnızca depo sahibinin davet ettiği kişiler erişebilir.
> Lütfen EXE'yi veya kaynak kodu depo dışına paylaşmayın.

---

## Hızlı başlangıç (arkadaşlar için)

1. GitHub'dan gelen davet e-postasını kabul edin (veya https://github.com/Emrefb06 adresindeki depo davetini onaylayın).
2. Deponun **Releases** bölümünden en son `RTX-Windows-Updater-vX.Y.Z.zip` dosyasını indirin.
3. ZIP'i bir klasöre çıkarın ve `RTX Windows Updater.exe` dosyasına çift tıklayın.
4. **Windows SmartScreen uyarısı:** EXE dijital olarak imzalanmadığı için ilk açılışta
   "Windows kişisel bilgisayarınızı korudu" uyarısı çıkabilir. **Ek bilgi → Yine de çalıştır** seçin.
   (Bu, imzasız her uygulamada görülen normal bir uyarıdır; kaynak kodun tamamı bu depodadır.)
5. Uygulama yönetici izni isteyecektir; UAC penceresinde **Evet** deyin.

Gereksinimler: Windows 11 (derleme 22000+), NVIDIA GeForce RTX ekran kartı, internet bağlantısı, winget (Windows 11'de hazır gelir).

---

## 1. Klasör yapısı

```
Desktop\E-mre_App\
├── RTX Windows Updater.exe          ← Son uygulama (yerel derleme çıktısı; depoya konmaz)
├── README.md
├── .gitignore / .gitattributes
├── .github\workflows\release.yml    ← Etiket gönderilince EXE'yi derleyip Releases'a ekler
├── assets\
│   ├── E-mreLogo.jpg                ← Orijinal logo (değiştirilmedi)
│   └── E-mreLogo.ico                ← 16/32/48/64/128/256 px ICO (Create-Icon.ps1 üretir)
├── tools\
│   ├── Create-Icon.ps1              ← JPG → çok boyutlu ICO dönüştürücü
│   └── Build-Exe.ps1                ← Tek komutla EXE üretimi
├── build\publish\                   ← dotnet publish ara çıktısı (depoya konmaz)
└── src\RtxWindowsUpdater\
    ├── RtxWindowsUpdater.csproj     ← Proje, ikon, tek dosya yayın ayarları
    ├── app.manifest                 ← UAC / DPI / Windows 10-11 bildirimi
    ├── App.xaml(.cs)                ← Giriş noktası, global hata yakalama, argümanlar
    ├── Core\
    │   ├── Logger.cs                ← Thread-safe günlük (arayüz + dosya)
    │   ├── ProcessRunner.cs         ← Harici komut çalıştırma: stdout/stderr, zaman aşımı, süreç ağacını sonlandırma
    │   ├── PowerShellRunner.cs      ← PowerShell 5.1 betikleri (##LOG / ##RESULT JSON protokolü)
    │   └── SystemMessages.cs        ← Windows araçlarının mesajlarını sistem dilinde yükler (SFC sonuç tanıma)
    ├── Models\Models.cs             ← ComponentStatus, ModuleResult, UpdateItem, RequirementsResult
    ├── Services\
    │   ├── IUpdateModule.cs         ← Check / Update + IMaintenanceModule (kart butonu) + ilerleme sözleşmeleri
    │   ├── SystemRequirementsChecker.cs
    │   ├── AdminPrivilegeManager.cs
    │   ├── WingetManager.cs         ← + WingetTableParser (dilden bağımsız tablo ayrıştırma)
    │   ├── WindowsUpdateManager.cs
    │   ├── MicrosoftStoreManager.cs
    │   ├── NvidiaDriverManager.cs
    │   ├── DefenderManager.cs
    │   ├── SfcManager.cs            ← sfc /verifyonly (kontrol) ve sfc /scannow (tarama + onarım)
    │   ├── DismManager.cs           ← DISM /Online /Cleanup-Image /CheckHealth (yalnızca kontrol)
    │   ├── MrtManager.cs            ← MRT hızlı tarama (önce yalnızca tespit, temizlik onayla)
    │   ├── RecycleBinManager.cs
    │   ├── SelectedOperationsManager.cs ← Kart seçimlerinin merkezi yönetimi
    │   └── UpdateOrchestrator.cs    ← Güvenli sıra, Tümü / Seçilenler akışları, modül izolasyonu, iptal
    ├── ViewModels\                  ← MainViewModel, DialogViewModel, kart/satır modelleri, komutlar
    ├── Views\                       ← MainWindow.xaml(.cs), Converters.cs
    └── Themes\Theme.xaml            ← Renkler, butonlar, kartlar, animasyonlar, ilerleme çubuğu
```

UI ile sistem işlemleri ayrıdır: `Services` katmanı WPF'e hiç referans vermez. Arayüz yalnızca
`UpdateOrchestrator`'ı `IProgress<T>` ile dinler.

## 2. Bağımlılıklar

| Bağımlılık | Neden |
|---|---|
| .NET 8 SDK (yalnızca derlemek için) | `winget install Microsoft.DotNet.SDK.8` ya da https://dotnet.microsoft.com/download/dotnet/8.0 |
| NuGet: `System.Management` 8.0.0 | WMI (GPU, sürücü, işletim sistemi bilgisi) |
| Windows bileşenleri | winget (App Installer), Windows PowerShell 5.1, Windows Update Agent, Defender cmdlet'leri, nvidia-smi (NVIDIA sürücüsüyle gelir) |

EXE self-contained yayımlandığı için **kullanıcı bilgisayarında .NET kurulu olması gerekmez**.

## 3. Logo / ikon

1. Logo `assets\E-mreLogo.jpg` konumundadır.
2. `tools\Create-Icon.ps1` logoyu **yalnızca yeniden boyutlandırarak** (renk/şekil değişmez) 16, 32, 48, 64, 128, 256 px
   PNG sıkıştırmalı girdiler içeren `assets\E-mreLogo.ico` dosyasına dönüştürür.
   Not: Orijinal JPG 176×176 px olduğu için 256 px boyut büyütülerek üretilir.
3. `.csproj` içindeki `<ApplicationIcon>..\..\assets\E-mreLogo.ico</ApplicationIcon>` ikonu EXE'ye gömer
   (Explorer, masaüstü kısayolu).
4. Aynı ICO ve JPG, WPF kaynağı olarak gömülür: pencere/görev çubuğu ikonu (`Window.Icon`) ve başlık logosu.

## 4. Derleme ve EXE üretimi

Tek komut (E-mre_App klasöründe):

```bash
powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1
```

Betik şunları yapar: .NET 8 SDK'yı bulur, ICO yoksa üretir, aşağıdaki komutu çalıştırır ve EXE'yi
`E-mre_App\RTX Windows Updater.exe` konumuna kopyalar.

Elle:

```bash
dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release
```

```bash
dotnet publish src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o build\publish
```

Kaynaktan derlemek için (depoya erişimi olan herkes):

```bash
git clone https://github.com/Emrefb06/RTX-Windows-Updater.git E-mre_App
```

Ardından `E-mre_App` klasöründe `tools\Build-Exe.ps1` çalıştırılır.

### Yeni sürüm yayınlama (depo sahibi)

1. `src\RtxWindowsUpdater\RtxWindowsUpdater.csproj` içindeki `<Version>` değerini artırın (ör. `1.1.0`).
2. Değişiklikleri commit'leyip gönderin, ardından etiket oluşturun:

```bash
git tag v1.1.0
```

```bash
git push origin v1.1.0
```

3. GitHub Actions (`.github/workflows/release.yml`) EXE'yi Windows sunucusunda derler ve
   `RTX-Windows-Updater-v1.1.0.zip` olarak **Releases** sayfasına ekler. Davetli arkadaşlar oradan indirir.

Not: Özel depolarda GitHub Actions ücretsiz planda aylık 2.000 dakika ile sınırlıdır (Windows dakikaları 2 kat sayılır);
bir derleme yaklaşık 3-5 dakika sürer.

### Arkadaş ekleme (depo sahibi)

GitHub'da depo sayfası → **Settings → Collaborators → Add people** → arkadaşınızın GitHub kullanıcı adı.
Davet edilen kişi daveti kabul ettikten sonra depoyu ve Releases'ı görebilir. Erişimi aynı sayfadan kaldırabilirsiniz.

## 5. Yönetici izni (UAC) nasıl çalışır

- `app.manifest` → `asInvoker`. Uygulama normal kullanıcı olarak açılır, gereksinimleri gösterir.
- Windows 11 + RTX uygunsa ve yönetici değilse, uygulama **neden gerektiğini açıklayan** bir pencere gösterir.
  Kullanıcı onaylarsa `AdminPrivilegeManager` kendini `Verb = "runas"` ile yeniden başlatır → Windows'un
  "Bu uygulamanın cihazınızda değişiklik yapmasına izin veriyor musunuz?" UAC penceresi açılır.
- Aynı anda yalnızca bir örnek çalışır (tek örnek kilidi); iki pencerenin aynı anda güncelleme yapması engellenir.
- UAC reddedilirse (Win32 hata 1223) uygulama kapanmaz: "Yönetici izni reddedildi" gösterilir, hiçbir değişiklik yapılmaz.
  "Tümünü Kontrol Et" ve diğer işlem butonları yönetici değilken tekrar izin ister.
- `requireAdministrator` bilinçli olarak kullanılmadı: red durumunda Windows uygulamayı hiç açmaz ve kullanıcıya
  açıklama gösterilemezdi. UAC hiçbir şekilde atlatılmaz.

## 6. Entegrasyonlar

| Bileşen | Kontrol | Güncelleme |
|---|---|---|
| **Winget** | `winget upgrade --source winget` + `winget list --source winget` (güncel paketler). Metin tablosu başlık konumlarından ayrıştırılır (Türkçe/İngilizce çıktıda çalışır). | Her paket tek tek: `winget upgrade --id <Id> --exact --silent --accept-package-agreements`. Çıkış kodları (yeniden başlatma gerekli, uygulama açık, disk dolu…) çözümlenir. "Açık hedefleme gerekli" (sabitlenmiş) paketler listelenir ama otomatik güncellenmez. |
| **Windows Update** | Resmi Windows Update Agent COM API'si: `Microsoft.Update.Session` → `UpdateSearcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0")`. Servis devre dışıysa yalnızca raporlanır (ayar değiştirilmez). Bekleyen yeniden başlatma `Microsoft.Update.SystemInfo` ile okunur. | Aynı güncellemeler yeniden doğrulanır → `UpdateDownloader` → `UpdateInstaller`. Güncelleme bazında sonuç kodu ve HRESULT gösterilir. **Otomatik yeniden başlatma yok.** |
| **Microsoft Store** | Store uygulamasının varlığı (`Get-AppxPackage`) + `winget upgrade --source msstore`. | Paketler winget ile tek tek güncellenir, ardından Store'un kendi taraması `MDM_EnterpriseModernAppManagement_AppManagement01.UpdateScanMethod` ile tetiklenir. |
| **NVIDIA** | GPU: WMI. Kurulu sürücü: `nvidia-smi` (yoksa WMI sürümünden hesap). NVIDIA App: kayıt defterinden tespit (bilgi). En son sürücü: nvidia.com sürücü sayfasının kullandığı resmi NVIDIA servisleri (`lookupValueSearch.aspx` + `AjaxDriverService DriverManualLookup`, WHQL, DCH, Windows 11). | Yalnızca `https://*.download.nvidia.com` adresinden indirilir, disk alanı kontrol edilir, **Authenticode imzası doğrulanır (NVIDIA Corporation olmalı)**, `-s -noreboot` ile kurulur, ardından sürüm `nvidia-smi` ile yeniden okunarak doğrulanır. |
| **Defender** | `Get-MpComputerStatus` + Microsoft'un resmi Defender sürüm servisi (`microsoft.com/security/encyclopedia/adlpackages.aspx?action=info&arch=x64`) ile gerçek karşılaştırma. | `Update-MpSignature` (başarısızsa MicrosoftUpdateServer, ardından MMPC kaynağı). Sürüm yeniden okunarak doğrulanır. Defender ayarlarına dokunulmaz. |
| **Çöp Kutusu** | `SHQueryRecycleBin` (tüm sürücüler). | Yalnızca onayla `SHEmptyRecycleBin`; sonra yeniden sayılarak doğrulanır. |
| **SFC** | `sfc /verifyonly` – yalnızca tarar, **onarım yapmaz**. Çıktı (UTF-16), `sfc.exe`'nin kendi mesaj tablosundan Windows dilinde yüklenen gerçek mesajlarla eşleştirilir; ilerleme yüzdesi canlı gösterilir. | Kartın "Tarama Başlat" butonu: `sfc /scannow` (onaydan sonra). Toplu güncellemede `sfc /scannow` yalnızca doğrulama bozuk dosya bulduysa çalışır. Onarım yarıda kesilmez. |
| **DISM** | `DISM /Online /Cleanup-Image /CheckHealth` (çıktının dilden bağımsız okunması için DISM'in `/English` görüntüleme seçeneğiyle). Sonuç: Sağlıklı / Onarılabilir / Onarılamaz / Hata. | **Yok.** `/RestoreHealth` veya başka bir onarım komutu uygulama tarafından asla çalıştırılmaz; "Onarılabilir" durumunda yalnızca bilgi verilir. |
| **MRT** | `MRT.exe /Q /N` – sessiz **hızlı tarama, yalnızca tespit** (dosyalara dokunulmaz). Sonuç, `%windir%\debug\mrt.log` dosyasına bu tarama için eklenen "Results Summary" ve "Return code" satırlarından okunur (KB891716 dönüş kodları). Tam tarama asla başlatılmaz. | Tehdit tespit edildiyse ve kullanıcı onaylarsa `MRT.exe /Q` (hızlı tarama + temizleme). |

Güvenli işlem sırası: Winget → Windows Update → Microsoft Store → NVIDIA → Defender → SFC → DISM → MRT → Çöp Kutusu.

Her modül bağımsızdır; biri başarısız olursa diğerleri devam eder. Durumlar: Güncel / Sağlıklı, Güncelleme mevcut,
Güncellendi, Kısmen güncellendi, Dikkat (ör. DISM "onarılabilir"), Yeniden başlatma gerekli, Yönetici izni gerekli,
Kontrol edilemedi, Başarısız, Atlandı.
Hiçbir hata başarılı gibi gösterilmez; nedeni kartta, sonuç ekranında ve günlükte yazar.

## 7. Kullanım

1. `Desktop\E-mre_App\RTX Windows Updater.exe` dosyasına çift tıklayın.
2. Sistem gereksinimleri (Windows 11, NVIDIA RTX, yönetici) kontrol edilir. Uygun değilse devam edilemez.
3. Yönetici izni penceresinde "Yönetici olarak başlat" → UAC'de "Evet".
4. "Gereksinimleri kabul ediyorum" → "Devam Et".
5. **Tümü:** "Tümünü Kontrol Et" tüm kartları kontrol eder (seçimlere bakılmaz). SFC doğrulaması ve MRT hızlı taraması
   birkaç dakika ile yarım saat arasında sürebilir.
6. **Seçilenler:** Kartların sağ üstündeki seçim kutularını işaretleyin (seçili kart neon çerçeveyle gösterilir, üstte
   "N işlem seçildi" yazar, "Seçimi Temizle" tüm seçimleri kaldırır). "Seçilenleri Kontrol Et" yalnızca seçilen kartları
   kontrol eder. Hiç seçim yoksa "Lütfen en az bir işlem seçin." uyarısı çıkar ve hiçbir şey çalışmaz.
7. Sonuçlar kartlarda, "Bulunan Güncellemeler" tablosunda (program, mevcut sürüm, yeni sürüm, durum) ve "İşlem Günlüğü"nde görünür.
8. "Tümünü Güncelle (ve Temizle)" → işlem gerektiren tüm kartlar; "Seçilenleri Güncelle / Çalıştır" → yalnızca seçilen
   kartlardan işlem gerektirenler. Seçili ama henüz kontrol edilmemiş kartlar önce gerçekten kontrol edilir; güncel /
   sağlıklı olanlar çalıştırılmaz. Her iki durumda da yapılacak işlemleri listeleyen onay penceresi çıkar.
   "Tümünü Güncelle"de çöp kutusu, Çöp Kutusu kartındaki "Tümünü Güncelle ile birlikte boşalt" kutusuna bağlıdır;
   "Seçilenleri Çalıştır"da ise Çöp Kutusu kartı seçildiyse boşaltılır.
9. **Her kart kendi butonuyla tek başına da çalıştırılabilir** (tüm kartlar "Sistem İşlemleri" başlığı altındadır):
   - Windows Update, Winget, Microsoft Store, NVIDIA Driver, Microsoft Defender, Çöp Kutusu → "Kontrol Et":
     yalnızca o kartı gerçekten kontrol eder; güncelleme / temizlenecek öğe bulunursa uygulamak için ayrıca onay sorulur.
   - "Tarama Başlat" (sfc /scannow, onaydan sonra), "Kontrolü Başlat" (DISM CheckHealth),
     "Hızlı Taramayı Başlat" (MRT, yalnızca tespit; tehdit bulunursa temizlik ayrıca sorulur).
   - Her karttaki "?" butonu, kartın ne yaptığını anlatan kısa bir bilgi kutusu açar.
   - İşlem sırasında kartta "Kontrol ediliyor…", "Tarama devam ediyor…", "Güncelleniyor…" gibi durum ve canlı ilerleme görünür;
     henüz çalıştırılmamış kartlarda "Henüz çalıştırılmadı" yazar.
10. "İşlem Tamamlandı" ekranı her bileşenin gerçek sonucunu gösterir. Yeniden başlatma gerekiyorsa
   "Yeniden başlat" butonu çıkar; onaylarsanız 60 saniye sonra yeniden başlar (`shutdown /a` ile iptal edilebilir).

Günlük dosyaları: `%LOCALAPPDATA%\RTX Windows Updater\Logs\` (arayüzde "Log dosyası" butonu).

## 8. Bilinen sınırlamalar

- **NVIDIA App'in herkese açık bir API/komut satırı arayüzü yoktur.** Bu nedenle kontrol NVIDIA'nın resmi sürücü
  servisiyle yapılır; NVIDIA App yalnızca "kurulu / bulunamadı" olarak raporlanır.
- NVIDIA sessiz kurulum parametreleri (`-s -noreboot`) NVIDIA tarafından resmi olarak belgelenmemiştir.
  Bu yüzden kurulum sonrası sürüm her zaman yeniden okunur; yeni sürüm etkin değilse başarılı gösterilmez.
- Microsoft Store: winget'in msstore kataloğuyla eşleşmeyen bazı yerleşik uygulamaları yalnızca Store'un kendi
  tarayıcısı görebilir; bu uygulamalar tarama tetiklendikten sonra Store tarafından arka planda güncellenir.
- Windows Update, sürücü ve "isteğe bağlı" (BrowseOnly) güncellemeleri kapsamaz. Bunlar Windows Ayarları'ndan kurulabilir.
- SFC kontrol aşamasında `sfc /verifyonly` kullanılır; "Tümünü / Seçilenleri Kontrol Et" hiçbir sistem dosyasını onarmaz.
  Onarım yalnızca "Tarama Başlat" veya güncelleme butonlarıyla, onaydan sonra `sfc /scannow` ile yapılır.
- DISM "onarılabilir" veya SFC "bazı dosyalar onarılamadı" derse gereken `DISM /RestoreHealth` bilinçli olarak otomatik
  çalıştırılmaz; kullanıcı isterse yönetici komut isteminden kendisi çalıştırmalıdır.
- MRT, Windows Update ile aylık dağıtılan bir araçtır (KB890830). Sistemde yoksa MRT kartı "Tarama başarısız – MRT.exe bulunamadı" gösterir.
- Çöp Kutusu, UAC'yi onaylayan kullanıcı hesabının çöp kutusudur (standart kullanıcı + başka bir yönetici parolası
  kullanılırsa o yönetici hesabının çöp kutusu olur).

## Sürüm geçmişi

### v1.1.0
- Yeni sistem bakım kartları: Windows Sistem Dosyası Kontrolü (SFC /SCANNOW), Windows Image Sağlık Kontrolü
  (DISM /CheckHealth), Microsoft Kötü Amaçlı Yazılım Temizleme Aracı (MRT hızlı tarama). Canlı ilerleme ve gerçek sonuçlar.
- Seçilebilir kartlar: "Seçilenleri Kontrol Et", "Seçilenleri Güncelle / Çalıştır", "N işlem seçildi", "Seçimi Temizle".
- Tüm kartlar tek "Sistem İşlemleri" kategorisinde ve aynı kart yapısında; her kartta "?" bilgi kutusu,
  anlaşılır açıklama ve kendi işlem butonu ("Kontrol Et" ile tek kart kontrolü + onaylı uygulama).
- Daha anlaşılır durum metinleri: "Henüz çalıştırılmadı", "Kontrol ediliyor…", "Tarama devam ediyor…", "Güncelleniyor…".
- "Güncelleme Kontrolünü Başlat" butonu "Tümünü Kontrol Et" olarak adlandırıldı (davranışı aynı, artık bakım kartlarını da kapsar).
- Kartlar ile işlem günlüğü arasındaki alan sürüklenerek boyutlandırılabilir.

### v1.0.0
- İlk sürüm: Winget, Windows Update, Microsoft Store, NVIDIA sürücüsü, Microsoft Defender, Çöp Kutusu.
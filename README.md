# E-mre Hub

> v1.3.0 ile uygulamanın adı **E-mre Hub** oldu (önceki adı: RTX Windows Updater).

Windows 11 bilgisayarlar için güncelleme ve bakım merkezi (NVIDIA RTX ekran kartı için sürücü desteğiyle; RTX yoksa
"kartsız mod" ile kullanılır). Winget, Windows Update, Microsoft Store,
NVIDIA sürücüsü ve Microsoft Defender güncellemelerini **gerçek sistem verileriyle** kontrol eder,
yalnızca kullanıcı onayıyla kurar, Windows geçici dosyalarını ve (onaylanırsa) Çöp Kutusu'nu temizler ve Windows'un
kendi bakım araçlarını (SFC, DISM CheckHealth / onayla RestoreHealth, MRT hızlı tarama) çalıştırır.

- **İki çalışma şekli:** "Tümünü Kontrol Et / Tümünü Güncelle" veya kartları seçerek "Seçilenleri Kontrol Et /
  Seçilenleri Güncelle-Çalıştır". Seçilmeyen karta hiçbir şekilde dokunulmaz. Güncelleme butonları ancak gerçek bir
  kontrol işlem gerektiren bir sonuç bulduğunda etkinleşir.
- **Sistem İşlemleri:** 10 kartın tamamı tek kategoride ve aynı yapıda: açıklama, gerçek durum, "?" bilgi kutusu,
  seçim kutusu ve kartın kendi işlem butonu ("Kontrol Et", "Tarama Başlat", "Kontrolü Başlat", "Hızlı Taramayı Başlat").
- **Şeffaflık:** Sistem Sağlık Özeti, Sistem Bilgileri, her kartta son çalıştırılma zamanı, Detaylı Sonuç paneli
  (gerçek komut, çıkış kodu, stdout/stderr, süre), Son İşlem özeti, işlem geçmişi, Windows bildirimleri ve Log Yönetimi.

- Teknoloji: C# / .NET 8 / WPF, MVVM
- Çıktı: `E-mre Hub.exe`: tek dosya, self-contained (hedef bilgisayarda .NET kurulu olması gerekmez)
- Arayüz: siyah / koyu lacivert, neon mavi vurgular, gölgeli kartlar, animasyonlar, Segoe Fluent ikonları (emoji yok)

> Bu depo **özeldir (private)**. Yalnızca depo sahibinin davet ettiği kişiler erişebilir.
> Lütfen EXE'yi veya kaynak kodu depo dışına paylaşmayın.

---

## Hızlı başlangıç (arkadaşlar için)

1. GitHub'dan gelen davet e-postasını kabul edin (veya https://github.com/Emrefb06 adresindeki depo davetini onaylayın).
2. Deponun **Releases** bölümünden en son `E-mre-Hub-vX.Y.Z.zip` dosyasını indirin (v1.2.0 ve öncesinin paketleri
   `RTX-Windows-Updater-vX.Y.Z.zip` adını taşır).
3. ZIP'i bir klasöre çıkarın ve `E-mre Hub.exe` dosyasına çift tıklayın.
4. **Windows SmartScreen uyarısı:** EXE dijital olarak imzalanmadığı için ilk açılışta
   "Windows kişisel bilgisayarınızı korudu" uyarısı çıkabilir. **Ek bilgi → Yine de çalıştır** seçin.
   (Bu, imzasız her uygulamada görülen normal bir uyarıdır; kaynak kodun tamamı bu depodadır.)
5. Uygulama yönetici izni isteyecektir; UAC penceresinde **Evet** deyin. (İzin vermezseniz uygulama açılır ama tüm kartlar
   kullanım dışı olur; hiçbir kontrol veya güncelleme yapılamaz.)

Gereksinimler: Windows 11 (derleme 22000+, zorunlu), internet bağlantısı, winget (Windows 11'de hazır gelir).
NVIDIA GeForce RTX ekran kartı önerilir. Kart yoksa, başka marka bir kart ya da RTX serisi olmayan bir NVIDIA kartı varsa
uygulama **"Kartsız Devam Et"** ile kullanılabilir; yalnızca NVIDIA Driver kartı kullanım dışı olur (bkz. [Kartsız mod](#kartsız-mod-nvidia-rtx-yoksa)).

---

## 1. Klasör yapısı

```
Desktop\E-mre_Hub\                   ← Proje klasörü (v1.3.0 öncesi adı: E-mre_App)
├── E-mre Hub.exe                    ← Son uygulama (yerel derleme çıktısı; depoya konmaz)
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
    ├── RtxWindowsUpdater.csproj     ← Proje, ikon, tek dosya yayın ayarları (iç proje adı eski addan kaldı; EXE adı AssemblyName'den)
    ├── app.manifest                 ← UAC / DPI / Windows 10-11 bildirimi
    ├── App.xaml(.cs)                ← Giriş noktası, global hata yakalama, argümanlar
    ├── Core\
    │   ├── AppInfo.cs               ← Uygulama adı (E-mre Hub), veri klasörü, sürüm
    │   ├── Logger.cs                ← Thread-safe günlük (arayüz + dosya)
    │   ├── ProcessRunner.cs         ← Harici komut çalıştırma: stdout/stderr, zaman aşımı, süreç ağacını sonlandırma
    │   ├── CmdCommand.cs            ← Güvenli cmd.exe komut satırı (tırnaklama + tehlikeli karakter reddi)
    │   ├── RestartManager.cs        ← Windows Restart Manager API (dosyaları kullanan işlemlerin tespiti)
    │   ├── PowerShellRunner.cs      ← PowerShell 5.1 betikleri (##LOG / ##RESULT JSON protokolü)
    │   ├── SystemMessages.cs        ← Windows araçlarının mesajlarını sistem dilinde yükler (SFC sonuç tanıma)
    │   └── ExecutionTrace.cs        ← İşlem sırasında çalışan komutların gerçek stdout/stderr/çıkış kodu kaydı
    ├── Models\Models.cs             ← ComponentStatus, ModuleResult, UpdateItem, RequirementsResult
    ├── Services\
    │   ├── IUpdateModule.cs         ← Check / Update + bakım butonu, çalışan uygulamayı kapatıp yeniden deneme, manuel güncelleme ve ilerleme sözleşmeleri
    │   ├── SystemRequirementsChecker.cs
    │   ├── AdminPrivilegeManager.cs
    │   ├── WingetManager.cs         ← + WingetTableParser (dilden bağımsız tablo ayrıştırma)
    │   ├── WindowsUpdateManager.cs
    │   ├── MicrosoftStoreManager.cs
    │   ├── NvidiaDriverManager.cs
    │   ├── DefenderManager.cs
    │   ├── SfcManager.cs            ← sfc /verifyonly (kontrol) ve sfc /scannow (tarama + onarım)
    │   ├── DismManager.cs           ← DISM /CheckHealth (kontrol) + onayla /RestoreHealth ve CheckHealth ile doğrulama
    │   ├── RunningAppManager.cs     ← Güncellemeyi engelleyen çalışan uygulamalar (Restart Manager) + onaylı kapatma
    │   ├── MrtManager.cs            ← MRT hızlı tarama (önce yalnızca tespit, temizlik onayla)
    │   ├── TemporaryFilesManager.cs ← Windows Geçici Dosyalar (ölçüm + kategori bazlı güvenli temizlik)
    │   ├── RecycleBinManager.cs
    │   ├── SelectedOperationsManager.cs ← Kart seçimlerinin merkezi yönetimi
    │   ├── SystemInfoService.cs     ← Sistem Bilgileri (WMI, kayıt defteri, nvidia-smi, DriveInfo)
    │   ├── AppStateStore.cs         ← İşlem geçmişi / son sonuçlar / ayarlar (%LOCALAPPDATA%\…\state.json)
    │   ├── NotificationService.cs   ← Windows 11 bildirimleri (toast)
    │   └── UpdateOrchestrator.cs    ← Güvenli sıra, Tümü / Seçilenler akışları, modül izolasyonu, iptal
    ├── ViewModels\                  ← MainViewModel, DialogViewModel, DetailViewModel, ThrottledProgress, kart/satır modelleri, komutlar
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
| Windows bileşenleri | winget (App Installer), Windows PowerShell 5.1, Windows Update Agent, Defender ve Teslim En İyileştirme cmdlet'leri, SFC / DISM / MRT, Windows Restart Manager, nvidia-smi (NVIDIA sürücüsüyle gelir) |

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

Tek komut (proje klasöründe, ör. `E-mre_Hub`):

```bash
powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1
```

Betik şunları yapar: .NET 8 SDK'yı bulur, ICO yoksa üretir, aşağıdaki komutu çalıştırır ve EXE'yi
proje klasörüne `E-mre Hub.exe` olarak kopyalar (klasörün adı önemli değildir).

Elle:

```bash
dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release
```

```bash
dotnet publish src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o build\publish
```

Kaynaktan derlemek için (depoya erişimi olan herkes):

```bash
git clone https://github.com/Emrefb06/RTX-Windows-Updater.git E-mre_Hub
```

Ardından `E-mre_Hub` klasöründe `tools\Build-Exe.ps1` çalıştırılır. (GitHub'daki depo adı `RTX-Windows-Updater` olarak kaldı;
uygulamanın adı E-mre Hub'dır. Depo adı GitHub'da Settings → Repository name ile değiştirilirse eski adres otomatik yönlenir.)

### Yeni sürüm yayınlama (depo sahibi)

1. `src\RtxWindowsUpdater\RtxWindowsUpdater.csproj` içindeki `<Version>` değerini artırın (ör. `1.3.0`).
2. Değişiklikleri commit'leyip gönderin, ardından etiket oluşturun:

```bash
git tag v1.3.0
```

```bash
git push origin v1.3.0
```

3. GitHub Actions (`.github/workflows/release.yml`) EXE'yi Windows sunucusunda derler ve
   `E-mre-Hub-v1.3.0.zip` olarak **Releases** sayfasına ekler. Davetli arkadaşlar oradan indirir.
   Etiketteki sürüm (v1.3.0) EXE'nin sürümü olarak kullanılır; csproj'daki `<Version>` ile aynı olmalıdır.

Not: Özel depolarda GitHub Actions ücretsiz planda aylık 2.000 dakika ile sınırlıdır (Windows dakikaları 2 kat sayılır);
bir derleme yaklaşık 3-5 dakika sürer.

### Arkadaş ekleme (depo sahibi)

GitHub'da depo sayfası → **Settings → Collaborators → Add people** → arkadaşınızın GitHub kullanıcı adı.
Davet edilen kişi daveti kabul ettikten sonra depoyu ve Releases'ı görebilir. Erişimi aynı sayfadan kaldırabilirsiniz.

## 5. Yönetici izni (UAC) nasıl çalışır

- `app.manifest` → `asInvoker`. Uygulama normal kullanıcı olarak açılır, gereksinimleri gösterir.
- Windows 11 uygunsa (RTX olsun olmasın) ve yönetici değilse, uygulama **neden gerektiğini açıklayan** bir pencere gösterir.
  Kullanıcı onaylarsa `AdminPrivilegeManager` kendini `Verb = "runas"` ile yeniden başlatır → Windows'un
  "Bu uygulamanın cihazınızda değişiklik yapmasına izin veriyor musunuz?" UAC penceresi açılır.
- Aynı anda yalnızca bir örnek çalışır (tek örnek kilidi); iki pencerenin aynı anda güncelleme yapması engellenir.
- UAC reddedilirse (Win32 hata 1223) uygulama kapanmaz: "Yönetici izni reddedildi" gösterilir, hiçbir değişiklik yapılmaz.
- **Yönetici yetkisi olmadan uygulamaya girilebilir ama 10 kartın tamamı "Kullanım dışı" olur** (kart butonları, seçim kutuları,
  "Tümünü / Seçilenleri Kontrol Et" ve güncelleme butonları kapalı). İşlemler panelinde bir uyarı ve **"Yönetici olarak yeniden başlat"**
  butonu çıkar; uygulama UAC onayıyla yönetici olarak yeniden açılır ve doğrudan ana ekrana döner.
- `requireAdministrator` bilinçli olarak kullanılmadı: red durumunda Windows uygulamayı hiç açmaz ve kullanıcıya
  açıklama gösterilemezdi. UAC hiçbir şekilde atlatılmaz.

### Komutlar nasıl çalıştırılır (cmd.exe)

- Sistem araçları (winget, sfc, DISM, MRT, nvidia-smi, NVIDIA kurulum programı, shutdown) `cmd.exe /d /s /c "..."` ile,
  pencere açılmadan çalıştırılır. cmd.exe, uygulamanın **zaten sahip olduğu** yetkiyle çalışır; gizli yükseltme yapmaz,
  UAC'yi atlatmaz. Yönetici yetkisi yalnızca yukarıdaki `runas` + UAC onayıyla alınır.
- Komut satırı `CmdCommand` ile güvenli kurulur: exe yolu her zaman tırnaklanır, boşluk/parantez içeren argümanlar
  tırnaklanır; `" % ! ^ & | < >` veya satır sonu içeren argümanlar **reddedilir** (komut enjeksiyonu yapılamaz).
  Winget paket kimlikleri ayrıca `^[A-Za-z0-9][A-Za-z0-9.\-_+]*$` kuralıyla doğrulanır.
- stdout / stderr / çıkış kodu / zaman aşımı / iptal davranışı aynen korunur; zaman aşımında cmd.exe ile birlikte tüm
  süreç ağacı sonlandırılır. Zaman aşımı, boş çıktı veya yalnızca "süreç başladı" olması hiçbir zaman başarı sayılmaz.
- Pencereli araçlar (MRT, NVIDIA kurulumu) `start "" /wait` ile beklenir; çıkış kodları gerçek değerleriyle okunur.

### Güncellemeyi engelleyen çalışan uygulamalar

- Winget / Microsoft Store güncellemesi "uygulama çalışıyor / dosyalar kullanımda" (`0x8A150101`, `0x8A150103`, `0x8A150111`)
  nedeniyle başarısız olursa, paketin kurulum klasörü Programlar ve Özellikler kaydından bulunur ve klasördeki
  .exe/.dll dosyalarını kullanan GERÇEK işlemler **Windows Restart Manager** API'siyle tespit edilir (kurulum
  programlarının kullandığı mekanizma). Tahmin yapılmaz.
- Bulunan işlemler onay penceresinde listelenir. Kullanıcı **"Kapat ve tekrar dene"** derse: önce uygulamaya normal kapatma
  isteği gönderilir, 10 saniye içinde kapanmazsa Görev Yöneticisi'ndeki "Görevi sonlandır" gibi sonlandırılır; ardından
  dosyaları hâlâ kullanan işlem kalıp kalmadığı yeniden denetlenir ve güncelleme yeniden denenir. Sonuç yine winget'in
  gerçek kodu ve doğrulama sorgusuyla belirlenir. Onay verilmezse hiçbir işlem kapatılmaz.
- Asla kapatılmayanlar: Windows hizmetleri, Windows Gezgini, kritik sistem işlemleri, Windows klasöründeki işlemler, başka
  bir kullanıcı oturumundaki işlemler ve bu uygulamanın kendisi. Kapatmadan önce PID + başlangıç zamanı doğrulanır.
  Program Files kökü, Windows klasörü, kullanıcı klasörü gibi geniş klasörler kurulum klasörü olarak kabul edilmez.
- Kapatılan uygulamalar otomatik olarak yeniden açılmaz.
- Bazı kurulum programları (ör. Discord/Squirrel) uygulama açıkken dosyaları değiştiremez ama yine de 0 (başarılı) döndürür.
  Uygulama bu "başarıyı" kabul etmez: doğrulama sorgusunda güncelleme hâlâ görünüyorsa paket "Doğrulanamadı" olur; paketin
  dosyalarını kullanan çalışan işlemler varsa bunlar tespit edilir ve aynı "Kapat ve tekrar dene" seçeneği sunulur.
- Winget'in 0x8A15008E döndürdüğü paket/sürüm çiftleri `state.json`'da saklanır; uygulama yeniden açıldığında aynı sürüm
  tekrar boşuna otomatik denenmez (yeni sürüm çıkarsa yeniden denenir).

### Otomatik uygulanmayan (manuel) güncellemeler

Winget bazı güncellemeleri otomatik uygulamaz; bunlar güncelleme sayısına ve "Tümünü Güncelle"ye katılmaz, kartta
"Manuel" olarak ayrı sayılır. Kullanıcı isterse **"Otomatik uygulanmayan güncellemeler"** penceresinden (varsayılan olarak
hiçbiri seçili değil) tek tek seçerek uygular:

| Tür | Neden | Uygulamanın yaptığı |
|---|---|---|
| **Açık hedefleme gerekli** (ör. Discord) | Manifest, uygulamanın kendini güncellediğini belirtir (ya da paket sabitlenmiştir); `winget upgrade --all` atlar. | Yalnızca bu paketi hedefleyen `winget upgrade --id <Id> --exact`. (Alternatif: uygulamayı açmak – kendini günceller.) |
| **Kurulum teknolojisi farklı** `0x8A15008E` (ör. Epic Online Services: kurulu sürüm MSI, yeni sürüm EXE) | Winget, kurulum türü değişen paketi yerinde yükseltemez. | Winget'in önerisi: önce `winget uninstall --id <Id> --exact`, başarılıysa `winget install --id <Id> --exact`. Kaldırma başarısızsa kurulum hiç denenmez ve hiçbir şey değişmez; kurulum başarısızsa paketin kurulu olmadan kaldığı ve yeniden kurma komutu açıkça yazılır. |

Sonuç her durumda gerçek winget sorgusuyla doğrulanır. Pencere, winget / Store kartının "Kontrol Et" butonundan her
zaman; "Tümünü Güncelle" / "Seçilenleri Güncelle" sonunda ve (otomatik güncelleme yoksa) kontrol sonunda bir kez sunulur.

## 6. Entegrasyonlar

| Bileşen | Kontrol | Güncelleme |
|---|---|---|
| **Winget** | `winget upgrade --source winget` + `winget list --source winget` (güncel paketler). Metin tablosu başlık konumlarından ayrıştırılır (Türkçe/İngilizce çıktıda çalışır). | Kontrolde bulunan liste kullanılır (yeniden liste sorgusu yok); her paket tek tek: `winget upgrade --id <Id> --exact --source <kaynak> --silent --accept-package-agreements`. Sonuç winget'in resmi dönüş kodlarına göre **paket bazında** yorumlanır: paket adı, mevcut → yeni sürüm, Türkçe açıklama, kurulum programının gerçek çıkış kodu (ör. "Installer failed with exit code: 6"), winget kodu + sembolü ve winget'in kendi mesajı. `0x8A15008E` (kurulum teknolojisi farklı) başarısız sayılır; bu paket/sürüm `state.json`'a kaydedilir ve tekrar listelendiğinde (uygulama yeniden açılsa da) otomatik güncellemeye alınmaz, manuel "kaldır + yeniden kur" seçeneği olarak sunulur. Yalnızca `0x8A150109 / 0x8A15010B` "güncellendi – yeniden başlatma gerekli" sayılır. Ardından TEK bir `winget upgrade --source <kaynak>` ile doğrulanır; hâlâ listelenen paket "doğrulanamadı" olur (winget 0 döndürse bile) ve dosyalarını kullanan çalışan uygulama varsa "kapat ve tekrar dene" sunulur. "Açık hedefleme gerekli" (sabitlenmiş) paketler otomatik güncellenmez ve güncelleme sayısına katılmaz; kartta "Otomatik uygulanabilir: N / Manuel: M" olarak ayrı gösterilir. Paket bazlı sonuç (durum, neden, kod + sembol, kurulum programı çıkış kodu, winget mesajı, engelleyen uygulamalar) Detaylı Sonuç panelinde listelenir. Çalışan uygulama nedeniyle başarısız olan paketler onayla yeniden denenebilir (yukarıya bakın). |
| **Windows Update** | Resmi Windows Update Agent COM API'si: `Microsoft.Update.Session` → `UpdateSearcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0")`. Servis devre dışıysa yalnızca raporlanır (ayar değiştirilmez). Bekleyen yeniden başlatma `Microsoft.Update.SystemInfo` ile okunur. | Aynı güncellemeler yeniden doğrulanır → `UpdateDownloader` → `UpdateInstaller`. Güncelleme bazında sonuç kodu ve HRESULT gösterilir. **Otomatik yeniden başlatma yok.** |
| **Microsoft Store** | Store uygulamasının varlığı (`Get-AppxPackage`) + `winget upgrade --source msstore`. | Paketler winget ile tek tek güncellenir, ardından Store'un kendi taraması `MDM_EnterpriseModernAppManagement_AppManagement01.UpdateScanMethod` ile tetiklenir. |
| **NVIDIA** | GPU: WMI. Kurulu sürücü: `nvidia-smi` (yoksa WMI sürümünden hesap). NVIDIA App: kayıt defterinden tespit (bilgi). En son sürücü: nvidia.com sürücü sayfasının kullandığı resmi NVIDIA servisleri (`lookupValueSearch.aspx` + `AjaxDriverService DriverManualLookup`, WHQL, DCH, Windows 11). | Yalnızca `https://*.download.nvidia.com` adresinden indirilir, disk alanı kontrol edilir, **Authenticode imzası doğrulanır (NVIDIA Corporation olmalı)**, `-s -noreboot` ile kurulur, ardından sürüm `nvidia-smi` ile yeniden okunarak doğrulanır. Kurulum dosyası `%ProgramData%\E-mre Hub\Downloads` klasörüne indirilir ve işlem bitince (başarılı ya da değil) silinir. Kartsız modda bu kart hiç çalışmaz. |
| **Defender** | `Get-MpComputerStatus` + Microsoft'un resmi Defender sürüm servisi (`microsoft.com/security/encyclopedia/adlpackages.aspx?action=info&arch=x64`) ile gerçek karşılaştırma. | `Update-MpSignature` (başarısızsa MicrosoftUpdateServer, ardından MMPC kaynağı). Sürüm yeniden okunarak doğrulanır. Defender ayarlarına dokunulmaz. |
| **Windows Geçici Dosyalar** | Ayarlar → Sistem → Depolama → Geçici dosyalar'daki güvenli kategorilerin gerçek konumları ölçülür: Kullanıcı geçici dosyaları (`%TEMP%`, .NET tek dosya çalışma klasörü hariç), Windows geçici dosyaları (`%WINDIR%\Temp`), Teslim En İyileştirme önbelleği (resmi `Get-DeliveryOptimizationStatus` / `Get-DOConfig` + önbellek klasörünün diskteki gerçek boyutu), Windows hata raporlama dosyaları (`WER\ReportArchive`, `ReportQueue`), DirectX gölgelendirici önbelleği (`%LOCALAPPDATA%\D3DSCache`). Her kategori için ayrı ayrı **Ölçülen**, **Temizlenebilir** ve **Korunan** hesaplanır. Temizlenebilir: 24 saatten uzun süredir değişmemiş, sistem/salt okunur olmayan dosyalar; Teslim En İyileştirme'de Windows'un sabitlemediği (IsPinned) ve etkin indirmede olmayan önbellek. Korunan alan (son 24 saat, salt okunur/sistem, sabitlenmiş/etkin önbellek, bir önceki temizlikte Windows'un silmediği önbellek) asla "temizlenebilir" sayılmaz ve kartta "Ek olarak X korunuyor" diye ayrı yazılır. Bağlantı noktaları izlenmez. Okunamayan konum "0 bayt" değil "Okunamadı" olarak raporlanır. Sonuç: "Temizlenebilir: X" veya "Temizlenecek geçici dosya bulunamadı". | Onay penceresinde tahmini alan ve kategori kutuları (varsayılan işaretli, seçili toplam canlı hesaplanır). Yalnızca işaretli kategoriler temizlenir; kullanımdaki dosyalar atlanır, kök klasörler ve son 24 saatte oluşturulmuş klasörler silinmez; Teslim En İyileştirme için Windows'un resmi `Delete-DeliveryOptimizationCache -Force` komutu (sabitlenmiş dosyalar silinmez; `-IncludePinnedFiles` kullanılmaz). Temizlikten sonra TÜM kategoriler dosya sisteminden yeniden ölçülür (silme komutunun kendi bildirimi sonuç sayılmaz): **Önce / Sonra / Temizlenen / Kalan / Kullanımda-atlanan / Korunan**. Kullanımdaki dosyalar kaldıysa sonuç "Kısmen temizlendi" (uyarı) olur. Çöp Kutusu bu hesaba dahil değildir. |
| **Çöp Kutusu** | `SHQueryRecycleBin` (tüm sürücüler). | Yalnızca onayla `SHEmptyRecycleBin`; sonra yeniden sayılarak doğrulanır. |
| **SFC** | `sfc /verifyonly` – yalnızca tarar, **onarım yapmaz**. Çıktı (UTF-16), `sfc.exe`'nin kendi mesaj tablosundan Windows dilinde yüklenen gerçek mesajlarla eşleştirilir; ilerleme yüzdesi canlı gösterilir. | Kartın "Tarama Başlat" butonu: `sfc /scannow` (onaydan sonra). Toplu güncellemede `sfc /scannow` yalnızca doğrulama bozuk dosya bulduysa çalışır. Onarım yarıda kesilmez. |
| **DISM** | `DISM /Online /Cleanup-Image /CheckHealth` (çıktının dilden bağımsız okunması için DISM'in `/English` görüntüleme seçeneğiyle). Sonuç: Sağlıklı / **Dikkat: Onarılabilir durumda** (başarılı sayılmaz, işlem gerektirir) / Onarılamaz / Hata. | Yalnızca kontrol "onarılabilir" dediyse ve kullanıcı onay verdiyse ("Tümünü Güncelle", "Seçilenleri Çalıştır" veya kartın "Onar" onayı): `DISM /Online /Cleanup-Image /RestoreHealth`. DISM'in "başarılı" mesajı doğrudan kabul edilmez; ardından `/CheckHealth` yeniden çalıştırılır ve bileşen deposu gerçekten sağlıklıysa "Onarıldı (doğrulandı)" gösterilir. Hata kodları (ör. 0x800F081F kaynak bulunamadı, 0x800F0906 indirilemedi) açıklamasıyla gösterilir. Onarım başladıktan sonra yarıda kesilmez. |
| **MRT** | `MRT.exe /Q /N` – sessiz **hızlı tarama, yalnızca tespit** (dosyalara dokunulmaz). Sonuç, `%windir%\debug\mrt.log` dosyasına bu tarama için eklenen "Results Summary" ve "Return code" satırlarından okunur (KB891716 dönüş kodları). Tam tarama asla başlatılmaz. | Tehdit tespit edildiyse ve kullanıcı onaylarsa `MRT.exe /Q` (hızlı tarama + temizleme). |

Güvenli işlem sırası: Winget → Windows Update → Microsoft Store → NVIDIA → Defender → DISM → SFC → MRT →
Geçici Dosyalar → Çöp Kutusu. (DISM, SFC'den önce çalışır: Microsoft'un önerdiği gibi önce bileşen deposu, sonra sistem dosyaları.)

Her modül bağımsızdır; biri başarısız olursa diğerleri devam eder. Durumlar: Güncel / Sağlıklı, Güncelleme mevcut,
Güncellendi, Kısmen güncellendi, Dikkat (ör. yalnızca manuel güncellemesi olan winget), Yeniden başlatma gerekli, Yönetici izni gerekli,
Kontrol edilemedi, Başarısız, Atlandı, Kullanım dışı (RTX ekran kartı yoksa NVIDIA Driver; yönetici yetkisi yoksa tüm kartlar).
Hiçbir hata başarılı gibi gösterilmez; nedeni kartta, sonuç ekranında ve günlükte yazar.

## 7. Kullanım

1. `E-mre Hub.exe` dosyasına çift tıklayın (kendi derlemenizde: `Desktop\E-mre_Hub\E-mre Hub.exe`).
2. Sistem gereksinimleri (Windows 11, NVIDIA RTX, yönetici) kontrol edilir. Windows 11 değilse uygulama kullanılamaz
   (devam edilemez). NVIDIA RTX ekran kartı yoksa GPU satırı turuncu uyarı olur ve devam butonu "Kartsız Devam Et" olarak görünür.
3. Yönetici izni penceresinde "Yönetici olarak başlat" → UAC'de "Evet". "Şimdi değil" derseniz de girebilirsiniz ama tüm kartlar
   kullanım dışı olur; sonradan İşlemler panelindeki "Yönetici olarak yeniden başlat" ile yetki verebilirsiniz.
4. "Gereksinimleri kabul ediyorum" → "Devam Et" (RTX ekran kartı yoksa "Kartsız Devam Et").
5. **Tümü:** "Tümünü Kontrol Et" tüm kartları kontrol eder (seçimlere bakılmaz). SFC doğrulaması ve MRT hızlı taraması
   birkaç dakika ile yarım saat arasında sürebilir.
6. **Seçilenler:** Kartların sağ üstündeki seçim kutularını işaretleyin (seçili kart neon çerçeveyle gösterilir, üstte
   "N işlem seçildi" yazar, "Seçimi Temizle" tüm seçimleri kaldırır). "Seçilenleri Kontrol Et" yalnızca seçilen kartları
   kontrol eder. Hiç seçim yoksa "Lütfen en az bir işlem seçin." uyarısı çıkar ve hiçbir şey çalışmaz.
7. Sonuçlar kartlarda, "Bulunan Güncellemeler" tablosunda (program, mevcut sürüm, yeni sürüm, durum) ve "İşlem Günlüğü"nde görünür.
8. "Tümünü Güncelle (ve Temizle)" → işlem gerektiren tüm kartlar; "Seçilenleri Güncelle / Çalıştır" → yalnızca seçilen
   kartlardan işlem gerektirenler. **Bu iki buton, kontrol yapılana kadar devre dışıdır** ve altında
   "Henüz kontrol yapılmadı. Önce "Tümünü Kontrol Et" veya "Seçilenleri Kontrol Et" çalıştırılmalı." yazar.
   Kontrol edilmemiş kart güncellenmez (günlüğe "kontrol edilmediği için atlandı" yazılır); "Kontrol edilemedi" /
   "Başarısız" sonuçlar hiçbir zaman işlem gerektiren veya başarılı sayılmaz; güncel / sağlıklı olanlar çalıştırılmaz.
   Bir işlem uygulandıktan sonra ilgili kartın kontrol sonucu geçersiz sayılır (yeniden kontrol gerekir).
   Her iki durumda da yapılacak işlemleri listeleyen onay penceresi çıkar.
   "Tümünü Güncelle"de çöp kutusu, Çöp Kutusu kartındaki "Tümünü Güncelle ile birlikte boşalt" kutusuna bağlıdır;
   "Seçilenleri Çalıştır"da ise Çöp Kutusu kartı seçildiyse boşaltılır.
9. **Her kart kendi butonuyla tek başına da çalıştırılabilir** (tüm kartlar "Sistem İşlemleri" başlığı altındadır):
   - Windows Update, Winget, Microsoft Store, NVIDIA Driver, Microsoft Defender, Windows Geçici Dosyalar, Çöp Kutusu →
     "Kontrol Et": yalnızca o kartı gerçekten kontrol eder; güncelleme / temizlenecek öğe bulunursa uygulamak için ayrıca
     onay sorulur (Geçici Dosyalar'da tahmini alan ve kategori seçimiyle).
   - "Tarama Başlat" (sfc /scannow, onaydan sonra), "Kontrolü Başlat" (DISM CheckHealth; "onarılabilir" çıkarsa
     RestoreHealth ile onarım ayrıca sorulur), "Hızlı Taramayı Başlat" (MRT, yalnızca tespit; tehdit bulunursa temizlik ayrıca sorulur).
   - Her karttaki "?" butonu, kartın ne yaptığını anlatan kısa bir bilgi kutusu açar.
   - İşlem sırasında kartta "Kontrol ediliyor…", "Tarama devam ediyor…", "Güncelleniyor…" gibi durum ve canlı ilerleme görünür;
     henüz çalıştırılmamış kartlarda "Henüz çalıştırılmadı" yazar.
10. Sol paneldeki **İlerleme** kartı işlem durumunu gösterir: Hazır → Kontrol devam ediyor… / İşlem devam ediyor… →
   İşlem tamamlandı (yeşil tik) / İşlem tamamlandı – hata var (kırmızı) / İşlem iptal edildi (turuncu). Yüzde yalnızca
   modüllerin bildirdiği gerçek adımlardan gelir; dönen ikon ve ilerleme çubuğundaki parıltı yalnızca işlem sürerken
   çalışır ve işlem biter bitmez durdurulur.
   İşlem bittiğinde tek bir durum geçişi yapılır: sonuç kaydı, durum, animasyonun durması, %100, buton durumları ve özet
   aynı anda güncellenir. İlerleme bildirimleri en fazla 100 ms'de bir (yalnızca en son değer) ekrana yansıtılır.
11. Güncellemeden sonra çalışan bir uygulama engel olduysa "Çalışan uygulamalar güncellemeyi engelliyor" penceresi çıkar
   (kapatılacak ve kapatılmayacak işlemler ayrı listelenir). "İşlem Tamamlandı" ekranı her bileşenin gerçek sonucunu gösterir. Yeniden başlatma gerekiyorsa
   "Yeniden başlat" butonu çıkar; onaylarsanız 60 saniye sonra yeniden başlar (`shutdown /a` ile iptal edilebilir).

### Kartsız mod (NVIDIA RTX yoksa)

- **Windows 11 zorunludur.** Windows 11 değilse gereksinim sayfasında "Bu uygulama bu sistem için desteklenmiyor" uyarısı
  çıkar, devam butonları kapalıdır ve uygulamanın hiçbir işlemi kullanılamaz.
- Windows 11 uygun ama ekran kartı bulunamadıysa, başka marka (Intel / AMD) bir kart ya da RTX serisi olmayan bir NVIDIA
  kartı (ör. GTX) varsa uygulama engellenmez: GPU satırı algılanan kartla birlikte turuncu uyarı olarak gösterilir ve
  "Kartsız Devam Et" butonu çıkar ("Gereksinimleri kabul ediyorum" işaretlenmelidir).
- Kartsız modda **NVIDIA Driver kartı "Kullanım dışı"** olur: kartın butonu ve seçim kutusu kapalıdır; "Tümünü Kontrol Et",
  "Tümünü Güncelle" ve seçili işlemler bu kartı hiç çalıştırmaz (günlüğe "bu sistemde kullanım dışı, kontrol edilmedi" yazılır).
  Yönetici yetkisiyle diğer 9 kart normal çalışır (yönetici yetkisi yoksa tüm kartlar kullanım dışıdır, aşağıya bakın).
- Sistem Sağlık Özeti bu kartı "çalıştırılmadı" saymaz, "1 kullanım dışı" olarak ayrıca gösterir; başlık altında
  "Windows 11 · kartsız mod" yazar. Sistem Bilgileri'nde NVIDIA alanları "NVIDIA ekran kartı yok" gösterir
  (RTX olmayan NVIDIA kartında gerçek model ve sürücü sürümü gösterilir).

### Yönetici yetkisi olmadan

- Uygulama normal kullanıcı olarak açılır ve yönetici iznini neden istediğini açıklar. İzin verilmezse ana ekrana yine girilebilir,
  ancak **10 kartın tamamı "Kullanım dışı"** olur (kartta neden: "Yönetici yetkisi yok…"). RTX ekran kartı da yoksa NVIDIA Driver
  kartında iki neden birlikte yazar.
- "Tümünü Kontrol Et", "Seçilenleri Kontrol Et", güncelleme butonları, kart butonları ve seçim kutuları kapalıdır; Sistem Sağlık Özeti
  "Yönetici yetkisi yok – tüm işlemler kullanım dışı" gösterir. Sistem Bilgileri, geçmiş sonuçlar (Detaylı Sonuç) ve günlük yine görüntülenebilir.
- İşlemler panelindeki **"Yönetici olarak yeniden başlat"** uygulamayı UAC onayıyla yönetici olarak yeniden açar. Yetki yalnızca
  bu yolla alınır; UAC hiçbir şekilde atlatılmaz.

Günlük dosyaları: `%LOCALAPPDATA%\E-mre Hub\Logs\` (arayüzde "Log dosyası" butonu). v1.3.0 öncesi günlükler eski
`%LOCALAPPDATA%\RTX Windows Updater\Logs\` klasöründe kalır (silinmez).

### Sistem Sağlık Özeti, Sistem Bilgileri ve işlem geçmişi

- **Sistem Sağlık Özeti** (ana ekranın üstünde) yalnızca kartların bu oturumdaki GERÇEK durumlarından hesaplanır:
  çalıştırılmamış kartlar "Kontrol edilmedi" olarak kalır ve sağlıklı sayılmaz; hata ve bekleyen güncellemeler gizlenmez.
  Genel durum, sorunsuz / güncelleme-uyarı / hata / çalıştırılmadı (varsa kullanım dışı) sayılarıyla birlikte gösterilir.
- **Son İşlem**: her işlem bitince gerçek sonuçlardan hesaplanan kısa özet. Kontrol: "10 kontrol tamamlandı · 3 güncelleme
  bulundu (Winget 2, Microsoft Defender 1) · 1 manuel güncelleme (otomatik uygulanmaz) · 1 uyarı · 0 hata · …".
  Güncelleme: "4 işlem · 2 başarılı · 1 uyarı · 1 hata · Winget: 2 paket güncellenemedi · Süre: 21 sn".
  Tüm işlemler "Son İşlemler" sekmesinde listelenir (son 50).
- **Sistem Bilgileri** (açılır/kapanır): işletim sistemi, Windows sürümü ve build, CPU, RAM, GPU, NVIDIA GPU modeli ve
  sürücü sürümü, mimari, bilgisayar adı, disk, boş/toplam alan, çalışma süresi. Alınamayan alan "Bilgi alınamadı" yazar
  ve nedeni günlüğe düşer (NVIDIA kartı olmayan sistemde NVIDIA alanları okuma hatası değil "NVIDIA ekran kartı yok"
  olarak gösterilir). Değişmeyen bilgiler (işletim sistemi, CPU, RAM, GPU) oturumda bir kez okunur; "Yenile" butonu
  yalnızca sürücü sürümü, disk alanı ve çalışma süresini yeniden okur. Ekran kartı listesi (WMI) gereksinim kontrolü,
  Sistem Bilgileri ve NVIDIA kartı arasında paylaşılır; yönetici yetkisi açılışta bir kez doğrulanır.
- **Son çalıştırılma**: her kartta "Son kontrol / Son tarama / Son güncelleme: tarih saat" (gerçek bitiş zamanı).
  Önceki oturumdan kalan sonuç "önceki oturum" olarak işaretlenir; kartın bu oturumdaki durumu yine "çalıştırılmadı" kalır.
- **Detaylı Sonuç** (kartın liste ikonlu butonu): işlem türü, bitiş zamanı, çalışma süresi, çalıştırılan her komut
  (komut satırı, Exit Code, süre, stdout, stderr), Windows API / NVIDIA / Microsoft servis yanıtları ve öğe tablosu.
  Elde edilmeyen bilgi "Bilgi alınamadı" olarak gösterilir.
- Geçmiş dosyası: `%LOCALAPPDATA%\E-mre Hub\state.json`. Yeni klasörde geçmiş yoksa önceki sürümün
  `%LOCALAPPDATA%\RTX Windows Updater\state.json` dosyası ilk açılışta bir kez kopyalanır (eski dosya silinmez).

### Windows bildirimleri

- Uzun süren işlemler bitince (SFC, MRT, toplu kontrol/güncelleme; 30 sn'yi aşan tek kart işlemleri) Windows bildirimi
  gönderilir. Metin gerçek sonuçlardan hesaplanır.
- Sol paneldeki "İşlem bitince Windows bildirimi göster" kutusuyla veya Windows Ayarları → Sistem → Bildirimler'den kapatılabilir.
- Paketlenmemiş masaüstü uygulamaları için Microsoft'un yöntemiyle, uygulama kimliği yalnızca geçerli kullanıcı için
  `HKCU\Software\Classes\AppUserModelId\E-mre.RTXWindowsUpdater` altına (görünen ad + ikon) kaydedilir.

### Log Yönetimi

- Günlük satırları `[saat] [INFO|SUCCESS|WARNING|ERROR] mesaj` biçimindedir; dosyaya arka planda yazılır (arayüz beklemez).
- Filtreler (Tümü / Bilgi / Başarılı / Uyarı / Hata) yalnızca görünümü değiştirir; kayıtlar silinmez.
- "Logları Temizle" yalnızca ekranı temizler, günlük dosyası korunur. "Dışa Aktar" gerçek oturum günlüğünü seçtiğiniz konuma
  `E-mre-Hub-Log-YYYY-AA-GG.txt` olarak kopyalar. "Log dosyası" dosyayı, "Klasör" günlük klasörünü açar.

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
- `DISM /RestoreHealth` hiçbir zaman otomatik (onaysız) çalışmaz; yalnızca kontrol "onarılabilir" dediğinde ve kullanıcı
  onay verdiğinde çalışır. Onarım dosyaları Windows Update'ten indirilir; Windows Update erişimi yoksa DISM
  0x800F081F / 0x800F0906 hatası verir ve bu hata olduğu gibi gösterilir. Onarım 10-60 dakika (bazen daha uzun) sürebilir;
  DISM indirme ve açma aşamalarında yüzdeyi uzun süre aynı tutar. Uygulama bu sırada dakikada bir geçen süreyi yazar.
- Teslim En İyileştirme önbelleğinde Windows'un sabitlediği (bekleyen Windows Update / Store işleri için tutulan) dosyalar
  temizlenmez; bunlar Windows tarafından gerektiğinde kendiliğinden silinir. Hizmet boştayken Windows etkin önbellek kaydı
  bildirmeyebilir; bu durumda yalnızca diskteki ölçüm gösterilir ve temizlenebilir miktar vaat edilmez.
- Çalışan uygulamayı kapatıp yeniden deneme, yalnızca kurulum klasörü Programlar ve Özellikler kaydından güvenle bulunabilen
  paketlerde mümkündür. Bir paketin dosyalarını başka bir uygulama DLL olarak yüklemiş olabilir (ör. OBS Sanal Kamera
  modülünü yükleyen bir tarayıcı/Electron uygulaması); bu durumda o uygulama listelenir ve kapatılması kullanıcıya bırakılır.
- Windows Geçici Dosyalar kartı, Ayarlar'daki listenin yalnızca güvenli ve dosya sistemi/resmi cmdlet ile doğrulanabilen
  kategorilerini kapsar. Windows Update Temizleme (DISM gerektirir), Küçük resimler (Gezgin kilitler), İndirilenler,
  Windows.old, önceki Windows kurulumu, sürücü paketleri ve Çöp Kutusu bilinçli olarak dahil edilmez. Bu nedenle toplam,
  Ayarlar ekranındaki değerden farklı olabilir. Son 24 saatte değişen dosyalar sayılmaz ve silinmez.
  `%WINDIR%\Temp` yönetici izni olmadan okunamaz (uygulama kontrolleri zaten yönetici olarak çalıştırır).
- Winget "kurulum teknolojisi farklı" (0x8A15008E) bildirdiğinde paket otomatik yükseltilemez; paketi kaldırıp yeni sürümü
  kurmak gerekir. Uygulama bunu yalnızca kullanıcı "Otomatik uygulanmayan güncellemeler" penceresinde ilgili paketi ayrıca
  seçerse yapar (kaldırma işlemi kullanıcının kararıdır). Kurulum programı hataları (ör. OBS "exit code 6") winget'in
  bildirdiği gerçek kodla gösterilir; kodun anlamını yalnızca üretici belgeleyebilir.
- Kendini güncelleyen uygulamalar (ör. Discord) açıkken winget ile güncellenemeyebilir; en kolay yol uygulamayı tamamen
  kapatıp yeniden açmaktır (kendini günceller) ya da uygulamanın sunduğu "Kapat ve tekrar dene" seçeneğidir.
- MRT, Windows Update ile aylık dağıtılan bir araçtır (KB890830). Sistemde yoksa MRT kartı "Tarama başarısız – MRT.exe bulunamadı" gösterir.
- Çöp Kutusu, UAC'yi onaylayan kullanıcı hesabının çöp kutusudur (standart kullanıcı + başka bir yönetici parolası
  kullanılırsa o yönetici hesabının çöp kutusu olur).

## Sürüm geçmişi

### v1.3.0

**Yeni**
- **Yeni ad: E-mre Hub.** EXE `E-mre Hub.exe`, Releases paketi `E-mre-Hub-vX.Y.Z.zip`. Veriler artık
  `%LOCALAPPDATA%\E-mre Hub` altında; önceki sürümün işlem geçmişi ilk açılışta otomatik kopyalanır, eski klasör silinmez.
- **Kartsız mod:** NVIDIA RTX ekran kartı olmayan (kart yok, başka marka veya RTX olmayan NVIDIA) Windows 11 bilgisayarlarda
  uygulama "Kartsız Devam Et" ile kullanılabilir; yalnızca NVIDIA Driver kartı "Kullanım dışı" olur (seçilemez, çalıştırılamaz,
  toplu işlemlere girmez). Diğer 9 kart normal çalışır. Windows 11 zorunlu kalır: değilse uygulama hiç kullanılamaz.
- **Yönetici yetkisi olmadan giriş:** UAC izni verilmezse uygulamaya girilebilir ama 10 kartın tamamı "Kullanım dışı" olur ve toplu
  kontrol/güncelleme butonları kapanır. İşlemler panelinde "Yönetici olarak yeniden başlat" butonu çıkar.

**İyileştirmeler**
- RTX bulunamaması artık hata değil uyarı olarak gösterilir ve günlüğe yazılır; algılanan gerçek ekran kartı gösterilir.
- Sistem Sağlık Özeti kullanım dışı kartı "çalıştırılmadı" saymaz, ayrıca "N kullanım dışı" olarak gösterir.
- Sistem Bilgileri: NVIDIA kartı olmayan sistemde NVIDIA alanları "Bilgi alınamadı" yerine "NVIDIA ekran kartı yok" gösterir.

### v1.2.0

**Yeni**
- **Windows Geçici Dosyalar** kartı: kategori bazında gerçek ölçüm (ölçülen / temizlenebilir / korunan), kategori seçimi,
  temizlikten sonra tüm kategorilerin dosya sisteminden yeniden ölçülmesi (Önce / Sonra / Temizlenen / Kalan).
- **DISM onarımı:** "Onarılabilir" sonucu artık onayla `DISM /RestoreHealth` ile onarılır ve `/CheckHealth` ile doğrulanır;
  onarım sırasında dakikada bir gerçek geçen süre gösterilir. Onaysız onarım yapılmaz.
- **Çalışan uygulamayı kapatıp yeniden deneme:** Güncellemeyi engelleyen işlemler Windows Restart Manager ile tespit edilir;
  onayla kapatılıp güncelleme yeniden denenir (hizmetler ve sistem işlemleri asla kapatılmaz).
- **Manuel güncellemeler:** Winget'in otomatik uygulamadığı güncellemeler (açık hedefleme gerekli / kurulum teknolojisi farklı)
  seçmeli pencerede sunulur ve yalnızca işaretlenirse uygulanır.
- Sistem Sağlık Özeti, Son İşlem özeti ve işlem geçmişi (uygulama kapansa da korunur); her kartta son çalıştırılma zamanı.
- Sistem Bilgileri paneli (WMI / kayıt defteri / nvidia-smi / disk verileri).
- Detaylı Sonuç paneli: gerçek komutlar, Exit Code, stdout/stderr, süre, servis yanıtları ve **paket bazlı sonuçlar**
  (durum, neden, kod + sembol, kurulum programı çıkış kodu, winget mesajı, engelleyen uygulamalar).
- Uzun işlemler bitince Windows 11 bildirimi (kapatılabilir).
- Log Yönetimi: seviye etiketleri, filtreler, temizleme, dışa aktarma, klasörü açma, Son İşlemler sekmesi.

**İyileştirmeler**
- Winget: paket bazında resmi hata kodu yorumu (0x8A15008E, 0x8A150111 + kurulum programı çıkış kodu vb.); otomatik ve manuel
  güncellemeler ayrı sayılır; güncelleme sonrası tek sorguyla doğrulama. Winget "başarılı" dese de doğrulamada hâlâ görünen
  paket başarı sayılmaz. 0x8A15008E kayıtları kalıcıdır (aynı sürüm boşuna tekrar denenmez).
- "Tümünü Güncelle" ve "Seçilenleri Güncelle" kontrol yapılana kadar devre dışı; kontrol edilmemiş kart güncellenmez.
- İşlem durumu (Hazır / Kontrol / Güncelleme / Tamamlandı / Hata / İptal) ayrı gösterilir; işlem bitince tek bir durum
  geçişiyle animasyon durur ve Fluent tik / hata / uyarı ikonu gösterilir.
- Özet metinleri gerçek dağılımı gösterir ("3 güncelleme bulundu (Winget 2, Microsoft Defender 1) · 1 manuel …",
  "4 işlem · 2 başarılı · 1 uyarı · 1 hata · Winget: 2 paket güncellenemedi").
- İşlem sırası: DISM, SFC'den önce (önce bileşen deposu, sonra sistem dosyaları).
- Sistem komutları güvenli `cmd.exe /d /s /c` sarmalayıcısıyla çalışır (tırnaklama, enjeksiyon koruması).

**Performans**
- Modüller arayüz iş parçacığının dışında çalışır (WMI, XML, imza doğrulama, winget ayrıştırma arayüzü dondurmaz).
- Boşta CPU ~%20 → ~%0-2 (sonsuz bulanık arka plan animasyonu kaldırıldı); işlem sürerken arayüz CPU'su ~%30 → ~%7
  (kart gölgeleri ayrı statik katmanda, sürekli animasyonlar 30 kare/sn).
- Günlük ve ilerleme bildirimleri 100 ms'de bir toplu uygulanır (dosyaya her satır yine eksiksiz yazılır).
- Sistem bilgileri, GPU listesi ve yönetici kontrolü gereksiz yere tekrar okunmaz.

**Düzeltmeler**
- DISM "onarılabilir" sonucu başarılı gösterilmez ("Dikkat: Onarılabilir durumda").
- 0x8A15010A (kurulumdan önce yeniden başlatma gerekli) artık başarı sayılmaz.
- Geçici dosyalarda Windows'un sabitlediği Teslim En İyileştirme önbelleği "temizlenebilir" gösterilmez (önceden
  9,88 GB temizlenebilir deyip 1,66 GB temizleniyordu); okunamayan konum "0 bayt" yerine "Okunamadı" gösterilir.
- İşlem bittiğinde ilerleme animasyonunun durmaması düzeltildi; iptal edilen işlem "hata" değil "iptal" olarak gösterilir.

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
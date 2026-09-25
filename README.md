# E-mre Control Center

> v1.4.0 ile uygulamanın adı **E-mre Control Center** oldu (önceki adları: E-mre Hub – v1.3, RTX Windows Updater – v1.0–v1.2).

Windows 11 bilgisayarlar için güncelleme ve bakım merkezi (NVIDIA RTX ekran kartı için sürücü desteğiyle; RTX yoksa
"kartsız mod" ile kullanılır). Winget, Windows Update, Microsoft Store,
NVIDIA sürücüsü ve Microsoft Defender güncellemelerini **gerçek sistem verileriyle** kontrol eder,
yalnızca kullanıcı onayıyla kurar, Windows geçici dosyalarını ve (onaylanırsa) Çöp Kutusu'nu temizler ve Windows'un
kendi bakım araçlarını (SFC, DISM CheckHealth / onayla RestoreHealth, MRT hızlı tarama) çalıştırır.

- **İki çalışma şekli:** "Tümünü Kontrol Et / Tümünü Güncelle" veya kartları seçerek "Seçilenleri Kontrol Et /
  Seçilenleri Güncelle-Çalıştır". Seçilmeyen karta hiçbir şekilde dokunulmaz. Güncelleme butonları ancak gerçek bir
  kontrol işlem gerektiren bir sonuç bulduğunda etkinleşir.
- **Kontrol Merkezi:** ana sayfada 7 kategori (üstte 4, altta 3): **Güncelleme**, **Temizleme**, **Cihaz Sağlık**, **Hız Testi**,
  **Genel Ayarlar**, **Özet**, **Cihaz Bilgileri**. Her kategori ekranında solda bölmeler (alt menü), sağda seçili bölmenin içeriği bulunur. Ana sayfadaki **Ara** kutusu
  (Ctrl+F) kategori, bölme ve kart adlarında arar. 10 kartın tamamı
  aynı yapıda (solda büyük ikon, sağda geniş kart): açıklama, gerçek durum, "?" bilgi kutusu, seçim kutusu ve kartın kendi
  işlem butonu ("Kontrol Et", "Tarama Başlat", "Kontrolü Başlat", "Hızlı Taramayı Başlat").
- **Hız Testi:** gerçek ölçümle indirme / yükleme hızı, ping (boşta ve yük altında), titreşim, paket kaybı, ISS ve test sunucusu;
  canlı gösterge, kullanım uygunluğu ve sonuç geçmişi (bkz. Hız Testi).
- **Şeffaflık:** Sistem Sağlık Özeti, Sistem Bilgileri, her kartta son çalıştırılma zamanı, Detaylı Sonuç paneli
  (gerçek komut, çıkış kodu, stdout/stderr, süre), Son İşlem özeti, işlem geçmişi, Windows bildirimleri ve Log Yönetimi.

- **Kurulum ve kaldırma:** `E-mre-Control-Center-Setup-vX.Y.Z.exe` ile gerçek bir Windows programı gibi kurulur (Program Files,
  Başlat menüsü, isteğe bağlı masaüstü kısayolu, Ayarlar → Uygulamalar kaydı). Windows'tan kaldırılırken veda ekranı ve isteğe
  bağlı geri bildirim ("Neden kaldırıyorsunuz?") açılır (bkz. [Kurulum ve kaldırma](#kurulum-güncelleme-ve-kaldırma)).
- **Uygulama içi güncelleme:** yeni sürüm yayınlandığında uygulama açılışta "Yeni sürüm yayınlandı" penceresini gösterir; Güncelle ile
  indirilir, doğrulanır ve kurulur (zorunlu; bkz. [Uygulama içi güncelleme](#uygulama-içi-güncelleme-zorunlu)).

- Teknoloji: C# / .NET 8 / WPF, MVVM
- Çıktı: `E-mre Control Center.exe`: tek dosya, self-contained (hedef bilgisayarda .NET kurulu olması gerekmez). Kurulum dosyası
  aynı EXE'dir; adında "Setup" geçtiği için kurulum ekranıyla açılır.
- Arayüz: siyah / koyu lacivert, neon mavi vurgular, gölgeli kartlar, animasyonlar, Segoe Fluent ikonları (emoji yok)

> Bu depo **herkese açıktır (public)**: kaynak kod ve Releases'taki kurulum dosyaları herkes tarafından görülebilir ve
> indirilebilir. Uygulama içi güncelleme de yeni sürümü bu deponun Releases sayfasından okur.

---

## Hızlı başlangıç (arkadaşlar için)

1. https://github.com/E-mre-Hub/E-mre-Control-Center/releases/latest adresini açın (GitHub hesabı veya davet gerekmez).
2. Deponun **Releases** bölümünden en son **`E-mre-Control-Center-Setup-vX.Y.Z.exe`** dosyasını indirin ve çift tıklayın.
   Kurulum ekranında **Yükle**'ye basın, Windows'un yönetici izni penceresinde **Evet** deyin. Kurulum bitince
   "Bizi tercih ettiğiniz için teşekkürler!" ekranından uygulamayı başlatın; sonra Başlat menüsünden (veya masaüstü kısayolundan) açılır.
   - Kurmadan kullanmak isterseniz aynı sayfadaki `E-mre-Control-Center-vX.Y.Z.zip` (taşınabilir) indirilip çıkarılır ve
     `E-mre Control Center.exe` çalıştırılır (eski paketler: v1.3.x `E-mre-Hub-vX.Y.Z.zip`, v1.2.0 ve öncesi `RTX-Windows-Updater-vX.Y.Z.zip`).
3. **Windows SmartScreen uyarısı:** dosya dijital olarak imzalanmadığı için ilk açılışta
   "Windows kişisel bilgisayarınızı korudu" uyarısı çıkabilir. **Ek bilgi → Yine de çalıştır** seçin.
   (Bu, imzasız her uygulamada görülen normal bir uyarıdır; kaynak kodun tamamı bu depodadır.) Aynı nedenle UAC penceresinde
   "Yayıncı: Bilinmeyen" yazar; bkz. [Kod imzalama](#kod-imzalama-ve-bilinmeyen-yayıncı).
4. Uygulama açılışta yönetici izni isteyecektir; UAC penceresinde **Evet** deyin. (İzin vermezseniz uygulama açılır ama 10 kartın tamamı
   kullanım dışı olur; hiçbir kontrol veya güncelleme yapılamaz. Hız Testi, Cihaz Bilgileri ve arama yine kullanılabilir.)
5. Kaldırmak için: **Windows Ayarlar → Uygulamalar → Yüklü uygulamalar → E-mre Control Center → Kaldır**.
6. Yeni sürümler (v1.7.0 ve sonrası) uygulama açılırken kendiliğinden bildirilir: **Güncelle**'ye basmanız yeterlidir.

Gereksinimler: Windows 11 (derleme 22000+, zorunlu), internet bağlantısı, winget (Windows 11'de hazır gelir).
NVIDIA GeForce RTX ekran kartı önerilir. Kart yoksa, başka marka bir kart ya da RTX serisi olmayan bir NVIDIA kartı varsa
uygulama **"Kartsız Devam Et"** ile kullanılabilir; yalnızca NVIDIA Driver kartı kullanım dışı olur (bkz. [Kartsız mod](#kartsız-mod-nvidia-rtx-yoksa)).

---

## 1. Klasör yapısı

```
Desktop\E-mre Control Center\        ← Proje klasörü (önceki adları: E-mre_Hub, E-mre_App)
├── E-mre Control Center.exe         ← Son uygulama (yerel derleme çıktısı; depoya konmaz)
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
    ├── App.xaml(.cs)                ← Giriş noktası (uygulama / kurulum / kaldırma), global hata yakalama, argümanlar
    ├── Core\
    │   ├── AppInfo.cs               ← Uygulama adı (E-mre Control Center), eski adlar, veri klasörü, sürüm, depo adresi
    │   ├── LaunchMode.cs            ← Açılış biçimi: uygulama / kurulum (dosya adında "Setup") / --install / --uninstall
    │   ├── ShellLink.cs             ← Windows kısayolu (.lnk) oluşturma ve okuma (IShellLinkW)
    │   ├── SetupLog.cs              ← Kurulum / kaldırma günlüğü (%TEMP%\E-mre Control Center Kurulum.log)
    │   ├── ProtectedDirectory.cs    ← Yalnızca Yöneticiler + SYSTEM erişimli klasör (kaldırıcı kopyası, indirilen güncelleme)
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
    │   ├── SystemInfoService.cs     ← Cihaz Bilgileri (WMI, kayıt defteri, nvidia-smi, DriveInfo; gruplu alanlar)
    │   ├── DeviceMonitorService.cs  ← Cihaz Durumu canlı ölçüm (GetSystemTimes, bellek, ACPI termal bölge, NVML, disk sıcaklığı)
    │   ├── SpeedTestService.cs      ← Hız Testi (Cloudflare; TCP gecikme, ICMP paket kaybı, çoklu/tek bağlantı indirme-yükleme)
    │   ├── OoklaSpeedtestService.cs ← Speedtest by Ookla (resmi araç: bul / winget ile kur / yakın sunucular / JSON olaylarıyla test)
    │   ├── AppStateStore.cs         ← İşlem geçmişi / son sonuçlar / ayarlar / hız testi geçmişi (%LOCALAPPDATA%\…\state.json)
    │   ├── NotificationService.cs   ← Windows 11 bildirimleri (toast)
    │   ├── InstallerService.cs      ← Kurulum / güncelleme / kaldırma (Program Files, kısayollar, Uninstall kaydı, doğrulama, geri alma)
    │   ├── FeedbackService.cs       ← Kaldırma geri bildirimi → Google Formu (yanıtlar Google E-Tablolar'da)
    │   ├── UpdateService.cs         ← Uygulama içi güncelleme: ana deponun GitHub Releases'ı (API) denetimi, indirme + doğrulama (boyut, SHA-256, ürün, sürüm)
    │   └── UpdateOrchestrator.cs    ← Güvenli sıra, Tümü / Seçilenler akışları, modül izolasyonu, iptal
    ├── ViewModels\                  ← MainViewModel (bölme gezinmesi; + MainViewModel.Device: Cihaz bölmeleri, canlı ölçüm; + MainViewModel.Search: ana sayfa araması), TextSearch (Türkçe harf duyarsız arama), SpeedTestViewModel, DeviceViewModels, CategoryViewModel (7 kategori + bölmeler; yalnızca
    │                                  arayüz düzeni), DialogViewModel, DetailViewModel, ThrottledProgress, kart/satır modelleri, komutlar;
    │                                  SetupViewModels (kurulum ve kaldırma ekranları), UpdateViewModel (zorunlu güncelleme penceresi)
    ├── Views\                       ← MainWindow.xaml(.cs): giriş sayfası, Kontrol Merkezi ana sayfası, kategori ekranları; RingGauge.cs, SpeedGauge.cs (hız göstergesi), CenteredWrapPanel.cs, WaveBackdrop.cs (ana sayfa dalga zemini, statik); Converters.cs;
    │                                  SetupWindow.xaml (kurulum), UninstallWindow.xaml (kaldırma + geri bildirim), SetupResources.xaml, WindowFrame.cs
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

Tek komut (proje klasöründe, ör. `E-mre Control Center`):

```bash
powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1
```

Betik şunları yapar: .NET 8 SDK'yı bulur, ICO yoksa üretir, aşağıdaki komutu çalıştırır ve EXE'yi
proje klasörüne `E-mre Control Center.exe` olarak kopyalar (klasörün adı önemli değildir).

Elle:

```bash
dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release
```

```bash
dotnet publish src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o build\publish
```

Kaynaktan derlemek için (depoya erişimi olan herkes):

```bash
git clone https://github.com/E-mre-Hub/E-mre-Control-Center.git "E-mre Control Center"
```

Ardından `E-mre Control Center` klasöründe `tools\Build-Exe.ps1` çalıştırılır (klasör adında boşluk olduğu için komut satırında
tırnak içinde yazılır). Depo: https://github.com/E-mre-Hub/E-mre-Control-Center. Eski adresler (`E-mre-Hub/E-mre_Hub`,
`Emrefb06/RTX-Windows-Updater`) GitHub tarafından buraya yönlendirilir.

### Yeni sürüm yayınlama (depo sahibi)

1. `src\RtxWindowsUpdater\RtxWindowsUpdater.csproj` içindeki `<Version>` değerini artırın (ör. `1.7.3`).
2. Değişiklikleri commit'leyip gönderin, ardından etiket oluşturun:

```bash
git tag v1.7.3
```

```bash
git push origin v1.7.3
```

3. GitHub Actions (`.github/workflows/release.yml`) EXE'yi Windows sunucusunda derler ve **Releases** sayfasına iki dosya ekler:
   `E-mre-Control-Center-Setup-v1.7.3.exe` (kurulum) ve `E-mre-Control-Center-v1.7.3.zip` (taşınabilir). Arkadaşlar oradan indirir;
   yüklü uygulamalar bu yayını "Yeni sürüm yayınlandı" olarak görür (v1.7.2 ve sonrası).
   Etiketteki sürüm (v1.7.3) EXE'nin sürümü olarak kullanılır; csproj'daki `<Version>` ile aynı olmalıdır. Yayının metni yalnızca
   README'deki `### v1.7.3` bölümüdür (uygulamadaki güncelleme penceresinde de bu metin görünür); bu bölüm etiketten önce yazılmalıdır.

Not: Herkese açık depolarda GitHub Actions standart sunucularda ücretsizdir;
bir derleme yaklaşık 3-5 dakika sürer.

### Kod imzalama ve "Bilinmeyen yayıncı"

- UAC penceresindeki **"Yayıncı"** satırı yalnızca EXE'nin **dijital imzasından** (Authenticode) okunur. EXE imzasız olduğu için
  "Bilinmeyen" yazar. EXE içindeki bilgiler (Şirket / Ürün / Açıklama / Telif: **E-mre Control Center**, Özellikler → Ayrıntılar'da görünür)
  bunu değiştirmez. Uyarıyı kaldırmanın meşru tek yolu, Windows'un güvendiği bir kuruluştan alınan **kod imzalama sertifikasıyla**
  imzalamaktır; uyarı hiçbir şekilde atlatılmaz.
- UAC'de görünecek ad sertifikayı veren kuruluşun **doğruladığı** addır: bireysel sertifikada kimlikteki ad-soyad, şirket
  sertifikasında ticaret siciline kayıtlı unvan. "E-mre Control Center" yayıncı olarak ancak bu adla kayıtlı bir işletme adına alınan
  sertifikayla görünür.
- Sertifika türleri: **OV** (bireylere de verilir; yıllık ücretli; 2023'ten beri anahtar USB donanım anahtarında veya bulut
  imzalama hizmetinde tutulur) ve **EV** (yalnızca şirketlere). Sertifika kuruluşlarının güncel koşul ve fiyatlarını kontrol edin.
- **SmartScreen** ("Windows kişisel bilgisayarınızı korudu") ayrı bir sistemdir: dosyanın indirme itibarına bakar. İmza UAC'deki
  yayıncıyı hemen düzeltir ama SmartScreen uyarısı, itibar oluşana kadar imzalı EXE'de de bir süre görülebilir.
- **İmzalı derleme:** sertifika (veya USB anahtar) takılıyken parmak iziyle çalıştırın:

```bash
powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1 -SignThumbprint <sertifika parmak izi>
```

  Betik EXE'yi SHA-256 ve zaman damgasıyla imzalar (Windows SDK'daki `signtool` varsa onunla, RFC 3161 zaman damgası; yoksa
  PowerShell'in `Set-AuthenticodeSignature` komutuyla, standart Authenticode zaman damgası), ardından imzayı doğrular: imza Windows tarafından "Geçerli" sayılmazsa, beklenen
  sertifika değilse veya zaman damgası eklenemezse derleme hata verir ve EXE kök klasöre kopyalanmaz. Zaman damgası sunucusu
  `-TimestampServer` ile değiştirilebilir (varsayılan `http://timestamp.digicert.com`). Parmak izini Windows'ta "Kullanıcı
  sertifikalarını yönet" → Kişisel → sertifika → Ayrıntılar → Parmak izi bölümünden alabilirsiniz.
- GitHub Actions EXE'yi imzasız derler (USB anahtardaki sertifika bulut sunucusunda kullanılamaz). İmzalı sürüm için EXE
  yerelde `-SignThumbprint` ile üretilir, `E-mre-Control-Center-vX.Y.Z.zip` olarak sıkıştırılır ve Releases sayfasındaki dosyanın yerine yüklenir.
- Kendinden imzalı (ücretsiz) bir sertifika yalnızca kendi bilgisayarınızda test içindir; başka bilgisayarlarda yayıncı yine
  "Bilinmeyen" görünür. Başkalarından böyle bir sertifikayı "güvenilir" olarak yüklemelerini istemeyin (güvenlik riski).

### Arkadaş ekleme (depo sahibi)

Depo herkese açık olduğu için indirmek ve güncelleme almak için davet gerekmez. Birine koda **yazma** izni vermek için:
depo sayfası → **Settings → Collaborators and teams → Add people** → GitHub kullanıcı adı (erişim aynı sayfadan kaldırılır).

## 5. Yönetici izni (UAC) nasıl çalışır

- `app.manifest` → `asInvoker`. Uygulama normal kullanıcı olarak açılır, gereksinimleri gösterir.
- Windows 11 uygunsa (RTX olsun olmasın) ve yönetici değilse, uygulama **neden gerektiğini açıklayan** bir pencere gösterir.
  Kullanıcı onaylarsa `AdminPrivilegeManager` kendini `Verb = "runas"` ile yeniden başlatır → Windows'un
  "Bu uygulamanın cihazınızda değişiklik yapmasına izin veriyor musunuz?" UAC penceresi açılır.
- Aynı anda yalnızca bir örnek çalışır (tek örnek kilidi); iki pencerenin aynı anda güncelleme yapması engellenir.
- UAC reddedilirse (Win32 hata 1223) uygulama kapanmaz: "Yönetici izni reddedildi" gösterilir, hiçbir değişiklik yapılmaz.
- **Yönetici yetkisi olmadan uygulamaya girilebilir ama 10 kartın tamamı "Kullanım dışı" olur** (kart butonları, seçim kutuları,
  "Tümünü / Seçilenleri Kontrol Et" ve güncelleme butonları kapalı). Ana sayfada, kategori ekranlarının sol menüsünde ve
  Genel Ayarlar → Yönetici Yetkisi bölmesinde bir uyarı ve **"Yönetici olarak yeniden başlat"** butonu çıkar; uygulama UAC onayıyla
  yönetici olarak yeniden açılır ve doğrudan ana ekrana döner.
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
| **NVIDIA** | GPU: WMI. Kurulu sürücü: `nvidia-smi` (yoksa WMI sürümünden hesap). NVIDIA App: kayıt defterinden tespit (bilgi). En son sürücü: nvidia.com sürücü sayfasının kullandığı resmi NVIDIA servisleri (`lookupValueSearch.aspx` + `AjaxDriverService DriverManualLookup`, WHQL, DCH, Windows 11). | Yalnızca `https://*.download.nvidia.com` adresinden indirilir, disk alanı kontrol edilir, **Authenticode imzası doğrulanır (NVIDIA Corporation olmalı)**, `-s -noreboot` ile kurulur, ardından sürüm `nvidia-smi` ile yeniden okunarak doğrulanır. Kurulum dosyası `%ProgramData%\E-mre Control Center\Downloads` klasörüne indirilir ve işlem bitince (başarılı ya da değil) silinir. Kartsız modda bu kart hiç çalışmaz. |
| **Defender** | `Get-MpComputerStatus` + Microsoft'un resmi Defender sürüm servisi (`microsoft.com/security/encyclopedia/adlpackages.aspx?action=info&arch=x64`) ile gerçek karşılaştırma. | `Update-MpSignature` (başarısızsa MicrosoftUpdateServer, ardından MMPC kaynağı). Sürüm yeniden okunarak doğrulanır. Defender ayarlarına dokunulmaz. |
| **Windows Geçici Dosyalar** | Ayarlar → Sistem → Depolama → Geçici dosyalar'daki güvenli kategorilerin gerçek konumları ölçülür: Kullanıcı geçici dosyaları (`%TEMP%`, .NET tek dosya çalışma klasörü hariç), Windows geçici dosyaları (`%WINDIR%\Temp`), Teslim En İyileştirme önbelleği (resmi `Get-DeliveryOptimizationStatus` / `Get-DOConfig` + önbellek klasörünün diskteki gerçek boyutu), Windows hata raporlama dosyaları (`WER\ReportArchive`, `ReportQueue`), DirectX gölgelendirici önbelleği (`%LOCALAPPDATA%\D3DSCache`). Her kategori için ayrı ayrı **Ölçülen**, **Temizlenebilir** ve **Korunan** hesaplanır. Temizlenebilir: 24 saatten uzun süredir değişmemiş, sistem/salt okunur olmayan dosyalar; Teslim En İyileştirme'de Windows'un sabitlemediği (IsPinned) ve etkin indirmede olmayan önbellek. Korunan alan (son 24 saat, salt okunur/sistem, sabitlenmiş/etkin önbellek, bir önceki temizlikte Windows'un silmediği önbellek) asla "temizlenebilir" sayılmaz ve kartta "Ek olarak X korunuyor" diye ayrı yazılır. Bağlantı noktaları izlenmez. Okunamayan konum "0 bayt" değil "Okunamadı" olarak raporlanır. Sonuç: "Temizlenebilir: X" veya "Temizlenecek geçici dosya bulunamadı". | Onay penceresinde tahmini alan ve kategori kutuları (varsayılan işaretli, seçili toplam canlı hesaplanır). Yalnızca işaretli kategoriler temizlenir; kullanımdaki dosyalar atlanır, kök klasörler ve son 24 saatte oluşturulmuş klasörler silinmez; Teslim En İyileştirme için Windows'un resmi `Delete-DeliveryOptimizationCache -Force` komutu (sabitlenmiş dosyalar silinmez; `-IncludePinnedFiles` kullanılmaz). Temizlikten sonra TÜM kategoriler dosya sisteminden yeniden ölçülür (silme komutunun kendi bildirimi sonuç sayılmaz): **Önce / Sonra / Temizlenen / Kalan / Kullanımda-atlanan / Korunan**. Kullanımdaki dosyalar kaldıysa sonuç "Kısmen temizlendi" (uyarı) olur. Çöp Kutusu bu hesaba dahil değildir. |
| **Çöp Kutusu** | `SHQueryRecycleBin` (tüm sürücüler). | Yalnızca onayla `SHEmptyRecycleBin`; sonra yeniden sayılarak doğrulanır. |
| **SFC** | `sfc /verifyonly` – yalnızca tarar, **onarım yapmaz**. Çıktı (UTF-16), `sfc.exe`'nin kendi mesaj tablosundan Windows dilinde yüklenen gerçek mesajlarla eşleştirilir; ilerleme yüzdesi canlı gösterilir. | Kartın "Tarama Başlat" butonu: `sfc /scannow` (onaydan sonra). Toplu güncellemede `sfc /scannow` yalnızca doğrulama bozuk dosya bulduysa çalışır. Onarım yarıda kesilmez. |
| **DISM** | `DISM /Online /Cleanup-Image /CheckHealth` (çıktının dilden bağımsız okunması için DISM'in `/English` görüntüleme seçeneğiyle). Sonuç: Sağlıklı / **Dikkat: Onarılabilir durumda** (başarılı sayılmaz, işlem gerektirir) / Onarılamaz / Hata. | Yalnızca kontrol "onarılabilir" dediyse ve kullanıcı onay verdiyse ("Tümünü Güncelle", "Seçilenleri Çalıştır" veya kartın "Onar" onayı): `DISM /Online /Cleanup-Image /RestoreHealth`. DISM'in "başarılı" mesajı doğrudan kabul edilmez; ardından `/CheckHealth` yeniden çalıştırılır ve bileşen deposu gerçekten sağlıklıysa "Onarıldı (doğrulandı)" gösterilir. Hata kodları (ör. 0x800F081F kaynak bulunamadı, 0x800F0906 indirilemedi) açıklamasıyla gösterilir. Onarım başladıktan sonra yarıda kesilmez. |
| **MRT** | `MRT.exe /Q /N` – sessiz **hızlı tarama, yalnızca tespit** (dosyalara dokunulmaz). Sonuç, `%windir%\debug\mrt.log` dosyasına bu tarama için eklenen "Results Summary" ve "Return code" satırlarından okunur (KB891716 dönüş kodları). Tam tarama asla başlatılmaz. | Tehdit tespit edildiyse ve kullanıcı onaylarsa `MRT.exe /Q` (hızlı tarama + temizleme). |
| **Hız Testi – Cloudflare** | `speed.cloudflare.com`: `/meta` (ISS, IP, konum, veri merkezi), TCP bağlantı süresi (ping), 50 ICMP yankı isteği (paket kaybı), `__down` / `__up` ile 10'ar sn indirme / yükleme (çoklu = 6, tek = 1 HTTP/1.1 bağlantısı). | Sistemde değişiklik yok; sonuç yalnızca yerel geçmişe (IP'siz) yazılır. |
| **Hız Testi – Speedtest by Ookla** | Ookla'nın resmi aracı (Speedtest CLI 1.2): `speedtest --servers --format=json` (yakın sunucular), `speedtest [--server-id=N] --format=jsonl --progress=yes` (canlı olaylar + sonuç). Araç bulunamazsa / koşullar kabul edilmemişse çalıştırılmaz. | Araç yoksa yalnızca onayla: `winget install --id Ookla.Speedtest.CLI --exact --source winget --scope user`; başarı winget listesi + `speedtest --version` ile doğrulanır. |

Güvenli işlem sırası: Winget → Windows Update → Microsoft Store → NVIDIA → Defender → DISM → SFC → MRT →
Geçici Dosyalar → Çöp Kutusu. (DISM, SFC'den önce çalışır: Microsoft'un önerdiği gibi önce bileşen deposu, sonra sistem dosyaları.)

Her modül bağımsızdır; biri başarısız olursa diğerleri devam eder. Durumlar: Güncel / Sağlıklı, Güncelleme mevcut,
Güncellendi, Kısmen güncellendi, Dikkat (ör. yalnızca manuel güncellemesi olan winget), Yeniden başlatma gerekli, Yönetici izni gerekli,
Kontrol edilemedi, Başarısız, Atlandı, Kullanım dışı (RTX ekran kartı yoksa NVIDIA Driver; yönetici yetkisi yoksa tüm kartlar).
Hiçbir hata başarılı gibi gösterilmez; nedeni kartta, sonuç ekranında ve günlükte yazar.

## 7. Kullanım

1. `E-mre Control Center.exe` dosyasına çift tıklayın (kendi derlemenizde: `Desktop\E-mre Control Center\E-mre Control Center.exe`).
2. Sistem gereksinimleri (Windows 11, NVIDIA RTX, yönetici) kontrol edilir. Windows 11 değilse uygulama kullanılamaz
   (devam edilemez). NVIDIA RTX ekran kartı yoksa GPU satırı turuncu uyarı olur ve devam butonu "Kartsız Devam Et" olarak görünür.
3. Yönetici izni penceresinde "Yönetici olarak başlat" → UAC'de "Evet". "Şimdi değil" derseniz de girebilirsiniz ama tüm kartlar
   kullanım dışı olur; sonradan "Yönetici olarak yeniden başlat" butonuyla (ana sayfa, kategori sol menüsü veya Genel Ayarlar →
   Yönetici Yetkisi) yetki verebilirsiniz.
4. "Gereksinimleri kabul ediyorum" → "Devam Et" (RTX ekran kartı yoksa "Kartsız Devam Et").
5. **Tümü:** "Tümünü Kontrol Et" tüm kartları kontrol eder (seçimlere bakılmaz). SFC doğrulaması ve MRT hızlı taraması
   birkaç dakika ile yarım saat arasında sürebilir.
6. **Seçilenler:** Kartların sağ üstündeki seçim kutularını işaretleyin (seçili kart neon çerçeveyle gösterilir, üstte
   "N işlem seçildi" yazar, "Seçimi Temizle" tüm seçimleri kaldırır). "Seçilenleri Kontrol Et" yalnızca seçilen kartları
   kontrol eder. Hiç seçim yoksa "Lütfen en az bir işlem seçin." uyarısı çıkar ve hiçbir şey çalışmaz.
7. Sonuçlar kartlarda, "Bulunan Güncellemeler" bölmesinde (program, mevcut sürüm, yeni sürüm, durum) ve "İşlem Günlüğü" bölmesinde görünür.
8. "Tümünü Güncelle (ve Temizle)" → işlem gerektiren tüm kartlar; "Seçilenleri Güncelle / Çalıştır" → yalnızca seçilen
   kartlardan işlem gerektirenler. **Bu iki buton, kontrol yapılana kadar devre dışıdır** ve altında
   "Henüz kontrol yapılmadı. Önce "Tümünü Kontrol Et" veya "Seçilenleri Kontrol Et" çalıştırılmalı." yazar.
   Kontrol edilmemiş kart güncellenmez (günlüğe "kontrol edilmediği için atlandı" yazılır); "Kontrol edilemedi" /
   "Başarısız" sonuçlar hiçbir zaman işlem gerektiren veya başarılı sayılmaz; güncel / sağlıklı olanlar çalıştırılmaz.
   Bir işlem uygulandıktan sonra ilgili kartın kontrol sonucu geçersiz sayılır (yeniden kontrol gerekir).
   Her iki durumda da yapılacak işlemleri listeleyen onay penceresi çıkar.
   "Tümünü Güncelle"de çöp kutusu, Çöp Kutusu kartındaki "Tümünü Güncelle ile birlikte boşalt" kutusuna bağlıdır;
   "Seçilenleri Çalıştır"da ise Çöp Kutusu kartı seçildiyse boşaltılır.
9. **Her kart kendi butonuyla tek başına da çalıştırılabilir** (kartlar Güncelleme, Temizleme ve Cihaz Sağlık kategorilerindedir):
   - Windows Update, Winget, Microsoft Store, NVIDIA Driver, Microsoft Defender, Windows Geçici Dosyalar, Çöp Kutusu →
     "Kontrol Et": yalnızca o kartı gerçekten kontrol eder; güncelleme / temizlenecek öğe bulunursa uygulamak için ayrıca
     onay sorulur (Geçici Dosyalar'da tahmini alan ve kategori seçimiyle).
   - "Tarama Başlat" (sfc /scannow, onaydan sonra), "Kontrolü Başlat" (DISM CheckHealth; "onarılabilir" çıkarsa
     RestoreHealth ile onarım ayrıca sorulur), "Hızlı Taramayı Başlat" (MRT, yalnızca tespit; tehdit bulunursa temizlik ayrıca sorulur).
   - Her karttaki "?" butonu, kartın ne yaptığını anlatan kısa bir bilgi kutusu açar.
   - İşlem sırasında kartta "Kontrol ediliyor…", "Tarama devam ediyor…", "Güncelleniyor…" gibi durum ve canlı ilerleme görünür;
     henüz çalıştırılmamış kartlarda "Henüz çalıştırılmadı" yazar.
10. Kategori ekranının sol menüsündeki **İlerleme** kartı (Güncelleme, Temizleme, Cihaz Sağlık ve Özet'te; ana sayfada hızlı işlem çubuğu) işlem durumunu gösterir: Hazır → Kontrol devam ediyor… / İşlem devam ediyor… →
   İşlem tamamlandı (yeşil tik) / İşlem tamamlandı – hata var (kırmızı) / İşlem iptal edildi (turuncu). Yüzde yalnızca
   modüllerin bildirdiği gerçek adımlardan gelir; dönen ikon ve ilerleme çubuğundaki parıltı yalnızca işlem sürerken
   çalışır ve işlem biter bitmez durdurulur.
   İşlem bittiğinde tek bir durum geçişi yapılır: sonuç kaydı, durum, animasyonun durması, %100, buton durumları ve özet
   aynı anda güncellenir. İlerleme bildirimleri en fazla 100 ms'de bir (yalnızca en son değer) ekrana yansıtılır.
11. Güncellemeden sonra çalışan bir uygulama engel olduysa "Çalışan uygulamalar güncellemeyi engelliyor" penceresi çıkar
   (kapatılacak ve kapatılmayacak işlemler ayrı listelenir). "İşlem Tamamlandı" ekranı her bileşenin gerçek sonucunu gösterir. Yeniden başlatma gerekiyorsa
   "Yeniden başlat" butonu çıkar; onaylarsanız 60 saniye sonra yeniden başlar (`shutdown /a` ile iptal edilebilir).

### Kurulum, güncelleme ve kaldırma

**Kurulum** (`E-mre-Control-Center-Setup-vX.Y.Z.exe`):

- Kurulum dosyası uygulamanın kendisidir: adında "Setup" geçtiği için kurulum ekranıyla açılır ve kendini
  `C:\Program Files\E-mre Control Center\E-mre Control Center.exe` olarak kopyalar (ayrı kurulum aracı, internetten indirme yok).
- **Yükle** → Windows yönetici izni (UAC) ister; izin verilmezse hiçbir şey değişmez. Program Files seçilmesinin nedeni: uygulama
  yönetici olarak çalıştığı için EXE'sinin yalnızca yöneticinin yazabildiği bir klasörde durması gerekir.
- Adımlar ekranda gerçek sonuçlarıyla gösterilir: çalışan uygulama denetimi → program dosyası kopyalama (gerçek bayt ilerlemesi) →
  SHA-256 doğrulaması (kurulan dosya indirilenle bayt bayt aynı) → Başlat menüsü kısayolu → masaüstü kısayolu (isteğe bağlı) →
  Windows Uygulamalar kaydı (ad, sürüm, yayıncı, simge, boyut, kaldırma komutu; kayıt yeniden okunarak doğrulanır).
- Bir adım başarısız olursa yapılanlar geri alınır (önceki sürüm geri yüklenir, yeni kısayollar ve kayıt silinir) ve neden yazılır.
- Son ekran: **"Bizi tercih ettiğiniz için teşekkürler! Aramıza hoş geldin."** ve "E-mre Control Center'ı şimdi başlat".
- **Güncelleme:** yeni sürümün Setup dosyası aynı şekilde çalıştırılır (buton "Güncelle"). Uygulama açıksa kurulum sorar ve normal
  kapanma isteği gönderir; zorla kapatmaz (bir işlem sürüyorsa uygulama kendi onay sorusunu gösterir). Ayarlar, geçmiş ve günlükler
  (`%LOCALAPPDATA%\E-mre Control Center`) korunur.

**Kaldırma** (Windows Ayarlar → Uygulamalar → Yüklü uygulamalar → E-mre Control Center → Kaldır):

- Windows yönetici izni ister, ardından kaldırma ekranı açılır: solda **"Bir dahaki sefere görüşmek üzere!"** ve kaldırılacaklar;
  sağda **"Neden kaldırıyorsunuz?"** (Sevmedim · Kasıyor / yavaş çalışıyor · Hata veriyor · Artık ihtiyacım yok · Diğer) ve isteğe
  bağlı mesaj (en fazla 500 karakter). Butonlar: **Gönder ve Kaldır** (bir şey seçildiyse; yoksa **Kaldır**) ve **İptal**
  (hiçbir şey değişmez).
- Kaldırılanlar: program dosyası ve klasörü, Başlat menüsü / masaüstü kısayolları (yalnızca hedefi bu uygulama olanlar), Windows
  Uygulamalar kaydı, uygulamanın geçici dosyaları (`%TEMP%\.net\E-mre Control Center` ve eski adları) ve bildirim kaydı.
- **"Ayarlarımı, geçmişimi ve günlüklerimi de sil"** işaretlenirse `%LOCALAPPDATA%\E-mre Control Center` (ve varsa eski
  `E-mre Hub` / `RTX Windows Updater` klasörleri) de silinir; işaretlenmezse korunur ve yeniden kurulumda kalınan yerden devam edilir.
- Güvenlik: yalnızca bu uygulamanın bilinen yolları silinir. Program klasörüne sonradan konmuş başka dosyalar ve hedefi başka
  program olan kısayollar bırakılır ve ekranda yazılır; klasör bağlantıları (junction / symlink) izlenmez. Kaldırma,
  `C:\ProgramData` altında yalnızca Yöneticiler ve SYSTEM'in erişebildiği geçici bir kopyadan çalışır: kurulu dosya kullanımda
  kalmadığı için hemen silinir, geçici kopyayı Windows bilgisayar yeniden başlatılınca siler.
- Speedtest by Ookla aracını kurduysanız o ayrı bir programdır; Windows Uygulamalar'dan ayrıca kaldırılabilir.

**Geri bildirim (Google Formlar):**

- Gönderilenler: seçilen nedenler, mesaj, uygulama sürümü ve Windows sürümü (ör. "Windows 11 25H2 (Derleme 26200.9550)").
  Ad, e-posta, kullanıcı adı veya bilgisayar adı gönderilmez (Google, isteğin IP adresini görür).
- Başarı yalnızca Google'ın gerçek yanıtıdır (HTTP 200). Gönderilemezse (ör. internet yok) kaldırma yine yapılır; son ekranda
  gerçek hata ve **Tekrar gönder** gösterilir.
- Yanıtlar depo sahibinin Google Formuna bağlı Google E-Tablosunda toplanır. Form kimlikleri `Services\FeedbackService.cs`
  içindeki `FeedbackForm` sabitlerindedir; boşsa gönderim kapalıdır ve kaldırma ekranı neden / mesaj alanlarını göstermez
  (sahte "gönderildi" yok).

Kurulum / kaldırma günlüğü: `%TEMP%\E-mre Control Center Kurulum.log`. Uygulamanın Hakkında bölmesinde bu kopyanın kurulu mu
taşınabilir mi olduğu yazar.

### Uygulama içi güncelleme (zorunlu)

- Uygulama her açılışta (gereksinimler geçtikten sonra, arka planda) bu deponun son yayınını denetler:
  `https://api.github.com/repos/E-mre-Hub/E-mre-Control-Center/releases/latest` (oturum açılmaz, anahtar kullanılmaz).
- Yeni sürüm varsa girişten sonra ekranın önüne **"Yeni sürüm yayınlandı"** penceresi gelir: yeni / yüklü sürüm, yayın tarihi, boyut ve
  sürüm notları (README'deki sürüm geçmişinden). Güncelleme **zorunludur**: arkadaki ekran kullanılamaz; seçenekler **Güncelle** veya
  **Uygulamayı kapat**. Bir kontrol / güncelleme işlemi veya hız testi sürerken pencere beklenir (işlem yarıda bırakılmaz).
- **Güncelle:** kurulum dosyası indirilir (gerçek bayt ilerlemesi) ve yalnızca şu doğrulamalardan geçerse çalıştırılır: GitHub'ın
  bildirdiği boyut, GitHub'ın bildirdiği SHA-256 özeti, EXE içindeki ürün adı ("E-mre Control Center") ve sürüm (etiketle aynı).
  Uygulama yönetici olarak çalışıyorsa dosya yalnızca Yöneticiler + SYSTEM erişimli bir klasöre (`C:\ProgramData`) indirilir.
  Ardından kurulum başlatılır, uygulama kapanır; kurulum eski sürümün kapanmasını bekler, yeni sürümü kurar ve **kendiliğinden açar**.
  Ayarlar ve geçmiş korunur. Uygulama yönetici olarak çalışmıyorsa Windows UAC sorar; reddedilirse güncelleme yapılmaz ve neden yazar.
- Denetlenemezse (internet yok, GitHub yanıt vermedi, istek sınırı) uygulama normal açılır; güncelleme varmış gibi gösterilmez.
  **Cihaz Bilgileri → Hakkında → Güncelleme** satırında gerçek sonuç yazar ("Güncel (son yayın …) · son denetim …" / "Denetlenemedi: …") ve
  **Şimdi denetle** ile yeniden denetlenebilir.
- Taşınabilir (ZIP) kopyadan güncelleme, uygulamayı Program Files'a kurar (pencerede yazar); sonra Başlat menüsünden açılır.
- v1.6.0 ve öncesinde bu özellik yoktur. **v1.7.0 – v1.7.1** ise hiç oluşturulmamış ayrı bir sürüm deposuna
  (`E-mre-Hub/E-mre-Control-Center-Releases`) baktığı için "Denetlenemedi (HTTP 404)" gösterir ve yeni sürümü göremez. Bu sürümleri
  kullananlar v1.7.2'yi (veya daha yenisini) **bir kez** Setup ile kurmalıdır (Releases → `E-mre-Control-Center-Setup-vX.Y.Z.exe` →
  Güncelle); sonraki sürümler uygulama içinden gelir.

### Kontrol Merkezi (ana sayfa ve kategoriler)

Ana ekran bir **Kontrol Merkezi**'dir: koyu (neredeyse siyah) zeminde ortada parlayan logo ve ad, altında **Ara** kutusu ve 7 kategori
(üstte 4, altta ortalı 3). Her kategoride parlak neon simge, kalın başlık, kısa açıklama ve mevcut gerçek durumdan bir durum satırı
bulunur (ör. "3 işlem · kontrol edilmedi", "1 işlemde hata var", "Kullanım dışı"). Kategoriler çerçevesizdir; üzerine gelince hafifçe
aydınlanır, klavyeyle seçilince neon çerçeve alır. Alttaki ince dalga çizgileri yalnızca süsdür ve hareketsizdir (animasyon yok).
Karta tıklamak ilgili ekranı açar; sol üstteki **Ana Sayfa** butonu veya **Esc** ile geri dönülür.

- **Ara** (Ctrl+F): kategori, bölme ve kart adlarında / açıklamalarında arar (ör. "ping" → Hız Testi, "dism" → Cihaz Sağlık →
  Sağlık Araçları, "günlük" → İşlem Günlüğü ve Günlük Dosyaları). Büyük / küçük harf ve Türkçe karakter duyarsızdır ("gunluk" =
  "günlük"). **Enter** ilk sonucu açar, **Aşağı ok** sonuç listesine geçer, **Esc** aramayı temizler. Arama yalnızca gezinmedir;
  hiçbir işlem başlatmaz.

Her kategori ekranı aynı düzendedir: **solda** Ana Sayfa, kategori başlığı ve **bölmeler** (alt menü); **sağda** seçili bölmenin
başlığı ve içeriği. Kartlar ve ayar satırları Cihaz Bilgileri'ndeki gibi solda büyük ikon, sağda geniş kart olarak gösterilir.
Kategori her açılışta ilk bölmesiyle açılır.

| Kategori | Bölmeler |
|---|---|
| **Güncelleme** | **Güncellemeler** (Windows Update, Winget, Microsoft Store, NVIDIA Driver, Microsoft Defender kartları + işlem çubuğu) · **Bulunan Güncellemeler** · **İşlem Günlüğü** |
| **Temizleme** | **Temizlik** (Windows Geçici Dosyalar, Çöp Kutusu kartları + işlem çubuğu) · **İşlem Günlüğü** |
| **Cihaz Sağlık** | **Sağlık Araçları** (SFC, DISM, MRT kartları + işlem çubuğu) · **İşlem Günlüğü** |
| **Hız Testi** | **Hız Testi** (BAŞLAT, seçili sunucu, canlı gösterge, indirme / yükleme, ping, titreşim, paket kaybı, kullanım uygunluğu, ISS ve sunucu, Ookla sonuç sayfası) · **Sunucu** (Speedtest by Ookla sunucu listesi: Otomatik Seç, arama, en yakın sunucular; veya Cloudflare) · **Sonuçlar** (geçmiş) · **Yöntem** (nasıl ölçüldüğü) |
| **Genel Ayarlar** | **Kolay Ayar** (açık / kapalı anahtarları: Windows bildirimleri, "Tümünü Güncelle ile Çöp Kutusu'nu boşalt") · **Yönetici Yetkisi** (gerçek durum + yeniden başlatma) · **Günlük Dosyaları** (oturum günlüğü: aç / klasör / dışa aktar; uygulama veri klasörü) |
| **Özet** | **Sağlık Özeti** (10 kartın durumu + Son İşlem) · **Son İşlemler** · **Bulunan Güncellemeler** · **İşlem Günlüğü** |
| **Cihaz Bilgileri** | **Cihaz Bilgileri** (İşlemci / Ekran Kartı / Bellek / Depolama / İşletim Sistemi kartları + Uyumluluk) · **Cihaz Durumu** (canlı kullanım ve sıcaklık göstergeleri) · **Hakkında** |

- Kart bölmelerinin üstündeki **işlem çubuğunda** Tümünü / Seçilenleri Kontrol Et, Tümünü / Seçilenleri Güncelle, İptal, seçim durumu
  ("N işlem seçildi", Seçimi Temizle) ve uygulanabilir işlem özeti bulunur; bu butonlar kategoriden bağımsız olarak tüm kartlar /
  seçilen kartlar için çalışır. Seçimler kategoriler arasında korunur.
- **İlerleme** kartı Güncelleme, Temizleme, Cihaz Sağlık ve Özet ekranlarının sol menüsünde, bölmelerin altındadır.
- Ana sayfanın altında **Tümünü Kontrol Et / Tümünü Güncelle** ve işlem durumu (gerçek adım ve yüzde) gösterilir.
- İşlem Günlüğü, Bulunan Güncellemeler ve Son İşlemler tam boy bölmelerdir (eski alt panel ve sekmeler bölmelere taşındı; içerik aynı).
  Günlükteki ve Son İşlem kartındaki "Son İşlemler" / "Geçmiş" butonu Özet → Son İşlemler bölmesini açar.
- Animasyonlar kısa ve tek seferliktir (ekran geçişi, kart üzerine gelme); sürekli animasyon yalnızca bir işlem gerçekten sürerken çalışır.

### Hız Testi

**BAŞLAT** düğmesi internet bağlantısını gerçek ölçümle test eder. Test sürerken gösterge (0 · 5 · 10 · 50 · 100 · 250 · 500 · 750 ·
1000 Mbps) gerçek anlık hızı gösterir; biten aşamanın sonucu hemen yazılır. BAŞLAT'ın altında seçili sunucu ve **Sunucuyu değiştir**
bağlantısı bulunur.

**Sunucu bölmesi** – iki ölçüm altyapısından biri seçilir:

| | Speedtest by Ookla | Cloudflare (varsayılan) |
|---|---|---|
| Sunucu | Ookla'nın Speedtest ağı: ISS ve veri merkezlerinin kendi sunucuları. **Size en yakın sunucular** listesinden seçilir (ör. Beyoğlu - Turkcell, İstanbul - Turknet, İstanbul - Türksat Kablonet); **Otomatik Seç** ile Ookla seçer. Liste içinde şehir / sağlayıcı / sunucu no ile arama yapılabilir (Türkçe harf ve büyük/küçük harf duyarsız) | Cloudflare'in herkese açık hız testi altyapısı (speed.cloudflare.com). Seçilemez: bağlantı otomatik olarak bir Cloudflare veri merkezine gider (bu bağlantıda Amsterdam, AMS) |
| Gereken | Ookla'nın resmi komut satırı aracı (**Speedtest CLI**, winget paketi `Ookla.Speedtest.CLI`). Uygulama yalnızca onayınızla kurar; ardından Ookla'nın lisans / kullanım / gizlilik koşullarını uygulamada kabul etmeniz gerekir | Kurulum yok |
| Ölçüm | Aracın kendi yöntemi; uygulama aracın JSON çıktısındaki ping, titreşim, paket kaybı, indirme / yükleme ve yük altındaki gecikmeyi (IQM) olduğu gibi gösterir | Aşağıdaki tablo |
| Veri paylaşımı | Ookla her testin sonucunu, IP adresini ve bağlantı bilgilerini kendi koşullarına göre saklar ve bir **sonuç sayfası** üretir (ekranda "Sonuç sayfasını aç") | Sonuçlar hiçbir yere gönderilmez |
| Süre / veri | ≈ 30-40 sn, 60 Mbps'lik bağlantıda ≈ 105-120 MB | ≈ 25 sn, ≈ 55-95 MB |

- **Ookla aracının kurulumu:** `winget install --id Ookla.Speedtest.CLI --exact --source winget --scope user` (yaklaşık 1 MB, yönetici yetkisi
  gerekmez, `%LOCALAPPDATA%\Microsoft\WinGet\Packages`). Başarı yalnızca paket winget'in kurulu paketler listesinde görünür **ve**
  `speedtest.exe --version` "Speedtest by Ookla" döndürürse kabul edilir (aynı adlı başka araçlar kabul edilmez). Ookla'nın Windows aracı
  **dijital olarak imzalı değildir**; güven, winget'in paket bildirimindeki SHA256 özet doğrulamasına dayanır (indirme adresi
  install.speedtest.net). Kaldırmak için: `winget uninstall Ookla.Speedtest.CLI`.
- **Lisans:** araç ve ürettiği bilgiler Ookla'nın koşullarına göre **yalnızca kişisel, ticari olmayan kullanım** içindir. Koşullar kabul
  edilmeden araç çalıştırılmaz; kabul, Sunucu bölmesinden **geri alınabilir** (bu durumda Cloudflare ile test yapılabilir).
- Aracın çıktısındaki yerel IP ve MAC adresi okunmaz, gösterilmez ve günlüğe yazılmaz.

**Cloudflare ölçüm yöntemi:**

| Değer | Nasıl ölçülür |
|---|---|
| ISS, IP, konum | Sunucunun bağlantı bilgisi yanıtı (`/meta`): ISS (AS numarasıyla), genel IP, şehir / ülke |
| Ping, titreşim | TCP bağlantı süresi (SYN → SYN-ACK, 443); boşta 20 ölçümün medyanı. Titreşim: ardışık ölçümler arasındaki farkların ortalaması. İndirme ve yükleme sırasında 0,5 sn'de bir ölçülen değerler **yük altındaki gecikme**dir |
| Paket kaybı | 50 ICMP yankı isteği (ping), 1 sn içinde yanıt gelmeyenlerin oranı. Hiç yanıt yoksa (ICMP engellenmiş olabilir) oran hesaplanmaz, "Ölçülemedi" yazılır |
| İndirme / yükleme | Her aşama 10 sn; **Çoklu** = 6, **Tek bağlantı** = 1 TCP bağlantısı. İlk 2 sn (bağlantının hızlanması) hesaba katılmaz; sonuç kalan 8 sn'de gerçekten aktarılan bayt ÷ süre. Yükleme rastgele veriyle yapılır |

- **Kullanım uygunluğu** (her iki altyapıda): web, oyun, video, görüntülü görüşme için 1-5 puan; ölçülen değerlerden sabit eşiklerle
  (eşikler simgenin araç ipucunda).
- Sunucu uzaktaysa ping yükselir: bu bağlantıda Cloudflare Amsterdam ≈ 43 ms, Ookla Turkcell Beyoğlu ≈ 3-4 ms.
- Ölçülemeyen değer uydurulmaz: "—" / "Ölçülemedi" ve gerçek neden gösterilir; sunucuya ulaşılamazsa veya araç hata bildirirse test
  "başarısız" + gerçek hata mesajıyla biter (ör. "Configuration - No servers defined").
- Windows bağlantıyı tarifeli bildiriyorsa test öncesinde onay istenir. Sonuç geçmişi (Sonuçlar bölmesi, en fazla 50; altyapı, sunucu ve
  varsa Ookla sonuç sayfasıyla) yalnızca bu bilgisayarda `state.json` içinde saklanır ve IP adresi içermez.
- Kontrol veya güncelleme işlemi sürerken test veya Ookla kurulumu başlatılamaz; bunlar sürerken de sistem işlemleri başlatılamaz.
  Hız testi yönetici yetkisi gerektirmez. İptal edilen test geçmişe yazılmaz (Ookla aracı iptalde sonlandırılır).

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
- Yönetici yetkisi gerektirmeyenler normal çalışır: **Hız Testi**, **Cihaz Bilgileri** (Cihaz Durumu'nda disk sıcaklığı yönetici
  yetkisi gerektirir; yetki yoksa "okunamıyor" ve nedeni yazar), ana sayfa araması ve Genel Ayarlar.
- **"Yönetici olarak yeniden başlat"** (ana sayfa, kategori sol menüsü, Genel Ayarlar → Yönetici Yetkisi) uygulamayı UAC onayıyla
  yönetici olarak yeniden açar. Yetki yalnızca bu yolla alınır; UAC hiçbir şekilde atlatılmaz.

Günlük dosyaları: `%LOCALAPPDATA%\E-mre Control Center\Logs\` (arayüzde İşlem Günlüğü bölmesindeki "Log dosyası" ve Genel Ayarlar → Günlük Dosyaları). Önceki sürümlerin günlükleri
eski klasörlerde kalır (`%LOCALAPPDATA%\E-mre Hub\Logs\`, `%LOCALAPPDATA%\RTX Windows Updater\Logs\`; silinmez).

### Sistem Sağlık Özeti, Sistem Bilgileri ve işlem geçmişi

- **Sistem Sağlık Özeti** (Özet kategorisinde) yalnızca kartların bu oturumdaki GERÇEK durumlarından hesaplanır:
  çalıştırılmamış kartlar "Kontrol edilmedi" olarak kalır ve sağlıklı sayılmaz; hata ve bekleyen güncellemeler gizlenmez.
  Genel durum, sorunsuz / güncelleme-uyarı / hata / çalıştırılmadı (varsa kullanım dışı) sayılarıyla birlikte gösterilir.
- **Son İşlem**: her işlem bitince gerçek sonuçlardan hesaplanan kısa özet. Kontrol: "10 kontrol tamamlandı · 3 güncelleme
  bulundu (Winget 2, Microsoft Defender 1) · 1 manuel güncelleme (otomatik uygulanmaz) · 1 uyarı · 0 hata · …".
  Güncelleme: "4 işlem · 2 başarılı · 1 uyarı · 1 hata · Winget: 2 paket güncellenemedi · Süre: 21 sn".
  Tüm işlemler Özet → "Son İşlemler" bölmesinde listelenir (son 50).
- **Cihaz Bilgileri** (Cihaz Bilgileri kategorisi → Cihaz Bilgileri): solda büyük donanım ikonu, sağda bilgi kartı:
  **İşlemci** (model, çekirdek / iş parçacığı, temel saat hızı), **Ekran Kartı** (tüm kartlar, NVIDIA modeli ve sürücüsü, ekran kartı belleği),
  **Bellek** (toplam / kullanılabilir, modüller: adet × boyut, üretici / parça no, hız), **Depolama** (fiziksel diskler: model · SSD/HDD ·
  NVMe/SATA · boyut; sistem sürücüsü, boş / toplam alan), **İşletim Sistemi** (sürüm, build, mimari, bilgisayar adı, çalışma süresi) ve
  **Uyumluluk** (Windows 11, NVIDIA RTX, yönetici). Kaynaklar: WMI (Win32_Processor, Win32_PhysicalMemory, Win32_VideoController,
  MSFT_PhysicalDisk), kayıt defteri (ekran kartı belleği: HardwareInformation.qwMemorySize), nvidia-smi, DriveInfo. Alınamayan alan "Bilgi alınamadı" yazar
  ve nedeni günlüğe düşer (NVIDIA kartı olmayan sistemde NVIDIA alanları okuma hatası değil "NVIDIA ekran kartı yok"
  olarak gösterilir). Değişmeyen bilgiler (işletim sistemi, CPU, RAM, GPU) oturumda bir kez okunur; "Yenile" butonu
  yalnızca sürücü sürümü, disk alanı ve çalışma süresini yeniden okur. Ekran kartı listesi (WMI) gereksinim kontrolü,
  Sistem Bilgileri ve NVIDIA kartı arasında paylaşılır; yönetici yetkisi açılışta bir kez doğrulanır.
- **Cihaz Durumu** (canlı): halka göstergeler; yalnızca bu bölme açıkken 2 saniyede bir ölçülür; başka bölmeye veya ana sayfaya geçince durur.

  | Gösterge | Kaynak |
  |---|---|
  | İşlemci kullanımı | Windows `GetSystemTimes` (iki ölçüm arasındaki boşta / toplam süre farkı) |
  | Termal bölge | Windows'un ACPI termal bölge sayacı (Thermal Zone Information). **İşlemci çekirdek sıcaklığı değildir**; Windows standart bir CPU sıcaklığı arayüzü sunmaz |
  | Ekran kartı kullanımı / sıcaklığı / belleği | NVIDIA sürücüsüyle gelen resmi NVML kitaplığı (`nvml.dll`); yalnızca NVIDIA kartlarda |
  | Bellek kullanımı | Windows `GlobalMemoryStatusEx` |
  | Disk sıcaklığı | Windows depolama güvenilirlik sayaçları (`MSFT_StorageReliabilityCounter`); yönetici yetkisi gerekir, 15 saniyede bir |
  | Fan hızı | NVML (ekran kartı fanı) ve `Win32_Fan`; çoğu dizüstü bunları bildirmez |

  Okunamayan değer tahmin edilmez: gösterge "—" ve "okunamıyor" yazar, nedeni üzerine gelince görünür (ör. "Disk sıcaklığı yönetici
  yetkisi gerektirir", "Bu ekran kartı fan hızını bildirmiyor"). Sıcaklık 80 °C ve üstünde turuncu, 90 °C ve üstünde kırmızı gösterilir.
- **Hakkında**: sürüm, dil, veri klasörü, günlük dosyası, kaynak kod adresi.- **Son çalıştırılma**: her kartta "Son kontrol / Son tarama / Son güncelleme: tarih saat" (gerçek bitiş zamanı).
  Önceki oturumdan kalan sonuç "önceki oturum" olarak işaretlenir; kartın bu oturumdaki durumu yine "çalıştırılmadı" kalır.
- **Detaylı Sonuç** (kartın liste ikonlu butonu): işlem türü, bitiş zamanı, çalışma süresi, çalıştırılan her komut
  (komut satırı, Exit Code, süre, stdout, stderr), Windows API / NVIDIA / Microsoft servis yanıtları ve öğe tablosu.
  Elde edilmeyen bilgi "Bilgi alınamadı" olarak gösterilir.
- Geçmiş dosyası: `%LOCALAPPDATA%\E-mre Control Center\state.json`. Yeni klasörde geçmiş yoksa önceki sürümün geçmişi
  (önce `%LOCALAPPDATA%\E-mre Hub\state.json`, o yoksa `%LOCALAPPDATA%\RTX Windows Updater\state.json`) ilk açılışta bir kez
  kopyalanır; eski dosyalar silinmez.

### Windows bildirimleri

- Uzun süren işlemler bitince (SFC, MRT, toplu kontrol/güncelleme; 30 sn'yi aşan tek kart işlemleri) Windows bildirimi
  gönderilir. Metin gerçek sonuçlardan hesaplanır.
- Genel Ayarlar → Kolay Ayar'daki "Windows bildirimleri" anahtarıyla veya Windows Ayarları → Sistem → Bildirimler'den kapatılabilir.
- Paketlenmemiş masaüstü uygulamaları için Microsoft'un yöntemiyle, uygulama kimliği yalnızca geçerli kullanıcı için
  `HKCU\Software\Classes\AppUserModelId\E-mre.RTXWindowsUpdater` altına (görünen ad + ikon) kaydedilir.

### Log Yönetimi

- Günlük satırları `[saat] [INFO|SUCCESS|WARNING|ERROR] mesaj` biçimindedir; dosyaya arka planda yazılır (arayüz beklemez).
- Filtreler (Tümü / Bilgi / Başarılı / Uyarı / Hata) yalnızca görünümü değiştirir; kayıtlar silinmez.
- "Logları Temizle" yalnızca ekranı temizler, günlük dosyası korunur. "Dışa Aktar" gerçek oturum günlüğünü seçtiğiniz konuma
  `E-mre-Control-Center-Log-YYYY-AA-GG.txt` olarak kopyalar. "Log dosyası" dosyayı, "Klasör" günlük klasörünü açar.

## 8. Bilinen sınırlamalar

- **İşlemci sıcaklığı ve sistem fan hızı** Windows'un standart arayüzlerinde yoktur; bu değerleri üretici yazılımları (ör. dizüstü
  kontrol merkezleri) kendi sürücüleriyle okur. E-mre Control Center sürücü kurmaz: Cihaz Durumu'nda işlemci için ACPI termal bölge
  sıcaklığını (bildiriliyorsa) gösterir, fan hızını bildirilmiyorsa "okunamıyor" olarak açıklar.
- Cihaz Durumu açıkken NVIDIA ekran kartı değerleri okunduğu için Optimus dizüstülerde NVIDIA kartı uyanık kalır; bölme kapanınca
  NVML serbest bırakılır ve kart yeniden uyku durumuna geçebilir. Kart uykudan yeni uyanırken ilk ölçümde kullanım okunamayabilir.
- **Hız Testi:** Cloudflare'de sunucu seçilemez (veri merkezini ISS'nin yönlendirmesi belirler). Ookla aracı yalnızca en yakın ~12
  sunucuyu listeler; speedtest.net'teki daha uzun listede görünen bazı sunucular (ör. başka şehirlerdeki ISS sunucuları) bu listede
  olmayabilir ve arama yalnızca bu liste içinde yapılır. Ookla şehir adlarını Türkçe harfsiz verir (Beyoglu, Istanbul); olduğu gibi
  gösterilir. Ookla aracı ve ürettiği bilgiler Ookla'nın koşullarına göre yalnızca kişisel, ticari olmayan kullanım içindir; Ookla'nın
  Windows aracı dijital olarak imzalı değildir (winget özet doğrulamasıyla kurulur).

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
- **Kurulum:** kurulum dosyası da imzasızdır (SmartScreen ve UAC "Bilinmeyen yayıncı"). Kurulum tüm kullanıcılar için Program Files'a
  yapılır ve yönetici izni gerektirir; kullanıcı başına (izinsiz) kurulum yoktur. Kaldırmada kullanımdaki dosyalar (ör. açık kalan
  bir kopyanın geçici dosyaları) ve kaldırıcının geçici kopyası bilgisayar yeniden başlatılınca silinir. "Ayarlarımı… sil" seçeneği,
  UAC'yi onaylayan hesabın `%LOCALAPPDATA%` klasörünü siler (standart kullanıcı + başka bir yönetici parolasıyla kaldırılırsa o
  yöneticinin klasörü).

## Sürüm geçmişi

### v1.7.3

**Yeni**
- Uygulama içi güncellemeden sonra yeni sürüm ilk açıldığında "Güncelleme tamamlandı: vX → vY" bilgisi gösterilir (ayarlar,
  geçmiş ve günlükler korunur). Bilgi yalnızca kurulumun ilettiği önceki sürüm geçerli ve daha eskiyse gösterilir.

**Not**
- Uygulama içi güncellemenin gerçek denemesi için yayınlanan sürümdür: v1.7.2 yüklü bilgisayarlarda uygulama açılınca
  "Yeni sürüm yayınlandı · v1.7.3" penceresi gelir; Güncelle ile indirilir, doğrulanır, kurulur ve yeni sürüm kendiliğinden açılır.

### v1.7.2

**Düzeltmeler**
- Uygulama içi güncelleme artık herkese açık ana depoyu (E-mre-Hub/E-mre-Control-Center) denetler. v1.7.0 ve v1.7.1, hiç oluşturulmamış
  ayrı bir sürüm deposuna baktığı için "Denetlenemedi (HTTP 404)" gösteriyor ve yeni sürümü göremiyordu.
- Güncelleme penceresinde yalnızca README'deki sürüm notu görünür; GitHub'ın otomatik "Full Changelog" bağlantısı eklenmez.
- Hakkında → Kaynak kod: depo artık herkese açık.

**Not**
- v1.7.0 veya v1.7.1 yüklüyse bu sürüm bir kez elle kurulmalıdır: Releases → E-mre-Control-Center-Setup-v1.7.2.exe → Güncelle.
  Sonraki sürümler uygulama içinden gelir.

### v1.7.1

**İyileştirmeler**
- Uygulama içi güncellemeden sonra yeni sürüm doğrudan Kontrol Merkezi'nde açılır (giriş ekranı yeniden sorulmaz; Windows 11
  denetimi yine yapılır).
- Hakkında → Güncelleme satırı sadeleşti: "Güncel (son yayın vX.Y.Z) · son denetim SS:DD".

**Düzeltmeler**
- "Güncelleme denetlenemedi" satırı işlem günlüğüne iki kez yazılıyordu; artık bir kez (uyarı olarak) yazılır.

**Not**
- Uygulama içi güncellemenin ilk gerçek sürümüdür: v1.7.0 yüklü bilgisayarlarda uygulama açılınca "Yeni sürüm yayınlandı" penceresi
  gelir; Güncelle ile indirilir, doğrulanır, kurulur ve yeni sürüm kendiliğinden açılır.

### v1.7.0

**Yeni**
- **Kurulum programı:** Releases'ta `E-mre-Control-Center-Setup-vX.Y.Z.exe`. Çift tıklayınca uygulamanın kendi tasarımında kurulum
  ekranı açılır: Yükle → UAC → `C:\Program Files\E-mre Control Center`, Başlat menüsü kısayolu, isteğe bağlı masaüstü kısayolu ve
  Windows Ayarlar → Uygulamalar kaydı. Her adım doğrulanır (SHA-256, kısayol hedefi, kayıt); başarısız kurulum geri alınır. Son ekran:
  "Bizi tercih ettiğiniz için teşekkürler! Aramıza hoş geldin." Yeni sürüm aynı dosyayla güncellenir (ayarlar ve geçmiş korunur).
- **Windows'tan kaldırma:** Ayarlar → Uygulamalar → Kaldır → "Bir dahaki sefere görüşmek üzere!" ekranı, "Neden kaldırıyorsunuz?"
  (Sevmedim, Kasıyor / yavaş çalışıyor, Hata veriyor, Artık ihtiyacım yok, Diğer) + mesaj, **Gönder ve Kaldır** / **İptal**.
  Ayarları, geçmişi ve günlükleri silmek isteğe bağlıdır. Geri bildirim Google Formuna gönderilir (yanıtlar Google E-Tablolar'da);
  gönderilemezse kaldırma yine yapılır ve gerçek hata + "Tekrar gönder" gösterilir.
- **Uygulama içi güncelleme (zorunlu):** açılışta herkese açık sürüm deposu denetlenir; yeni sürüm varsa "Yeni sürüm yayınlandı"
  penceresi (sürüm notlarıyla) gelir ve uygulama güncellenmeden kullanılamaz. Güncelle → indirme (gerçek ilerleme) → boyut + SHA-256 +
  ürün adı + sürüm doğrulaması → kurulum → yeni sürüm kendiliğinden açılır. Denetlenemezse uygulama normal açılır, neden Hakkında'da yazar.
- Hakkında bölmesinde **Kurulum** (yüklü / taşınabilir) ve **Güncelleme** (son denetim sonucu + "Şimdi denetle") satırları.

**Güvenlik**
- Kaldırma, `C:\ProgramData` altında yalnızca Yöneticiler ve SYSTEM erişimli geçici bir kopyadan çalışır (yerel kitaplıkları da orada);
  yalnızca bu uygulamanın bilinen yolları silinir, klasör bağlantıları izlenmez, başka programların dosyaları / kısayolları bırakılır.
- Çalışan uygulama kurulum veya kaldırma için zorla kapatılmaz (yalnızca normal kapanma isteği).

- İndirilen güncelleme doğrulanmadan çalıştırılmaz; yönetici olarak çalışırken yalnızca Yöneticiler + SYSTEM erişimli klasöre indirilir.

**Dağıtım**
- GitHub Actions her sürümde Setup EXE'sini ve taşınabilir ZIP'i birlikte yayınlar; `tools\Build-Exe.ps1` yerelde
  `E-mre Control Center Setup.exe` dosyasını da üretir (aynı EXE).
- Sürüm notları README'deki sürüm geçmişinden alınır; kurulum dosyası ve notlar herkese açık sürüm deposuna
  (`E-mre-Hub/E-mre-Control-Center-Releases`) da yayınlanır (kaynak kod özel kalır; `RELEASES_TOKEN` gerekir) – bu depo hiç oluşturulmadı; v1.7.2 ana depoyu kullanır.

### v1.6.0

**Yeni**
- **Ana sayfada Ara kutusu** (Ctrl+F): kategori, bölme ve kart adlarında / açıklamalarında arar; büyük / küçük harf ve Türkçe karakter
  duyarsız ("gunluk" = "günlük", "dism" → Cihaz Sağlık → Sağlık Araçları). Enter ilk sonucu açar, Aşağı ok sonuçlara geçer, Esc
  temizler. Yalnızca gezinmedir, işlem başlatmaz.

**İyileştirmeler (göz yormayan, daha net ana sayfa)**
- Zemin daha koyu (neredeyse siyah; üst ortada hafif lacivert aydınlık); kartların zemini de koyulaştırıldı.
- Önemli öğeler parlak neon: logo çevresinde ışıma, parlayan kategori simgeleri, neon çerçeveli arama kutusu, "KONTROL MERKEZİ" yazısı.
- Kategoriler çerçevesiz ve sade: kalın beyaz başlık, okunaklı açıklama, durum noktası + durum metni (uzun durum metni artık kırpılmaz,
  iki satıra sarılır); üzerine gelince hafif aydınlanma, klavye odağında neon çerçeve.
- İkincil metinler daha açık ve okunaklı (açıklama ve soluk metin renkleri açıldı; kontrast arttı).
- Ana sayfanın alt kısmında ince dalga çizgileri (hareketsiz, önbellekli; boşta arayüz CPU'su ~%0,3).

### v1.5.0

**Yeni**
- **Hız Testi kategorisi** (ana sayfada 4. kutucuk; ana sayfa artık 7 kategori: üstte 4, altta ortalı 3). Gerçek ölçümle indirme /
  yükleme, ping (boşta + indirme / yükleme sırasında), titreşim, paket kaybı, ISS / IP / konum, test sunucusu, canlı gösterge
  (0-1000 Mbps), kullanım uygunluğu (web, oyun, video, görüntülü görüşme), sonuç geçmişi (Sonuçlar bölmesi; IP'siz, en fazla 50) ve
  Yöntem bölmesi. Tarifeli bağlantıda test öncesinde onay istenir. Yönetici yetkisi gerektirmez.
- **Sunucu seçimi (Sunucu bölmesi):** Speedtest by Ookla sunucu listesi – seçili sunucu, **Otomatik Seç**, arama, **Size en yakın
  sunucular** (ör. Beyoğlu - Turkcell, İstanbul - Turknet). Ookla'nın resmi aracı (Speedtest CLI) yalnızca onayla winget'ten kurulur;
  Ookla'nın koşulları uygulamada kabul edilir ve geri alınabilir; test sonrası Ookla sonuç sayfası açılabilir. Alternatif olarak
  kurulum gerektirmeyen ve sonuç göndermeyen **Cloudflare** altyapısı (çoklu / tek bağlantı; varsayılan).
- Hız testi sürerken (veya Ookla aracı kurulurken) kontrol / güncelleme işlemleri başlatılamaz; sistem işlemi sürerken de test
  başlatılamaz (ölçümü etkilememesi için).

**İyileştirmeler**
- Cihaz Durumu bölmesinin simgesi grafik simgesi oldu (hız göstergesi simgesi artık Hız Testi'nde).
- Hız göstergesi sürekli animasyon kullanmaz (değer saniyede 10 kez gerçek ölçümden çizilir); test sürerken arayüz CPU'su ~%12-13
  (tek çekirdek; bunun ~%5'i ağ aktarımı).

### v1.4.0

**Yeni**
- **Yeni ad: E-mre Control Center.** EXE `E-mre Control Center.exe`, Releases paketi `E-mre-Control-Center-vX.Y.Z.zip`,
  EXE bilgileri (Şirket / Ürün / Açıklama / Telif) "E-mre Control Center", depo https://github.com/E-mre-Hub/E-mre-Control-Center.
- Veriler `%LOCALAPPDATA%\E-mre Control Center` altında. İlk açılışta en yeni eski geçmiş (E-mre Hub, o yoksa
  RTX Windows Updater) bir kez kopyalanır; eski klasörler ve günlükler silinmez.
- **Kontrol Merkezi arayüzü:** ana sayfada 6 kategori (3x2, büyük ikonlu kartlar, kısa açıklama ve mevcut verilerden durum satırı):
  Güncelleme, Temizleme, Cihaz Sağlık, Genel Ayarlar, Özet, Cihaz Bilgileri. Ana sayfada "Tümünü Kontrol Et / Tümünü Güncelle" ve
  işlem durumu. Mevcut kartlar, komutlar ve kontrol / güncelleme mantığı aynen korunur; yalnızca düzen değişti. Giriş (gereksinim) sayfası aynı.
- **Bölmeli kategori ekranları** (Monster Kontrol Merkezi düzeni, tüm kategorilerde aynı): solda "Ana Sayfa" (veya Esc), kategori
  başlığı ve bölmeler (alt menü), sağda seçili bölmenin başlığı ve içeriği. Kartlar ve ayarlar solda büyük ikon, sağda geniş kart
  satırları olarak gösterilir. Eski alt paneldeki İşlem Günlüğü, Bulunan Güncellemeler ve Son İşlemler sekmeleri tam boy bölmelere
  taşındı; toplu işlem butonları kart bölmelerinin üstündeki işlem çubuğunda, İlerleme sol menüde. Genel Ayarlar: Kolay Ayar
  (açık / kapalı anahtarları), Yönetici Yetkisi, Günlük Dosyaları. Hiçbir özellik kaldırılmadı.
- **Cihaz Bilgileri kategorisi**: Cihaz Bilgileri / Cihaz Durumu / Hakkında bölmeleri. Donanım
  kartları (İşlemci, Ekran Kartı, Bellek, Depolama, İşletim Sistemi, Uyumluluk); yeni bilgiler: temel saat hızı, bellek modülleri ve hızı,
  ekran kartı belleği, disk modeli / SSD-HDD / NVMe-SATA. **Cihaz Durumu**: canlı halka göstergeler (işlemci kullanımı, ACPI termal bölge,
  NVIDIA kullanım / sıcaklık / bellek, bellek kullanımı, disk sıcaklığı, fan); okunamayan değer "okunamıyor" + gerçek neden.

**Performans**
- Yalnızca açık kategorinin seçili bölmesi çizilir; sık değişen listeler (günlük, tablolar) gölgesi ayrı statik katmanda olan
  kartlardadır. Ölçüm (tek çekirdek): boşta ~%0, işlem sürerken ~%5, Cihaz Durumu açıkken %0,4–3,5.

### v1.3.1

**İyileştirmeler**
- EXE bilgileri (Özellikler → Ayrıntılar: Şirket / Ürün / Açıklama / Telif) "E-mre Hub"; ürün sürümü yalın (`1.3.1`,
  git kimliği eklenmez).
- Derleme betiği isteğe bağlı kod imzalamaya hazır (`-SignThumbprint`: SHA-256 + zaman damgası + imza doğrulaması; imza
  doğrulanamazsa EXE üretilmez). UAC'deki "Yayıncı: Bilinmeyen" yalnızca kod imzalama sertifikasıyla düzelir (bkz. Kod imzalama).
- Depo **E-mre Hub** organizasyonuna taşındı: https://github.com/E-mre-Hub/E-mre_Hub (sürümler ve Releases burada yayınlanır).

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
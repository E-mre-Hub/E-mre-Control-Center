# Sürüm geçmişi

E-mre Control Center'ın tüm sürümleri ve değişiklikleri. İndirmek için: [Releases](https://github.com/E-mre-Hub/E-mre-Control-Center/releases).

> Bir sürüm etiketi (`vX.Y.Z`) gönderildiğinde GitHub Actions bu dosyadaki `### vX.Y.Z` bölümünü yayın notu olarak kullanır;
> uygulama içi güncelleme penceresi de aynı metni gösterir. Bölüm etiketten önce yazılmalıdır.
> v2.0.0'dan itibaren bölüm iki dillidir: `**English**` satırından öncesi Türkçe, sonrası İngilizce; güncelleme penceresi seçili
> dildekini gösterir.

### v2.0.0

**Yeni: Türkçe ve İngilizce (English)**
- Uygulamanın tamamı iki dilde: tüm ekranlar, kartlar, onay pencereleri, işlem günlüğü, Detaylı Sonuç, Sistem Raporu, Destek
  Paketi, bildirim alanı menüsü, kurulum ve kaldırma ekranları.
- İlk açılışta "Dil ve görünüm" ekranı gelir: Türkçe veya English seçilir (Windows dili önerilir). Önceki sürümden güncelleyenler de
  bu ekranı bir kez görür.
- Dil sonradan Genel Ayarlar → Kolay Ayar'dan değiştirilebilir; uygulama onayınızla yeniden başlatılır, ayarlar, geçmiş ve günlükler
  korunur. Kontrol, güncelleme veya hız testi sürerken dil değiştirilmez.
- İki kurulum dosyası: `E-mre-Control-Center-Setup-TR-vX.Y.Z.exe` (Türkçe) ve `E-mre-Control-Center-Setup-EN-vX.Y.Z.exe` (English).
  İkisi aynı programdır; yalnızca kurulum ekranının dili farklıdır ve seçilen dil kurulan uygulamaya iletilir. Adında dil işareti
  olmayan `E-mre-Control-Center-Setup-vX.Y.Z.exe` aynı dosyadır; yalnızca eski sürümlerin otomatik güncellemesi için tutulur.

**Yeni: Koyu ve açık tema**
- Koyu tema (varsayılan, alışılmış görünüm) ve yeni açık tema (beyaz ve açık gri). İlk açılış ekranından veya Genel Ayarlar →
  Kolay Ayar → Görünüm'den seçilir; değişiklik anında uygulanır.
- Kurulum ve kaldırma pencerelerinin başlık çubuğunda tema düğmesi vardır; kurulumda seçilen tema uygulamaya iletilir.

**Diğer**
- Uygulama içi güncelleme penceresi sürüm notunu seçili dilde gösterir.
- İngilizcede tarih, sayı ve yüzde biçimleri İngilizceye uygundur (ör. 2026-10-07, 45%).

**English**

**New: English and Turkish**
- The whole app is available in two languages: every screen, card, confirmation dialog, the operation log, Detailed Result, System
  Report, Support Package, the notification area menu and the setup and uninstall screens.
- On first start a "Language and appearance" screen appears: choose English or Türkçe (the Windows language is suggested). Users
  updating from an earlier version also see this screen once.
- You can change the language later in General Settings → Quick Settings; the app restarts with your approval and your settings,
  history and logs are kept. The language cannot be changed while a check, update or speed test is running.
- Two setup files: `E-mre-Control-Center-Setup-EN-vX.Y.Z.exe` (English) and `E-mre-Control-Center-Setup-TR-vX.Y.Z.exe` (Turkish).
  Both are the same program; only the language of the setup screens differs, and the chosen language is passed to the installed app.
  `E-mre-Control-Center-Setup-vX.Y.Z.exe` (no language mark) is the same file, kept only for the automatic update of older versions.

**New: Dark and light theme**
- Dark theme (default, the familiar look) and a new light theme (white and light gray). Choose it on the first-start screen or in
  General Settings → Quick Settings → Appearance; the change applies immediately.
- The setup and uninstall windows have a theme button in the title bar; the theme chosen during setup is passed to the app.

**Other**
- The in-app update window shows the release notes in the selected language.
- In English, dates, numbers and percentages use English formats (e.g. 2026-10-07, 45%).

### v1.9.3

**Düzeltme: Google Play Games her kontrolde yeniden "güncelleme var / doğrulanamadı"**
- Winget, Google Play Games için 156.0.8067.0 sürümünü listeliyor; bu numara uygulamanın değil Google güncelleyicisinin sürümü.
  Uygulamanın kendi sürümü (26.9.555.1) Google'ın güncelleme kaydında da aynı: uygulama güncel, winget kataloğu yanlış numara gösteriyor.
  Kurulum kaç kez çalıştırılırsa çalıştırılsın sürüm değişmeyeceği için v1.9.2'deki "manuel yeniden dene" seçeneği işe yaramıyordu.
- Artık winget'in başarı bildirdiği ama kurulu sürümü hiç değiştirmeyen bir paket, sonraki kontrollerde **güncelleme sayılmaz**:
  ne otomatik ne manuel sunulur; Winget kartında "Güncel (1 paket winget ile güncellenemiyor – bilgi)" yazar ve nedeni ayrıntıda
  açıklanır. Paketin kurulu ya da sunulan sürümü değişirse yeniden normal güncelleme olarak değerlendirilir.

### v1.9.2

**Düzeltme: Microsoft Defender tanımları güncellenemiyordu**
- Windows Update hata vermeden "yeni tanım yok" dediğinde, Microsoft'un yayımladığı daha yeni tanımlar olsa bile güncelleme
  "başarısız" bitiyordu: uygulama sonraki kaynağa yalnızca hata olunca geçiyordu. Artık sürüm değişmezse Microsoft'un resmi tanım
  sunucusu (MMPC) da denenir; günlükte yeni tanımın hangi kaynaktan geldiği yazar.

**Düzeltme: Hep "güncelleme var" görünen winget paketleri (ör. Google Play Games)**
- Bazı paketlerde winget kurulumun başarılı olduğunu bildirir ama uygulamanın kurulu sürümü hiç değişmez (winget kataloğundaki sürüm
  numarası uygulamanınkinden farklıdır; Google Play Games'te katalog 156.0.8067.0, uygulama 26.9.x). Bu paketler her Tümünü
  Güncelle'de yeniden kuruluyor ve 1 dakika boyunca doğrulanmaya çalışılıyordu.
- Artık böyle bir paket "kurulu sürüm değişmedi" olarak not edilir. Aynı sürüm bir dahaki kontrolde otomatik denenmez,
  "Otomatik uygulanmayan güncellemeler" listesinde "yeniden dene" seçeneğiyle sunulur. Paketin sürümü değişince normal güncellemeye döner.

**İyileştirme**
- Uygulama kapanırken günlüğe "Uygulama kapanıyor" satırı yazılır (Windows kapanırken de). Böylece günlükten uygulamanın normal mi
  kapandığı yoksa dışarıdan mı sonlandırıldığı anlaşılır.

### v1.9.1

**Düzeltme: Güncelleme sürerken ilerleme %0'da kalıyordu**
- Tümünü Güncelle'de Winget paketleri birer birer kurulurken (ör. 7 paket, 20-25 dakika) ilerleme çubuğu %0'da kalıyor ve uygulama
  donmuş gibi görünüyordu. Artık kartta ve ilerleme çubuğunda hangi paketin kurulduğu (ör. "5/7 · Visual Studio Code"), winget'in
  bildirdiği aşama (indiriliyor / kurulum dosyası doğrulandı / kuruluyor), winget yazıyorsa indirilen boyut, yazmıyorsa o aşamada
  geçen süre görünür. Yüzde, tamamlanan paket sayısından hesaplanır (tahmini veya uydurma yüzde yok).
- SFC, DISM, MRT ve geçici dosya adımlarının kendi ilerlemesi de genel ilerleme çubuğuna yansır.

**Düzeltme: İşlem sürerken çıkış / iptal**
- Güncelleme sürerken "Bitince kapat" veya İptal seçilince, kurulmakta olan paket yarıda kesilmez; ama sıradaki Winget paketleri artık
  başlatılmaz ve "atlandı (iptal edildi)" olarak raporlanır (eskiden kalan tüm paketlerin kurulması bekleniyordu). Atlanan paketler
  bir sonraki kontrolde yeniden sunulur.

### v1.9.0

**Yeni: Gizlilik Politikası ve Kullanım Koşulları (Türkçe + English)**
- Gizlilik Politikası (çerez bildirimi dahil) ile Kullanım Koşulları ve Lisans Sözleşmesi (garanti reddi, sorumluluk sınırı, üçüncü
  taraf yazılımlar) eklendi.
- Gereksinim ekranındaki kutu artık Kullanım Koşulları'nı ve Gizlilik Politikası'nı da kapsar. Belgeler kutunun altındaki
  bağlantılardan, kurulum ekranından ve Genel Ayarlar → Yasal ve Gizlilik bölümünden açılır. Kabul yalnızca bu bilgisayarda kaydedilir.
- Uygulama hesap istemez; telemetri, analitik, reklam ve çerez kullanmaz.

**Yeni: Günlük arşivi**
- Genel Ayarlar → Günlük Dosyaları'nda günlüklerin sayısı ve boyutu görünür; 30 günden eski günlükler onayla silinebilir.
- İsteğe bağlı "Açılışta 30 günden eski günlükleri otomatik sil" ayarı (varsayılan kapalı).

**Güvenlik**
- Yönetici olarak çalışırken winget yalnızca Windows'un korumalı paket klasöründen çalıştırılır, NVIDIA kurulum dosyası yalnızca
  yöneticilerin erişebildiği bir klasöre indirilir ve Speedtest by Ookla aracı yönetici yetkisi olmadan çalıştırılır.
- Bağlantılar, Windows Ayarlar sayfaları ve günlük dosyası yönetici yetkisi olmadan açılır.
- Yönetici olmadan indirilen güncelleme dosyası, kurulum başlayana kadar kilitli tutulur ve yeniden doğrulanır.

**Düzeltmeler**
- Geçici dosyalar: temizlik öncesi / sonrası ölçüm ve temizlik aynı 24 saat sınırını kullanır; sınırdaki dosyalar yüzünden yanlış
  "kısmen temizlendi" sonucu çıkmaz.
- Winget: kurulumu arka planda süren paketlerde (ör. Discord) doğrulama birkaç kez yeniden denenir; hemen "Doğrulanamadı" denmez.
- Çalışan uygulama tespiti: listelendikten sonra kapanan bir işlem artık "başka bir kullanıcı oturumunda" diye gösterilmez.
- Ayarlar ve işlem geçmişi uygulama kapanırken beklemeden diske yazılır; kapanıştan hemen önceki değişiklik kaybolmaz.

### v1.8.4

**Yeni: İnternet bağlantısı zorunlu**
- Gereksinim ekranına "İnternet bağlantısı" eklendi (Windows 11, NVIDIA RTX GPU, yönetici yetkisi ve internet). İnternet yoksa uygulamaya
  girilemez; "Tekrar dene" ile ya da bağlantı gelince kendiliğinden yeniden denetlenir.
- Kullanırken internet giderse "İnternet bağlantısı yok" ekranı uygulamayı kilitler; sürmekte olan işlem yarıda kesilmez, bağlantı
  gelince ekran kalkar ve kaldığınız yerden devam edersiniz.

**Düzeltme: İşlem sürerken çıkış**
- Kontrol sürerken çıkışta kontrol durdurulur ve araçların kapandığı beklenir; güncelleme / onarım sürerken kurulum yarıda kesilmez,
  o adım bitince uygulama kendiliğinden kapanır ("Bitince kapat"). Uyarı metni artık gerçek davranışı anlatıyor.

**Düzeltme: Çevrimdışı durumda yanlış "Güncel" yok**
- İnternet doğrulanamadığında winget sonucu "güncel görünüyor – internet doğrulanamadı" olarak işaretlenir.
- Defender tanımları Microsoft sunucusuyla karşılaştırılamazsa "Güncel (Defender'ın kendi bilgisine göre)" yazar ve nedeni gösterilir.

### v1.8.3

**Yeni: Güncelleme uygulama açıkken de gelir**
- Yeni sürüm yayınlanınca uygulamayı kapatıp açmak gerekmez: uygulama açıkken 5 dakikada bir denetler ve "Yeni sürüm yayınlandı"
  penceresi hangi sayfada olursanız olun ekrana gelir (güncelleme zorunludur). Sürmekte olan bir işlem varsa yarıda kesilmez; bitince gelir.
- Uygulama bildirim alanındayken Windows bildirimi gösterilir; sağ tık menüsünde yalnızca "Güncelleme var" ve "Çıkış" kalır.
- Denetim GitHub'a koşullu istekle yapılır (yayın değişmediyse istek sınırından düşmez).

**Düzeltme: Geçici dosyalar "kısmen temizlendi" uyarısı**
- Teslim En İyileştirme önbelleği: Windows silmeyi arka planda yaptığı için uygulama önbelleği hemen ölçüp "temizlenemedi
  (dosyalar kullanımda veya Windows silmedi)" diyordu; oysa dosyalar birkaç saniye içinde siliniyordu. Artık silinen dosyaların
  Windows kaydından çıkması en fazla 60 sn beklenir ve doğrulanır.
- Önbellek boşken "okunamadı" yerine Windows'un bildirdiği boyut (0 bayt → "Temizlenecek dosya yok") gösterilir.
- Kullanıcı / Windows geçici dosyaları: şu anda bir uygulamanın açık tuttuğu dosyalar temizlenebilir sayılmaz; kullanan
  uygulamanın adıyla "korunan (kullanımda – Microsoft OneDrive)" olarak gösterilir. Uygulamalar kapatılmaz.
- Eski tarihiyle Temp'e yeni taşınan dosyalar (ör. OneDrive) artık "eski" sayılıp silinmeye çalışılmaz (NTFS değişim zamanı).
- Microsoft Edge güncellemesi günlüğünde sunulan sürüm artık yazılıyor; önceki sürümden kalan Edge "kaldır + kur" kaydı Edge
  güncel olsa da temizlenir.

### v1.8.2

**Düzeltme: Microsoft Edge güncellemesi**
- Microsoft Edge (ve WebView2 Çalışma Zamanı) artık winget'in "kaldır + yeniden kur" yoluyla değil, Microsoft'un kendi
  güncelleyicisiyle (Microsoft Edge Update) güncellenir. Edge Windows bileşeni olduğu için kaldırılamıyordu (çıkış kodu 93) ve
  güncelleme her seferinde başarısız oluyordu.
- Kontrol, yeni sürümün bu cihaza gerçekten sunulup sunulmadığını Edge Update'e sorar; sunulmuyorsa nedeni yazılır.
- Güncelleme sonrası sürüm kayıt defterinden doğrulanır; Edge açıksa yeni sürümün Edge yeniden açılınca etkinleşeceği yazılır.
- Detaylı Sonuç'ta paket mesajı etiketi "Araç mesajı" oldu (Edge Update mesajları da gösterilir).

### v1.8.1

**Yeni: Bildirim alanı (arka planda çalışma)**
- Pencere kapatılınca uygulama bildirim alanında (^ gizli simgeler) çalışmaya devam eder; simgeye çift tıklayınca kaldığı yerden
  açılır. Uygulama yeniden başlatılırsa çalışan pencere öne gelir. Genel Ayarlar → Kolay Ayar'dan kapatılabilir.
- Sağ tık menüsü: E-mre Control Center'ı aç, Ana Sayfa, Tümünü Kontrol Et, Tek Tıkla Tanıla, Hız Testi, Performans, İşlem Geçmişi,
  Bildirimler (Açık / Kapalı), Çıkış. Çıkış uygulamayı gerçekten kapatır; işlem sürüyorsa önce onay ister.
- Pencere gizliyken canlı ölçümler (Performans, İşlemler) durur; açılınca kaldığı yerden sürer.
- Kurulum, güncelleme ve kaldırma, pencere gizli olsa da çalışan uygulamayı kapatabilir.

### v1.8.0

**Yeni: Sistem Tanılama merkezi**
- Ana sayfaya **Sistem Araçları** kategorisi eklendi (8 kategori, üstte 4 · altta 4): **Tek Tıkla Tanıla**, **Başlangıç Uygulamaları**,
  **Windows Servisleri**, **İşlemler**, **Güvenlik**, **Sistem Raporu**, **Destek Paketi**.
- Mevcut kategorilere yeni bölmeler: Güncelleme → **Sürücüler**, **Uygulamalar** · Temizleme → **Depolama Analizi** · Cihaz Sağlık →
  **Sistem Sağlığı**, **Depolama Sağlığı**, **Olay Günlüğü**, **Çökme Analizi** · Hız Testi → **Ağ Merkezi**, **DNS Tanılama** ·
  Genel Ayarlar → **Gizlilik** · Cihaz Bilgileri → **Batarya**; Cihaz Durumu **Performans** oldu (disk etkinliği ve ağ aktarımı eklendi).
- **Tek Tıkla Tanıla:** 10 gerçek kontrol, adım adım durum (yüzde uydurulmaz), sonuç sayıları ve ayrıntı ekranına geçiş.
- **Özet:** Sistem Tanılaması satırları (son gerçek sonuç veya "Henüz kontrol edilmedi"); **İşlem Geçmişi** artık araç işlemlerini de
  (sürücü denetimi, ağ / DNS testi, hizmet ve başlangıç değişiklikleri, rapor, destek paketi) gerçek sonucu ve hata metniyle kaydeder.
- **Tümünü Kontrol Et:** kart kontrollerinden sonra güvenli tanılama adımları da çalışır (yalnızca okuma).
- **Arama:** yeni bölmeler ve "SFC", "DNS", "Wi-Fi", "Başlangıç", "Servis", "Event Log", "BSOD", "Batarya", "Sürücü" gibi terimler.
- **Sistem Raporu** (TXT / HTML / JSON) ve **Destek Paketi** (ZIP): kişisel bilgiler (bilgisayar / kullanıcı adı, IP, MAC) gizlenir,
  hiçbir yere gönderilmez.

**Güvenlik**
- Değişiklik yapan her işlem onay ister ve sonucu yeniden okunarak doğrulanır: başlangıç kaydı silinmez (yalnızca Görev Yöneticisi
  durumu), kritik hizmetler durdurulamaz, Windows / hizmet / kritik işlemler sonlandırılamaz, dosya yalnızca Geri Dönüşüm Kutusu'na
  gönderilir. Sürücü, uygulama, DNS ve güvenlik ayarları bu uygulamadan değiştirilmez.

**İyileştirme**
- Pencere küçük ekranlarda (ör. 1366x768) çalışma alanına sığar; yeni ekranlarda yatay kaydırma yoktur.

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
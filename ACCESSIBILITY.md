# Erişilebilirlik / Accessibility

[Türkçe](#türkçe) · [English](#english)

## Türkçe

E-mre Control Center'ın mümkün olduğunca çok kişi tarafından rahatça kullanılabilmesini istiyoruz. Bu sayfa neyin desteklendiğini,
bilinen sınırlamaları ve bir engelle karşılaşırsanız nasıl bildireceğinizi açıklar.

### Desteklenen ortam

- Windows 11 (masaüstü uygulaması). Pencere en az 1024 × 640 boyutundadır ve 1366 × 768 ekrana sığar.
- Windows'un ekran ölçeklendirme ayarına (ör. %125, %150) uyum sağlar.
- Arayüz Türkçe veya İngilizce; koyu (varsayılan) veya açık tema.

### Neler destekleniyor

- **Klavye:** Tab / Shift+Tab ile denetimler arasında gezinilir, Enter / Boşluk ile seçilir.
  - Odaklanan öğe koyu ve açık temada neon bir çerçeveyle gösterilir (düğmeler, seçimler, bağlantılar, tablolar, işlem günlüğü,
    kurulum ve kaldırma pencereleri dahil). Çerçeve yalnızca klavyeyle gezinirken görünür.
  - Ana penceredeki kaydırılabilir bölmeler (ör. kartlar, Cihaz Bilgileri, yasal metinler) Tab ile odaklanıp ok tuşları ve
    Page Up / Page Down ile kaydırılabilir.
  - Ana sayfada **Ctrl+F** arama kutusuna gider, **Aşağı ok** sonuçlara geçer, **Enter** ilk sonucu açar.
  - **Esc** iletişim kutusunu veya Detaylı Sonuç panelini kapatır, aramayı temizler ve ana sayfaya döner.
  - Bildirim alanındaki simge klavyeyle de açılabilir.
- **Ekran okuyucular:** Simgeli düğmelerin ve seçimlerin çoğu Windows UI Automation üzerinden adlandırılmıştır.
- **Renk tek başına bilgi taşımaz:** Her renkli durum noktasının yanında durumun metni yazar (ör. "Güncel", "Hata").
- **Grafikler metinle desteklenir:** Hız testi göstergesi ve performans halkalarındaki değerler ayrıca sayı olarak yazılır.
- **Hareket:** Yanıp sönen içerik yoktur; dönen simgeler yalnızca bir işlem sürerken görünür.
- **Zaman sınırı:** Onay pencereleri siz karar verene kadar bekler. Bilgisayarın yeniden başlatılması yalnızca onayınızla planlanır.

### Bilinen sınırlamalar

- Uygulama Windows Ekran Okuyucusu ve NVDA ile sistematik olarak test edilmedi; bazı öğeler eksik veya genel bir adla okunabilir.
- Windows'un yüksek karşıtlık temaları uygulamanın kendi renklerine uygulanmaz; koyu veya açık tema kullanılır.
- Windows'un "Metin boyutu" ayarı (Erişilebilirlik → Metin boyutu) tüm metinlere uygulanmayabilir.
- Renk kontrastı ölçüldü ve bazı yerlerde WCAG AA sınırının (normal metin için 4,5:1) altında: birincil düğmelerdeki beyaz yazı
  degradenin açık mavi başlangıcında (koyu temada 2,28:1, açık temada 2,70:1), açık temada bağlantı rengi (3,97:1) ile yeşil ve turuncu
  durum metinleri (3,37:1 ve 3,81:1), koyu temada günlük saat damgaları (2,47:1). Ana metinler 5:1'in üzerindedir. Ayrıntı:
  [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md#contrast-measured).
- Windows'un "Animasyon efektleri" ayarı kapatılsa da kısa geçiş animasyonları (sayfa açılışı, kart üzerine gelme) oynar.

### WCAG ile ilişkisi

Uygulama bir web sitesi değil, masaüstü uygulamasıdır; WCAG 2.2 ölçütleri masaüstüne uyarlanarak değerlendirildi. Resmî bir
uygunluk denetimi yapılmadı; aşağıdaki tablo bir uygunluk beyanı değildir.

| Ölçüt | Durum | Dayanak |
|---|---|---|
| 1.4.1 Renk kullanımı | Karşılanıyor | Her durum rengi metinle birlikte gösterilir |
| 1.4.3 Kontrast (asgari) | Kısmen | Ölçüldü; yukarıdaki istisnalar dışında 4,5:1 ve üzeri |
| 1.4.4 Metin boyutlandırma | Kısmen | Ekran ölçeklendirme desteklenir; "Metin boyutu" her metne uygulanmayabilir |
| 2.1.1 Klavye | Kısmen doğrulandı | Ana akışlar klavyeyle kullanılır; tüm akışlar elle denenmedi |
| 2.2.1 Zamanlama | Karşılanıyor | Süre sınırı yok |
| 2.3.1 Üç parlama | Karşılanıyor | Yanıp sönen içerik yok |
| 2.4.7 Odak görünür | Doğrulandı | Otomatik denetim: odaklanabilen her denetim klavye odağını gösteriyor (koyu ve açık tema) |
| 3.1.1 Sayfanın dili | Karşılanıyor | Türkçe veya English seçilir; tüm metinler seçilen dilde |
| 4.1.2 Ad, rol, değer | Kısmen | Çoğu denetimin UI Automation adı var; ekran okuyucuyla sistematik test yok |

### Engel bildirme

Bir engelle karşılaşırsanız lütfen bildirin:

- [Hata bildirme formu](https://github.com/E-mre-Hub/E-mre-Control-Center/issues/new/choose): "Hangi bölüm?" alanında ilgili bölümü
  seçin ve başlığa "Erişilebilirlik" yazın.
- veya [Tartışmalar](https://github.com/E-mre-Hub/E-mre_-Discussions/discussions) sayfasında bir konu açın.

Kullandığınız yardımcı teknolojiyi (ör. Ekran Okuyucusu, NVDA, büyüteç), Windows sürümünü ve uygulama sürümünü yazmanız sorunu
anlamamıza yardımcı olur. Bildirimler, sınırlamaların hangi sırayla giderileceğine karar verirken dikkate alınır.

## English

We want E-mre Control Center to be comfortable to use for as many people as possible. This page explains what is supported, the known
limitations and how to report a barrier.

### Supported environment

- Windows 11 (desktop app). The window is at least 1024 × 640 and fits a 1366 × 768 screen.
- Follows Windows display scaling (e.g. 125%, 150%).
- Interface in English or Turkish; dark (default) or light theme.

### What is supported

- **Keyboard:** move between controls with Tab / Shift+Tab, activate with Enter / Space.
  - The focused item is shown with a neon outline in both the dark and the light theme (including buttons, choices, links, tables,
    the operation log and the setup and uninstall windows). The outline only appears while you navigate with the keyboard.
  - Scrollable sections in the main window (e.g. the cards, Device Info, the legal texts) can be focused with Tab and scrolled with
    the arrow keys and Page Up / Page Down.
  - On the home screen **Ctrl+F** goes to the search box, **Down arrow** moves to the results and **Enter** opens the first result.
  - **Esc** closes a dialog or the Detailed Result panel, clears the search and returns to the home screen.
  - The notification area icon can also be opened with the keyboard.
- **Screen readers:** most icon buttons and choices are named through Windows UI Automation.
- **Color is never the only signal:** every colored status dot is accompanied by the status text (e.g. "Up to date", "Error").
- **Charts have text values:** the speed test gauge and performance rings also show their values as numbers.
- **Motion:** no flashing content; spinning icons appear only while an operation is running.
- **No time limits:** confirmation dialogs wait for your decision. A computer restart is only scheduled with your approval.

### Known limitations

- The app has not been systematically tested with Windows Narrator and NVDA; some elements may be read with a missing or generic name.
- Windows high contrast themes are not applied to the app's own colors; the dark or light theme is used instead.
- The Windows "Text size" setting (Accessibility → Text size) may not apply to all text.
- Color contrast was measured and is below the WCAG AA threshold (4.5:1 for normal text) in some places: white text on primary buttons
  at the light blue start of the gradient (2.28:1 dark, 2.70:1 light), the light theme link color (3.97:1) and green and orange status
  text (3.37:1 and 3.81:1), and log timestamps in the dark theme (2.47:1). Main text is above 5:1. Details:
  [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md#contrast-measured).
- Short transition animations (page entrance, card hover) still play when the Windows "Animation effects" setting is turned off.

### Relation to WCAG

The app is a desktop application, not a website; WCAG 2.2 criteria were assessed as adapted to a desktop app. No formal conformance
audit has been performed; the table below is not a conformance claim.

| Criterion | Status | Basis |
|---|---|---|
| 1.4.1 Use of Color | Met | Every status color comes with text |
| 1.4.3 Contrast (Minimum) | Partly | Measured; 4.5:1 or more except the cases listed above |
| 1.4.4 Resize Text | Partly | Display scaling is supported; "Text size" may not apply to all text |
| 2.1.1 Keyboard | Partly verified | Main flows work with the keyboard; not every flow was tested manually |
| 2.2.1 Timing Adjustable | Met | No time limits |
| 2.3.1 Three Flashes | Met | No flashing content |
| 2.4.7 Focus Visible | Verified | Automated audit: every focusable control shows keyboard focus (dark and light theme) |
| 3.1.1 Language of Page | Met | English or Turkish is chosen; all text appears in that language |
| 4.1.2 Name, Role, Value | Partly | Most controls have a UI Automation name; no systematic screen reader testing |

### Reporting a barrier

If you run into a barrier, please let us know:

- Use the [bug report form](https://github.com/E-mre-Hub/E-mre-Control-Center/issues/new/choose): pick the related area in "Which area?"
  and include "Accessibility" in the title.
- Or open a topic in [Discussions](https://github.com/E-mre-Hub/E-mre_-Discussions/discussions).

Mentioning the assistive technology you use (e.g. Narrator, NVDA, Magnifier), your Windows version and the app version helps us
understand the problem. Reports are taken into account when deciding the order in which limitations are addressed.

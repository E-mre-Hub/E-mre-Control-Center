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

### Reporting a barrier

If you run into a barrier, please let us know:

- Use the [bug report form](https://github.com/E-mre-Hub/E-mre-Control-Center/issues/new/choose): pick the related area in "Which area?"
  and include "Accessibility" in the title.
- Or open a topic in [Discussions](https://github.com/E-mre-Hub/E-mre_-Discussions/discussions).

Mentioning the assistive technology you use (e.g. Narrator, NVDA, Magnifier), your Windows version and the app version helps us
understand the problem. Reports are taken into account when deciding the order in which limitations are addressed.

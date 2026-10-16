# Katkı rehberi / Contributing

[Türkçe](#türkçe) · [English](#english)

## Türkçe

İlginiz için teşekkürler. Katkı yalnızca kod değildir; hata bildirmek, soru yanıtlamak ve çeviri düzeltmek de çok değerlidir.

### Nasıl yardımcı olabilirsiniz?

| Ne | Nerede |
|---|---|
| Hata bildirmek | [Issues](https://github.com/E-mre-Hub/E-mre-Control-Center/issues/new/choose) → "Hata bildir" formu |
| Soru sormak, fikir önermek | [Tartışmalar](https://github.com/E-mre-Hub/E-mre_-Discussions/discussions) |
| Güvenlik açığı bildirmek | Herkese açık yazmayın: [SECURITY.md](SECURITY.md) |
| Çeviri veya metin düzeltmek | Issue açın ya da pull request gönderin |

### Kod katkısı

1. Büyük bir değişiklikten önce bir Issue veya tartışma açarak fikrinizi paylaşın; boşa emek harcanmasın.
2. Depoyu çatallayın (fork), ayrı bir dalda çalışın ve pull request açın. Önerilen dal adları (zorunlu değil): `fix/kisa-aciklama`,
   `feature/kisa-aciklama`, `docs/kisa-aciklama`.
3. Projenin kurallarına uyun: [AGENTS.md](AGENTS.md). En önemlileri:
   - Sonuçlar gerçek olmalı; başarısız bir işlem asla başarılı gösterilmez, sahte ilerleme yoktur.
   - Sistemi değiştiren hiçbir işlem kullanıcı onayı olmadan çalışmaz; UAC atlatılmaz.
   - Ekranda görünen her metin iki dilde yazılır (`L.T("Türkçe", "English")`, XAML'da `{l:T 'Türkçe', 'English'}`).
   - Renkler tema anahtarlarından gelir; görünümlere sabit renk yazılmaz. Emoji kullanılmaz.
   - Klavyeyle odaklanabilen her yeni denetim odağı gösterir (`Theme.xaml`'deki `Focus.*` stilleri) ve bir UI Automation adı taşır.
4. Derleme 0 uyarı ile bitmeli: `dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release`.
5. Testler sistemde gerçek değişiklik yapmamalı (güncelleme, onarım, silme vb.). Neyi, nasıl denetleyeceğiniz: [TESTING.md](TESTING.md).
6. Commit iletisi kısa ve ne yapıldığını anlatan bir cümle olsun (Türkçe veya İngilizce), ör. "Hız testi geçmişinde tarih biçimi
   düzeltildi". Bir commit tek bir konuyu içersin.
7. Kullanıcıya görünen bir değişiklikse pull request açıklamasına Türkçe ve İngilizce kısa bir sürüm notu yazın. Sürüm numarasını,
   CHANGELOG bölümünü ve yayını proje sahibi hazırlar.

Kodu tanımak için: [ARCHITECTURE.md](ARCHITECTURE.md) (yapı), [CODE_STYLE.md](CODE_STYLE.md) (yazım kuralları),
[DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) (renkler ve bileşenler), [docs/TEKNIK.md](docs/TEKNIK.md) (Türkçe teknik belge).

### Lisans

Bu proje açık kaynak değildir; kaynak kodu görünürdür ve tüm hakları saklıdır ([LICENSE](LICENSE)). Bir katkı gönderdiğinizde,
katkınızın projenin bir parçası olarak proje sahibi tarafından kullanılabileceğini, değiştirilebileceğini ve dağıtılabileceğini kabul
etmiş olursunuz.

### Davranış

Tüm katılımcılardan [Davranış kurallarına](CODE_OF_CONDUCT.md) uymaları beklenir.

## English

Thank you for your interest. Contributing is not only code; reporting bugs, answering questions and fixing translations are very
valuable too.

### How you can help

| What | Where |
|---|---|
| Report a bug | [Issues](https://github.com/E-mre-Hub/E-mre-Control-Center/issues/new/choose) → "Report a bug" form |
| Ask a question, suggest an idea | [Discussions](https://github.com/E-mre-Hub/E-mre_-Discussions/discussions) |
| Report a vulnerability | Do not post it publicly: [SECURITY.md](SECURITY.md) |
| Fix a translation or text | Open an issue or send a pull request |

### Code contributions

1. Before a large change, open an issue or discussion to share the idea, so no effort is wasted.
2. Fork the repository, work on a separate branch and open a pull request. Suggested branch names (not required): `fix/short-name`,
   `feature/short-name`, `docs/short-name`.
3. Follow the project rules: [AGENTS.md](AGENTS.md). Most important:
   - Results must be real; a failed operation is never shown as successful, and there is no fake progress.
   - Nothing changes the system without the user's approval; UAC is never bypassed.
   - Every user-visible string is written in both languages (`L.T("Türkçe", "English")`, in XAML `{l:T 'Türkçe', 'English'}`).
   - Colors come from theme keys; never hard-code a color in a view. No emoji.
   - Every new focusable control shows keyboard focus (the `Focus.*` styles in `Theme.xaml`) and has a UI Automation name.
4. The build must finish with 0 warnings: `dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release`.
5. Tests must not make real changes to the system (updates, repairs, deletions, etc.). What to check and how: [TESTING.md](TESTING.md).
6. Write a short commit message that says what was done (Turkish or English), e.g. "Fix date format in speed test history". Keep one
   topic per commit.
7. For a user-visible change, add a short release note in Turkish and English to the pull request description. The maintainer prepares
   the version number, the CHANGELOG section and the release.

To get to know the code: [ARCHITECTURE.md](ARCHITECTURE.md) (structure), [CODE_STYLE.md](CODE_STYLE.md) (conventions),
[DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) (colors and components), [docs/TEKNIK.md](docs/TEKNIK.md) (technical documentation in Turkish).

### License

This project is not open source; the source code is viewable and all rights are reserved ([LICENSE](LICENSE)). By submitting a
contribution, you agree that it may be used, modified and distributed by the maintainer as part of the project.

### Conduct

All participants are expected to follow the [Code of conduct](CODE_OF_CONDUCT.md).

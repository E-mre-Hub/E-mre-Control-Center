# Güvenlik politikası / Security policy

[Türkçe](#türkçe) · [English](#english)

## Türkçe

### Desteklenen sürümler

Yalnızca **en son sürüm** desteklenir. Uygulama yeni sürümü kendisi denetler ve güncelleme zorunludur; bir güvenlik düzeltmesi
yayınlandığında tüm kullanıcılara uygulamanın içinden gelir.

| Sürüm | Destek |
|---|---|
| En son sürüm | Evet |
| Daha eski sürümler | Hayır (uygulama açılınca en son sürüme güncellenir) |

### Güvenlik açığı bildirme

**Lütfen güvenlik açıklarını herkese açık Issues sayfasına yazmayın.**

1. Deponun **Security** sekmesinde **Report a vulnerability** ile özel olarak bildirin (yalnızca depo sahibi görür).
2. Bu seçenek görünmüyorsa [Issues](https://github.com/E-mre-Hub/E-mre-Control-Center/issues) sayfasında ayrıntı vermeden
   "Güvenlik bildirimi" başlıklı bir konu açın; size özel iletişim yolu bildirilir.

Bildirimde şunlar yardımcı olur: uygulama sürümü, Windows sürümü, açığın ne olduğu ve nasıl tekrarlanacağı, olası etkisi.
Kişisel bilgilerinizi ve başkalarının sistemlerine ait bilgileri paylaşmayın. Bildirimler incelenir; doğrulanan açıklar düzeltilip
sürüm notunda (CHANGELOG.md) belirtilir.

### Kapsam

Kapsamda: bu depodaki uygulama, kurulum / kaldırma ekranları, uygulama içi güncelleme ve GitHub Actions derleme süreci.
Kapsam dışında: uygulamanın kullandığı üçüncü taraf hizmet ve programlar (Windows, winget paketleri, NVIDIA, Ookla vb.; bunları
kendi üreticilerine bildirin) ve zaten yönetici yetkisiyle çalışan kötü amaçlı yazılımın bulunduğu bilgisayarlar.

### Güvenli indirme

Uygulamayı yalnızca resmi sayfadan indirin: <https://github.com/E-mre-Hub/E-mre-Control-Center/releases>. Başka sitelerdeki
kopyalar değiştirilmiş olabilir. Releases sayfasında her dosyanın SHA-256 özeti görünür; indirdiğiniz dosyanınkini PowerShell'de
`Get-FileHash <dosya> -Algorithm SHA256` ile karşılaştırabilirsiniz.

### Uygulamanın güvenlik tasarımı (özet)

- İnternetten bağlantı kabul etmez (açık port, uzaktan kontrol yok); yalnızca kendisi bilinen hizmetlere bağlanır.
- Şifre, anahtar veya erişim belirteci (token) içermez; telemetri yoktur.
- Windows'un güvenlik sorusunu (UAC) atlatmaz.
- Yönetici yetkisiyle çalışırken normal kullanıcının değiştirebileceği hiçbir dosyayı yönetici olarak çalıştırmaz.
- İndirdiklerini çalıştırmadan önce doğrular: NVIDIA sürücüsünde NVIDIA dijital imzası, uygulama güncellemesinde SHA-256 özeti,
  boyut, ürün adı ve sürüm.
- Kurulum, onarım ve silme işlemleri yalnızca kullanıcının onayıyla çalışır.

Ayrıntılar: [Teknik belge](docs/TEKNIK.md), [Mimari](ARCHITECTURE.md) (İngilizce).

### Uygulanan önlemler ve doğrulama

- **Hesap, oturum, çerez:** uygulamada hesap, oturum açma veya sunucu tarafı yoktur; yetki Windows yönetici izniyle (UAC) sınırlıdır.
  Çerez kullanılmaz ([Çerez Politikası](legal/tr/cerez-politikasi.md)).
- **Girdi ve komut güvenliği:** komut satırları güvenli biçimde kurulur, kabuk özel karakterleri içeren argümanlar reddedilir, winget
  paket kimlikleri doğrulanır; uygulama yalnızca izin listesindeki web adreslerini açar.
- **Gizli bilgiler:** depoda ve uygulamada şifre, anahtar veya token yoktur. Yayın iş akışı yalnızca GitHub'ın kendi geçici belirtecini
  (`contents: write` yetkisiyle) kullanır; yayına yazan üçüncü taraf eylem bir commit kimliğine sabitlenmiştir. GitHub gizli bilgi
  taraması ve gönderim koruması açıktır. 16 Ekim 2026'da Git geçmişinin tamamı (33 commit) bilinen anahtar, token ve özel anahtar
  desenleri için tarandı; bulgu yok.
- **Bağımlılıklar:** tek NuGet paketi vardır (System.Management 8.0.0). `dotnet list package --vulnerable` (16 Ekim 2026) bilinen açık
  bildirmedi; Dependabot uyarıları açıktır. Lisanslar: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **Günlükler:** yalnızca bilgisayarda kalır; dosya yollarında Windows kullanıcı adı geçebilir. Sistem Raporu ve Destek Paketi'nde
  bilgisayar adı, kullanıcı adı, IP ve MAC adresleri maskelenir.
- **Testler:** proje sahibinin yerel testleri güvensiz adres ve şemaların, tehlikeli komut argümanlarının reddini ve indirme
  doğrulamasının hatalı özet, boyut, ürün veya sürümde dosyayı reddettiğini sınar ([TESTING.md](TESTING.md)).

### Bilinen eksikler

- Program dosyası bir kod imzalama sertifikasıyla imzalı değildir: Windows SmartScreen uyarısı ve izin penceresinde "Bilinmeyen
  yayıncı" görünür. Dosyanın bütünlüğü yukarıdaki SHA-256 özetiyle doğrulanabilir.
- Speedtest by Ookla'nın Windows aracı üreticisi tarafından imzalanmamıştır; güven, winget'in özet doğrulamasına dayanır.
- Bağımsız bir güvenlik denetimi yapılmadı; yazılım malzeme listesi (SBOM) yayınlanmıyor.

## English

### Supported versions

Only the **latest version** is supported. The app checks for new versions itself and updates are mandatory, so a security fix
reaches every user through the app.

| Version | Supported |
|---|---|
| Latest | Yes |
| Older | No (the app updates to the latest version when it starts) |

### Reporting a vulnerability

**Please do not report security vulnerabilities in public issues.**

1. Report privately with **Report a vulnerability** on the repository's **Security** tab (only the repository owner can see it).
2. If that option is not available, open an issue titled "Security report" on the
   [Issues](https://github.com/E-mre-Hub/E-mre-Control-Center/issues) page without any details; a private contact will be provided.

Helpful details: app version, Windows version, what the issue is and how to reproduce it, and its possible impact. Do not share
personal information or information about other people's systems. Reports are reviewed; confirmed issues are fixed and mentioned in
the release notes (CHANGELOG.md).

### Scope

In scope: the app in this repository, the setup and uninstall screens, the in-app update and the GitHub Actions build.
Out of scope: third-party services and programs used by the app (Windows, winget packages, NVIDIA, Ookla and others; please report
those to their vendors) and computers that already run malware with administrator rights.

### Safe download

Download the app only from the official page: <https://github.com/E-mre-Hub/E-mre-Control-Center/releases>. Copies on other sites
may have been modified. The Releases page shows the SHA-256 digest of every file; compare it with the digest of your download in
PowerShell: `Get-FileHash <file> -Algorithm SHA256`.

### Security design (summary)

- Accepts no incoming connections (no open ports, no remote control); it only connects to known services itself.
- Contains no passwords, keys or access tokens; no telemetry.
- Never bypasses Windows User Account Control (UAC).
- When running as administrator, it never runs a file that a standard user could modify.
- Verifies downloads before running them: NVIDIA's digital signature for drivers; SHA-256 hash, size, product name and version for
  app updates.
- Installation, repair and deletion run only with the user's approval.

Details: [Architecture](ARCHITECTURE.md), [Technical document](docs/TEKNIK.md) (Turkish).

### Measures in place and how they are verified

- **Accounts, sessions, cookies:** the app has no accounts, sign-in or server side; privileges are limited to the Windows administrator
  permission (UAC). No cookies are used ([Cookie Policy](legal/en/cookie-policy.md)).
- **Input and command safety:** command lines are built safely, arguments containing shell metacharacters are rejected, winget
  package IDs are validated, and the app opens only allowlisted web addresses.
- **Secrets:** the repository and the app contain no passwords, keys or tokens. The release workflow uses only GitHub's temporary token
  (with `contents: write`); the third-party action that writes the release is pinned to a commit. GitHub secret scanning and push
  protection are enabled. On 2026-10-16 the entire Git history (33 commits) was scanned for known key, token and private key
  patterns; nothing was found.
- **Dependencies:** there is one NuGet package (System.Management 8.0.0). `dotnet list package --vulnerable` (2026-10-16) reported no
  known vulnerabilities; Dependabot alerts are enabled. Licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **Logs:** stay on the computer; file paths may contain the Windows user name. The System Report and Support Package mask the computer
  name, user name, IP and MAC addresses.
- **Tests:** the maintainer's local tests check that unsafe addresses and schemes and dangerous command arguments are rejected and that
  download verification rejects a wrong digest, size, product or version ([TESTING.md](TESTING.md)).

### Known gaps

- The executable is not signed with a code signing certificate: Windows SmartScreen warns and the permission prompt shows "Unknown
  publisher". File integrity can be checked with the SHA-256 digest above.
- Ookla's Speedtest CLI for Windows is not signed by its vendor; trust relies on winget's hash verification.
- No independent security audit has been performed; no software bill of materials (SBOM) is published.

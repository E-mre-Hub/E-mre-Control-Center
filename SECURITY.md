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
kopyalar değiştirilmiş olabilir.

### Uygulamanın güvenlik tasarımı (özet)

- İnternetten bağlantı kabul etmez (açık port, uzaktan kontrol yok); yalnızca kendisi bilinen hizmetlere bağlanır.
- Şifre, anahtar veya erişim belirteci (token) içermez; telemetri yoktur.
- Windows'un güvenlik sorusunu (UAC) atlatmaz.
- Yönetici yetkisiyle çalışırken normal kullanıcının değiştirebileceği hiçbir dosyayı yönetici olarak çalıştırmaz.
- İndirdiklerini çalıştırmadan önce doğrular: NVIDIA sürücüsünde NVIDIA dijital imzası, uygulama güncellemesinde SHA-256 özeti,
  boyut, ürün adı ve sürüm.
- Kurulum, onarım ve silme işlemleri yalnızca kullanıcının onayıyla çalışır.

Ayrıntılar: [Teknik belge](docs/TEKNIK.md).

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
may have been modified.

### Security design (summary)

- Accepts no incoming connections (no open ports, no remote control); it only connects to known services itself.
- Contains no passwords, keys or access tokens; no telemetry.
- Never bypasses Windows User Account Control (UAC).
- When running as administrator, it never runs a file that a standard user could modify.
- Verifies downloads before running them: NVIDIA's digital signature for drivers; SHA-256 hash, size, product name and version for
  app updates.
- Installation, repair and deletion run only with the user's approval.

Details (Turkish): [Technical document](docs/TEKNIK.md).

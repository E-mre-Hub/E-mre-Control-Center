# E-mre Control Center – Gizlilik Politikası

**Sürüm:** 1.0 · **Yürürlük tarihi:** 7 Ekim 2026 · [English version](../en/privacy-policy.md)

Bu politika, Windows için bakım ve güncelleme uygulaması **E-mre Control Center**'ın ("Uygulama") hangi bilgileri işlediğini,
nerede sakladığını ve hangi durumlarda üçüncü taraflara bağlandığını açıklar. Kısaca: **Uygulama hesap istemez, sizi tanımlayan
bilgi toplamaz, telemetri / analitik / reklam kullanmaz ve çerez kullanmaz.** Uygulamanın ürettiği veriler bilgisayarınızda kalır.

## 1. Veri sorumlusu ve iletişim

Uygulama bağımsız bir geliştirici tarafından "E-mre Control Center" adıyla yayınlanır. Bu politika ile ilgili soru ve talepleriniz
için proje sayfasındaki **Issues** bölümünü kullanabilirsiniz: <https://github.com/E-mre-Hub/E-mre-Control-Center/issues>
(lütfen herkese açık bir başlığa kişisel bilgi yazmayın).

## 2. Bilgisayarınızda saklanan veriler

Uygulama aşağıdaki verileri **yalnızca bilgisayarınızda** saklar; geliştiriciye veya başka bir yere göndermez:

| Veri | Konum | İçerik |
|---|---|---|
| Ayarlar ve işlem geçmişi | `%LOCALAPPDATA%\E-mre Control Center\state.json` | Tercihler (bildirimler, bildirim alanı, günlük ayarı vb.), yapılan kontrol / güncelleme işlemlerinin özeti, hız testi geçmişi (en fazla 50 sonuç; **IP adresi kaydedilmez**), Ookla koşullarının kabul tarihi |
| Oturum günlükleri | `%LOCALAPPDATA%\E-mre Control Center\Logs\` | Yapılan işlemlerin ayrıntıları: komutlar, sonuç kodları, paket adları, dosya yolları (Windows kullanıcı adınızı içeren yollar olabilir) |
| Kurulum günlüğü | `%TEMP%\E-mre Control Center Kurulum.log` | Kurulum / kaldırma adımları |
| Geçici indirmeler | `C:\ProgramData\…` veya `%TEMP%\…` altında, bu uygulamaya özel klasörler | Uygulama güncellemesi ve NVIDIA sürücüsü kurulum dosyaları; kurulumdan sonra silinir |

- Günlükler yalnızca sizin isteğinizle silinir (Genel Ayarlar → Günlük Dosyaları; isteğe bağlı olarak 30 günden eskileri açılışta).
- **Sistem Raporu** ve **Destek Paketi** yalnızca siz istediğinizde, seçtiğiniz konuma oluşturulur. Bu dosyalarda bilgisayar adı,
  kullanıcı adı, kullanıcı klasörü yolu, cihazınızın IP adresleri ve MAC adresleri **maskelenir**; Wi-Fi ağ adı rapora yazılmaz.
  Bu dosyaları kiminle paylaşacağınıza siz karar verirsiniz.
- Uygulamayı kaldırırken "uygulama verilerini de sil" seçeneğiyle bu verileri silebilirsiniz.

## 3. Uygulamanın bağlandığı hizmetler

Uygulama, işlevlerini yerine getirmek için aşağıdaki hizmetlere bağlanır. Her bağlantıda IP adresiniz, internetin doğası gereği
bağlanılan sunucu tarafından görülebilir. Bu hizmetler kendi gizlilik politikalarına tabidir.

| Hizmet | Ne zaman | Gönderilen bilgi |
|---|---|---|
| **GitHub** (api.github.com, github.com) | Açılışta ve uygulama açıkken 5 dakikada bir yeni sürüm denetimi; "Güncelle"ye basınca kurulum dosyasının indirilmesi | Uygulama sürümünü içeren tarayıcı kimliği (`E-mre-Control-Center/x.y.z`); hesap / kişisel bilgi yok |
| **Microsoft bağlantı testi** (msftconnecttest.com, msftncsi.com) | İnternet bağlantısını doğrulamak için | Standart bir istek; içerik gönderilmez |
| **Microsoft hizmetleri** (Windows Update, winget, Microsoft Store, Microsoft Defender, Microsoft Edge Update) | Siz kontrol / güncelleme başlattığınızda | Bu işlemler Windows'un kendi bileşenleri tarafından yapılır ve Microsoft'un gizlilik bildirimine tabidir. Defender için Microsoft'un herkese açık sürüm sayfası okunur |
| **Uygulama yayıncıları** | winget ile bir uygulama güncellendiğinde | Kurulum dosyası, uygulamayı yayınlayan şirketin sunucusundan (ör. discord.com) winget tarafından indirilir |
| **NVIDIA** (nvidia.com, gfwsl.geforce.com, download.nvidia.com) | NVIDIA kartı kontrol edildiğinde / sürücü güncellendiğinde | Ekran kartı modeli, Windows sürümü ve dil (en uygun sürücüyü bulmak için) |
| **Cloudflare** (speed.cloudflare.com, 1.1.1.1) | Hız testi ve ağ tanılaması yaptığınızda | Test trafiği. Cloudflare, görünen IP adresinizi ve internet sağlayıcınızı ekranda göstermek için döndürür; IP adresi kaydedilmez |
| **DNS tanılaması** | DNS testini başlattığınızda | Yapılandırılmış DNS sunucularınıza bilinen alan adları (microsoft.com, cloudflare.com, google.com, wikipedia.org) için sorgu |
| **Speedtest by Ookla** (isteğe bağlı) | Yalnızca Ookla altyapısını seçip Ookla'nın koşullarını kabul ederseniz | Hız testi Ookla'nın resmi aracıyla yapılır; sonuçlar Ookla'ya iletilir ve Ookla'nın [Gizlilik Politikası](https://www.speedtest.net/about/privacy) ile [Kullanım Koşulları](https://www.speedtest.net/about/terms)'na tabidir. Kabulünüzü uygulamadan geri alabilirsiniz |
| **Google Forms** (isteğe bağlı) | Yalnızca uygulamayı kaldırırken geri bildirim göndermeyi seçerseniz | Seçtiğiniz kaldırma nedenleri, yazdığınız mesaj, uygulama sürümü ve Windows sürümü. **Adınız, e-posta adresiniz, bilgisayar adınız veya kullanıcı adınız gönderilmez.** Mesaj alanına kişisel bilgi yazmamanızı rica ederiz |

## 4. Çerezler ve izleme teknolojileri

Uygulama **çerez, reklam kimliği, parmak izi (fingerprinting), telemetri veya analitik aracı kullanmaz** ve kullanım istatistiği
toplamaz. Uygulamanın indirildiği GitHub sayfaları GitHub'ın kendi gizlilik ve çerez politikalarına tabidir.

## 5. Verilerin kullanım amacı ve hukuki dayanak

- Bilgisayarınızda saklanan veriler yalnızca Uygulamanın çalışması (işlem geçmişini göstermek, ayarları hatırlamak, sorun gidermek)
  içindir.
- İsteğe bağlı geri bildirim yalnızca Uygulamayı geliştirmek için kullanılır ve sizin açık tercihinize (rızanıza) dayanır.
- Geliştirici, Uygulama aracılığıyla sizi tanımlayan bir veri tabanı tutmaz ve verileri satmaz, kiralamaz veya paylaşmaz.

## 6. Saklama süresi

- Yerel veriler siz silene veya Uygulamayı verileriyle birlikte kaldırana kadar bilgisayarınızda kalır.
- Geri bildirim yanıtları, Uygulamanın geliştirilmesi için gerekli olduğu sürece geliştiricinin Google E-Tablolar hesabında saklanır.
  Yanıtlar kimliğinizle ilişkilendirilmediği için tek tek size ait olduğu belirlenemez.

## 7. Haklarınız (KVKK ve GDPR)

6698 sayılı Kişisel Verilerin Korunması Kanunu (KVKK) ve geçerli olduğu ölçüde Avrupa Birliği Genel Veri Koruma Tüzüğü (GDPR)
kapsamında; işlenen verilerinize erişme, düzeltme, silme, işlemeye itiraz etme ve şikâyet hakkınız vardır. Uygulama verilerinin büyük
kısmı yalnızca sizin bilgisayarınızda bulunduğundan bunları doğrudan kendiniz görüntüleyip silebilirsiniz. Diğer talepler için
1. bölümdeki iletişim kanalını kullanabilirsiniz. Türkiye'de Kişisel Verileri Koruma Kurumu'na, AB'de bulunduğunuz ülkenin veri koruma
otoritesine başvurma hakkınız saklıdır.

## 8. Çocukların gizliliği

Uygulama sistem bakımı içindir ve çocuklara yönelik değildir; bilerek çocuklardan veri toplanmaz.

## 9. Güvenlik

Uygulama, indirdiği kurulum dosyalarını çalıştırmadan önce doğrular (uygulama güncellemesinde boyut + SHA-256 + ürün adı + sürüm;
NVIDIA sürücüsünde NVIDIA dijital imzası) ve yönetici yetkisiyle çalışacak dosyaları yalnızca yöneticilerin erişebildiği klasörlerde
tutar. Hiçbir yöntem %100 güvenlik garantisi veremez.

## 10. Değişiklikler

Bu politika güncellenebilir. Güncel sürüm her zaman proje deposundaki `legal/` klasöründe ve Uygulamanın Genel Ayarlar → Yasal ve Gizlilik bölümünde
bağlantı olarak bulunur. Önemli değişiklikler sürüm notlarında belirtilir.

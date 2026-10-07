using System.IO;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Uygulamanın görünen adı ve kullanıcı veri klasörü (tek yerden). Ad geçmişi: "RTX Windows Updater" (v1.0–v1.2) →
/// "E-mre Hub" (v1.3) → "E-mre Control Center" (v1.4). Eski klasörler silinmez; işlem geçmişi (state.json) yeni klasörde
/// yoksa en yeni eski klasörden bir kez kopyalanır (<see cref="Services.AppStateStore"/>).
/// </summary>
public static class AppInfo
{
    public const string Name = "E-mre Control Center";

    /// <summary>Kaynak kod deposu (özel). Windows Uygulamalar kaydında "Destek bağlantısı" ve hız testi Referer başlığı.</summary>
    public const string RepositoryUrl = "https://github.com/E-mre-Hub/E-mre-Control-Center";

    /// <summary>Yasal belgelerin sürümü (Gizlilik Politikası + Kullanım Koşulları; depo: legal/). Değişince kabul yeniden kaydedilir.</summary>
    public const string LegalVersion = "1.0";

    public const string PrivacyPolicyUrl = RepositoryUrl + "/blob/main/legal/tr/gizlilik-politikasi.md";
    public const string TermsUrl = RepositoryUrl + "/blob/main/legal/tr/kullanim-kosullari.md";
    public const string PrivacyPolicyUrlEn = RepositoryUrl + "/blob/main/legal/en/privacy-policy.md";
    public const string TermsUrlEn = RepositoryUrl + "/blob/main/legal/en/terms-of-use.md";
    public const string IssuesUrl = RepositoryUrl + "/issues";

    /// <summary>Uygulamanın açmasına izin verilen yasal / proje adresleri (başka adres açılmaz).</summary>
    public static IReadOnlyList<string> LegalLinks { get; } = [PrivacyPolicyUrl, TermsUrl, PrivacyPolicyUrlEn, TermsUrlEn, IssuesUrl];

    /// <summary>
    /// Uygulama içi güncellemenin denetlediği depo: ana depo (2026-09-26'dan beri herkese açık; GitHub Actions her etiketle kurulum
    /// dosyasını ve sürüm notlarını buraya yayınlar). v1.7.0 – v1.7.1 ayrı "E-mre-Control-Center-Releases" deposuna bakıyordu
    /// (o depo hiç oluşturulmadı); o sürümler yeni sürümü ancak elle kurulumla veya o adda bir köprü deposuyla görür.
    /// </summary>
    public const string ReleasesRepository = "E-mre-Hub/E-mre-Control-Center";

    /// <summary>Önceki adlar, en yeniden en eskiye; yalnızca eski veri klasörlerini bulmak için kullanılır.</summary>
    public static IReadOnlyList<string> LegacyNames { get; } = ["E-mre Hub", "RTX Windows Updater"];

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%LOCALAPPDATA%\E-mre Control Center (günlükler, state.json, bildirim ikonu).</summary>
    public static string DataDirectory { get; } = Path.Combine(LocalAppData, Name);

    /// <summary>Eski sürümlerin veri klasörleri (%LOCALAPPDATA%\E-mre Hub, %LOCALAPPDATA%\RTX Windows Updater), en yeniden en eskiye.</summary>
    public static IReadOnlyList<string> LegacyDataDirectories { get; } = LegacyNames.Select(n => Path.Combine(LocalAppData, n)).ToList();

    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}

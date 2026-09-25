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

    /// <summary>
    /// Herkese açık sürüm deposu (yalnızca kurulum dosyaları ve sürüm notları; kaynak kod özel depoda kalır). GitHub Actions her etiketle
    /// kurulum dosyasını buraya da yayınlar; uygulama içi güncelleme buradan denetler.
    /// </summary>
    public const string ReleasesRepository = "E-mre-Hub/E-mre-Control-Center-Releases";

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

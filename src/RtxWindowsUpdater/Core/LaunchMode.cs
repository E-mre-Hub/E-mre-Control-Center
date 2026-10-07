using System.IO;

namespace RtxWindowsUpdater.Core;

/// <summary>Aynı EXE'nin açılış biçimi: uygulama, kurulum (Setup) veya kaldırma.</summary>
public enum LaunchMode
{
    App,
    /// <summary>Kurulum karşılama ekranı (dosya adında "Setup" / "Kurulum" geçiyor).</summary>
    Setup,
    /// <summary>Kurulum doğrudan başlar (karşılama ekranından yönetici olarak yeniden başlatılan örnek).</summary>
    Install,
    /// <summary>Windows Ayarlar → Uygulamalar → Kaldır (Uninstall kaydındaki komut).</summary>
    Uninstall
}

/// <summary>
/// Kurulum programı ayrı bir dosya değildir: Releases'taki "E-mre-Control-Center-Setup-TR-vX.Y.Z.exe" / "…-Setup-EN-…" uygulamanın kendisidir ve adında
/// "Setup" geçtiği için kurulum ekranıyla açılır; kendini "E-mre Control Center.exe" adıyla Program Files'a kopyalar (böylece kurulan
/// dosya ile indirilen dosya bayt bayt aynıdır). Kurulu uygulama Windows'un kaldırma komutunda <see cref="ArgUninstall"/> ile açılır.
/// </summary>
public static class LaunchModes
{
    public const string ArgInstall = "--install";
    public const string ArgUninstall = "--uninstall";
    public const string ArgDesktop = "--desktop";
    public const string ArgNoDesktop = "--no-desktop";
    /// <summary>Kaldırıcının geçici kopyası / güncelleme kurulumu, kendisini başlatan işlemin (kurulu EXE) kapanmasını bekler.</summary>
    public const string ArgWaitPid = "--wait-pid";
    /// <summary>Uygulama içi güncellemeyle başlatılan kurulum: bitince yeni sürüm kendiliğinden açılır.</summary>
    public const string ArgUpdate = "--update";
    /// <summary>Güncelleme kurulumunun yeni sürümü açarken verdiği önceki sürüm: yeni sürüm "Güncelleme tamamlandı: vX → vY" gösterir.</summary>
    public const string ArgUpdatedFrom = "--updated-from";
    /// <summary>Arayüz dili "tr" / "en" (kurulum, kurduğu uygulamayı açarken seçili dili iletir; uygulama ilk açılışta önerir).</summary>
    public const string ArgLang = "--lang";
    /// <summary>Tema "dark" / "light" (kurulum ekranında seçilen tema uygulamaya iletilir).</summary>
    public const string ArgTheme = "--theme";
    /// <summary>Uygulama kendini yeniden başlattı (dil değişikliği): yeni örnek öncekinin kapanmasını bekler.</summary>
    public const string ArgRestarted = "--restarted";

    /// <summary>Geçerli tema kodu ("dark" / "light"); tema her uygulandığında güncellenir (Views.ThemeManager.Apply).</summary>
    public static string? ThemeCode { get; set; }

    /// <summary>
    /// Başlatılan bir sonraki örneğe (UAC sonrası kurulum / kaldırma, geçici kaldırıcı kopyası, kurulan uygulama, güncelleme kurulumu)
    /// seçili dil ve tema iletilir: "--lang tr|en [--theme dark|light]".
    /// </summary>
    public static IEnumerable<string> PreferenceArgs()
    {
        yield return ArgLang;
        yield return L.Code;
        if (ThemeCode is { Length: > 0 } theme)
        {
            yield return ArgTheme;
            yield return theme;
        }
    }

    /// <summary>"--ad değer" biçimindeki argümanın değeri (yoksa null).</summary>
    public static string? ArgValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>
    /// Kurulum / kaldırma ekranının dili: "--lang" → dosya adındaki dil işareti ("…Setup-EN-…" / "English" → İngilizce, "…Setup-TR-…" →
    /// Türkçe) → kullanıcının uygulamada seçtiği dil → Türkçe. İşaretsiz eski ad (E-mre-Control-Center-Setup-vX.Y.Z.exe) yalnızca eski
    /// sürümlerin uygulama içi güncellemesi için yayında tutulan uyumluluk kopyasıdır.
    /// </summary>
    public static AppLanguage SetupLanguage(IReadOnlyList<string> args, string? processPath, AppLanguage? saved)
    {
        if (L.Parse(ArgValue(args, ArgLang)) is { } arg) return arg;
        var name = Path.GetFileNameWithoutExtension(processPath ?? string.Empty);
        var tokens = name.Split(['-', '_', ' ', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(t => t.Equals("EN", StringComparison.OrdinalIgnoreCase) || t.Equals("English", StringComparison.OrdinalIgnoreCase)))
            return AppLanguage.English;
        if (tokens.Any(t => t.Equals("TR", StringComparison.OrdinalIgnoreCase) || t.Equals("Turkish", StringComparison.OrdinalIgnoreCase) ||
                            t.Equals("Türkçe", StringComparison.OrdinalIgnoreCase)))
            return AppLanguage.Turkish;
        return saved ?? AppLanguage.Turkish;
    }

    public static LaunchMode Detect(IReadOnlyList<string> args, string? processPath)
    {
        if (args.Contains(ArgUninstall, StringComparer.OrdinalIgnoreCase)) return LaunchMode.Uninstall;
        if (args.Contains(ArgInstall, StringComparer.OrdinalIgnoreCase)) return LaunchMode.Install;
        var name = Path.GetFileNameWithoutExtension(processPath ?? string.Empty);
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("kurulum", StringComparison.OrdinalIgnoreCase)
            ? LaunchMode.Setup
            : LaunchMode.App;
    }
}

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
/// Kurulum programı ayrı bir dosya değildir: Releases'taki "E-mre-Control-Center-Setup-vX.Y.Z.exe" uygulamanın kendisidir ve adında
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

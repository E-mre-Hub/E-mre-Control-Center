using System.IO;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Uygulamanın görünen adı ve kullanıcı veri klasörü (tek yerden). v1.3.0 ile ad "RTX Windows Updater" → "E-mre Hub" oldu;
/// eski klasör silinmez, işlem geçmişi (state.json) ilk açılışta yeni klasöre kopyalanır (<see cref="Services.AppStateStore"/>).
/// </summary>
public static class AppInfo
{
    public const string Name = "E-mre Hub";

    /// <summary>v1.3.0 öncesindeki ad; yalnızca eski veri klasörünü bulmak için kullanılır.</summary>
    public const string LegacyName = "RTX Windows Updater";

    /// <summary>%LOCALAPPDATA%\E-mre Hub (günlükler, state.json, bildirim ikonu).</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>%LOCALAPPDATA%\RTX Windows Updater (eski sürümlerin veri klasörü).</summary>
    public static string LegacyDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName);

    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}

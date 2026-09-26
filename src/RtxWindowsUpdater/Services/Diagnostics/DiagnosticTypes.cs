namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>
/// Tanılama kontrolünün GERÇEK sonucu. Hiçbiri tahmin edilmez: Unknown = kontrol yapılamadı (yetki / destek / hata; neden Summary'de),
/// Skipped = bilinçli olarak çalıştırılmadı (ör. yönetici gerekli), Info = sağlık değerlendirmesi olmayan bilgi satırı.
/// </summary>
public enum CheckState { NotChecked, Checking, Healthy, Warning, Error, Unknown, Skipped, Info }

/// <summary>Tek bir kontrol satırı. TargetCategory / TargetSection: tıklanınca açılacak ayrıntı bölmesi.</summary>
public sealed record CheckResult(
    string Title,
    CheckState State,
    string Summary,
    string? Detail = null,
    string? TargetCategory = null,
    string? TargetSection = null);

public static class CheckStates
{
    public static string Text(CheckState state) => state switch
    {
        CheckState.Healthy => "Sağlıklı",
        CheckState.Warning => "Uyarı",
        CheckState.Error => "Hata",
        CheckState.Unknown => "Kontrol edilemedi",
        CheckState.Skipped => "Atlandı",
        CheckState.Checking => "Kontrol ediliyor…",
        CheckState.Info => "Bilgi",
        _ => "Henüz kontrol edilmedi"
    };

    /// <summary>Birden çok kontrolün özeti: hata > uyarı > kontrol edilemedi > sağlıklı (hiç sonuç yoksa NotChecked).</summary>
    public static CheckState Worst(IEnumerable<CheckState> states)
    {
        var list = states.Where(s => s is not (CheckState.Info or CheckState.NotChecked)).ToList();
        if (list.Count == 0) return CheckState.NotChecked;
        if (list.Contains(CheckState.Error)) return CheckState.Error;
        if (list.Contains(CheckState.Warning)) return CheckState.Warning;
        if (list.Contains(CheckState.Unknown)) return CheckState.Unknown;
        if (list.All(s => s == CheckState.Skipped)) return CheckState.Skipped;
        return CheckState.Healthy;
    }
}

/// <summary>Tarih / hız biçimleri (tanılama ekranları ve raporlar için ortak). Boyut: mevcut <see cref="TemporaryFilesManager.FormatSize"/>.</summary>
public static class Formats
{
    private static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    public static string Bytes(double bytes) => TemporaryFilesManager.FormatSize((long)Math.Max(0, bytes));

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/sn";

    public static string Date(DateTime? value) => value is { } d ? d.ToString("dd.MM.yyyy HH:mm", Tr) : "—";

    public static string Number(double value, string format = "0.#") => value.ToString(format, Tr);
}

/// <summary>
/// Tanılama sonuçlarının açtığı ekranların anahtarları (ViewModels.CategoryKeys / SectionKeys aynı değerleri buradan alır;
/// servis katmanı arayüz katmanına bağlanmadan "ilgili ayrıntı ekranı"nı belirtebilsin diye burada).
/// </summary>
public static class Nav
{
    // Kategoriler
    public const string Update = "update";
    public const string Cleanup = "cleanup";
    public const string Health = "health";
    public const string Settings = "settings";
    public const string Summary = "summary";
    public const string Device = "device";
    public const string SpeedTest = "speedtest";
    public const string SystemTools = "systools";

    // Yeni bölmeler
    public const string Drivers = "drivers";
    public const string Apps = "apps";
    public const string StorageAnalysis = "storage-analysis";
    public const string SystemHealth = "syshealth";
    public const string StorageHealth = "storagehealth";
    public const string EventLog = "eventlog";
    public const string Crash = "crash";
    public const string Network = "network";
    public const string Dns = "dns";
    public const string Privacy = "privacy";
    public const string Battery = "battery";
    public const string Performance = "device-status";
    public const string Diagnose = "diagnose";
    public const string Startup = "startup";
    public const string Services = "services";
    public const string Processes = "processes";
    public const string Security = "security";
    public const string Report = "report";
    public const string Support = "support";
    public const string Requirements = "admin";
    public const string Cards = "cards";
}

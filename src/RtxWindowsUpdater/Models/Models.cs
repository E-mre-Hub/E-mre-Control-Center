namespace RtxWindowsUpdater.Models;

/// <summary>Bir bileşenin (Winget, Windows Update, ...) gerçek durumu.</summary>
public enum ComponentStatus
{
    NotChecked,       // Gri   – kontrol edilmedi
    Checking,         // Mavi  – kontrol ediliyor
    UpToDate,         // Yeşil – güncel
    UpdateAvailable,  // Turuncu – güncelleme mevcut
    Updating,         // Mavi  – güncelleniyor
    Updated,          // Yeşil – başarıyla güncellendi / temizlendi
    PartiallyUpdated, // Turuncu – bir kısmı başarısız
    RebootRequired,   // Turuncu – yeniden başlatma gerekli
    AdminRequired,    // Kırmızı – yönetici izni gerekli
    CheckFailed,      // Kırmızı – kontrol edilemedi
    Failed,           // Kırmızı – güncelleme başarısız
    Skipped,          // Gri   – kullanıcı tarafından atlandı / uygulanamaz
    Attention,        // Turuncu – dikkat gerekiyor ancak otomatik işlem yapılmaz (örn. winget kurulum teknolojisi uyuşmazlığı)
    Unavailable       // Gri   – bu sistemde kullanım dışı (örn. NVIDIA RTX ekran kartı yok; hiçbir işlem çalıştırılmaz)
}

public static class ComponentKeys
{
    public const string Winget = "winget";
    public const string WindowsUpdate = "windowsupdate";
    public const string Store = "store";
    public const string Nvidia = "nvidia";
    public const string Defender = "defender";
    public const string Sfc = "sfc";
    public const string Dism = "dism";
    public const string Mrt = "mrt";
    public const string TempFiles = "tempfiles";
    public const string RecycleBin = "recyclebin";
}

/// <summary>Bir modül sonucunu üreten işlemin türü.</summary>
public enum OperationKind
{
    Check,   // Kontrol (sistemi değiştirmez)
    Update,  // Güncelleme / onarım / temizlik
    Action   // Bakım kartının kendi butonu (SFC /scannow, DISM CheckHealth, MRT hızlı tarama)
}

/// <summary>Bir modülün içindeki tek güncellenebilir öğe (paket, KB, sürücü...).</summary>
public sealed class UpdateItem
{
    public required string Name { get; init; }
    public string Id { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public bool UpdateAvailable { get; init; }

    /// <summary>"Tümünü Güncelle" ile otomatik güncellenebilir mi (winget açık hedefleme gibi durumlar hariç).</summary>
    public bool AutoUpdatable { get; init; } = true;

    /// <summary>Otomatik uygulanmayan güncellemenin türü (yalnızca kullanıcı ayrıca seçerse uygulanır).</summary>
    public ManualUpdateKind Manual { get; init; }

    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// Kullanıcının bu öğeyi işleme dahil edip etmediği (ör. Geçici Dosyalar kategorileri). Varsayılan: seçili.
    /// </summary>
    public bool Selected { get; set; } = true;

    /// <summary>Modüle özel ek veri (örn. NVIDIA indirme URL'si).</summary>
    public string? Tag { get; init; }

    // --- Paket bazlı GERÇEK güncelleme sonucu (winget çıkış kodu ve çıktısından; Detaylı Sonuç panelinde gösterilir) ---

    /// <summary>Bu öğe için güncelleme denendiyse sonucu; denenmediyse null.</summary>
    public ItemOutcome? Outcome { get; set; }

    /// <summary>Sonucun kısa Türkçe açıklaması (ör. "Kurulum teknolojisi uyuşmazlığı").</summary>
    public string? OutcomeText { get; set; }

    /// <summary>Aracın döndürdüğü kod (ör. "0x8A15008E").</summary>
    public string? ResultCode { get; set; }

    /// <summary>Kodun resmi sembolü (ör. "UPDATE_INSTALL_TECHNOLOGY_MISMATCH").</summary>
    public string? ResultSymbol { get; set; }

    /// <summary>Kurulum programının gerçek çıkış kodu (winget çıktısında bildirildiyse).</summary>
    public string? InstallerExitCode { get; set; }

    /// <summary>Aracın kendi mesajı (winget çıktısındaki son anlamlı satırlar).</summary>
    public string? ToolMessage { get; set; }

    /// <summary>Güncelleme, uygulama/dosyalar kullanımda olduğu için başarısız oldu (winget 0x8A150101/0x8A150103/0x8A150111).</summary>
    public bool InUse { get; set; }

    /// <summary>Restart Manager ile tespit edilen, paketin dosyalarını kullanan çalışan işlemler.</summary>
    public List<RunningProcessInfo> BlockingProcesses { get; set; } = [];
}

/// <summary>Winget'in otomatik uygulamadığı güncelleme türleri.</summary>
public enum ManualUpdateKind
{
    None,
    /// <summary>Paket yalnızca açık hedeflemeyle güncellenir (manifestte RequireExplicitUpgrade – genelde kendini güncelleyen uygulamalar – veya sabitleme).</summary>
    ExplicitTargeting,
    /// <summary>Kurulu sürümün kurulum türü (ör. MSI) yeni sürümden (ör. EXE) farklı: 0x8A15008E. Yalnızca kaldır + yeniden kur ile güncellenir.</summary>
    TechnologyMismatch
}

/// <summary>Bir öğe için denenen güncellemenin gerçek sonucu.</summary>
public enum ItemOutcome
{
    Updated,          // Başarılı (winget 0) – ardından doğrulandı
    UpdatedReboot,    // Başarılı, yeniden başlatma gerekli (0x8A150109 / 0x8A15010B)
    Failed,           // Başarısız
    Unverified        // Winget başarı bildirdi ancak doğrulamada güncelleme hâlâ görünüyor
}

/// <summary>
/// Bir dosyayı kullanan çalışan işlem (Windows Restart Manager tespiti).
/// <paramref name="StartTime"/> işlemin oluşturulma zamanıdır (FILETIME); aynı PID'nin başka bir işleme geçmesini önler.
/// </summary>
public sealed record RunningProcessInfo(
    int ProcessId,
    long StartTime,
    string Name,
    string? ExePath,
    string AppType,
    bool CanClose,
    string? NotClosableReason)
{
    public string DisplayText =>
        $"{Name} (PID {ProcessId})" + (CanClose ? string.Empty : $" – {NotClosableReason}");
}

/// <summary>Bir modülün kontrol veya güncelleme sonucunu arayüze taşır.</summary>
public sealed class ModuleResult
{
    public required string Key { get; init; }
    public required ComponentStatus Status { get; init; }

    /// <summary>Kartta büyük yazılan kısa durum ("3 güncelleme mevcut").</summary>
    public required string Summary { get; init; }

    /// <summary>Kartta gösterilen ek satırlar (sürüm bilgisi vb.).</summary>
    public string Details { get; init; } = string.Empty;

    /// <summary>Başarısızlık / uyarı nedeni (sonuç ekranında gösterilir).</summary>
    public string? Reason { get; init; }

    public IReadOnlyList<UpdateItem> Items { get; init; } = [];

    /// <summary>Gerçekten uygulanabilir güncelleme sayısı.</summary>
    public int ActionableCount { get; init; }

    public bool RebootRequired { get; init; }

    public bool HasActionableUpdates => Status == ComponentStatus.UpdateAvailable && ActionableCount > 0;

    // --- Orkestratörün işlem bitince doldurduğu gerçek çalışma bilgileri (Detaylı Sonuç paneli) ---

    /// <summary>İşlemin türü (kontrol / güncelleme / kart eylemi).</summary>
    public OperationKind Operation { get; set; }

    /// <summary>İşlemin gerçekten bittiği an.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>İşlemin gerçek süresi.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>İşlem sırasında çalıştırılan komutlar (gerçek stdout/stderr/çıkış kodu).</summary>
    public IReadOnlyList<RtxWindowsUpdater.Core.CommandRecord> Commands { get; set; } = [];

    /// <summary>Komut dışı gerçek ara sonuçlar (HTTP/API yanıtları vb.).</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];

    public static ModuleResult CheckFailed(string key, string reason, string details = "") => new()
    {
        Key = key,
        Status = ComponentStatus.CheckFailed,
        Summary = "Kontrol edilemedi",
        Reason = reason,
        Details = details
    };

    public static ModuleResult Failed(string key, string reason, string details = "") => new()
    {
        Key = key,
        Status = ComponentStatus.Failed,
        Summary = "Güncelleme başarısız",
        Reason = reason,
        Details = details
    };
}

public sealed class GpuInfo
{
    public required string Name { get; init; }
    public string WmiDriverVersion { get; init; } = string.Empty;
    public string PnpDeviceId { get; init; } = string.Empty;
    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    public bool IsRtx => IsNvidia && System.Text.RegularExpressions.Regex.IsMatch(Name, @"\bRTX\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

public sealed class RequirementsResult
{
    public bool IsWindows11 { get; init; }
    public string OsDescription { get; init; } = string.Empty;
    public bool HasRtxGpu { get; init; }
    public string GpuDescription { get; init; } = string.Empty;
    public GpuInfo? RtxGpu { get; init; }
    public bool IsAdministrator { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Uygulamaya giriş için yalnızca Windows 11 zorunludur. NVIDIA RTX ekran kartı yoksa uygulama "kartsız" kullanılabilir;
    /// bu durumda yalnızca NVIDIA Driver kartı kullanım dışı olur (<see cref="HasRtxGpu"/>).
    /// </summary>
    public bool IsSupported => IsWindows11;
}

/// <summary>Orkestratörün arayüze bildirdiği ilerleme.</summary>
public sealed record StepProgress(string Text, double Percent);

/// <summary>
/// Uzun süren bir modülün (SFC, DISM, MRT) çalışırken bildirdiği canlı durum.
/// Percent null ise ilerleme yüzdesi bilinmiyordur (belirsiz ilerleme).
/// </summary>
public sealed record ModuleProgress(string Key, string Text, double? Percent);

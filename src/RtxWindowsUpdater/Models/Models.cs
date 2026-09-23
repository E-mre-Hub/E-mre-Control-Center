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
    Attention         // Turuncu – dikkat gerekiyor ancak otomatik işlem yapılmaz (örn. DISM "onarılabilir")
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
    public const string RecycleBin = "recyclebin";
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

    public string StatusText { get; set; } = string.Empty;

    /// <summary>Modüle özel ek veri (örn. NVIDIA indirme URL'si).</summary>
    public string? Tag { get; init; }
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
    public bool IsSupported => IsWindows11 && HasRtxGpu;
}

/// <summary>Orkestratörün arayüze bildirdiği ilerleme.</summary>
public sealed record StepProgress(string Text, double Percent);

/// <summary>
/// Uzun süren bir modülün (SFC, DISM, MRT) çalışırken bildirdiği canlı durum.
/// Percent null ise ilerleme yüzdesi bilinmiyordur (belirsiz ilerleme).
/// </summary>
public sealed record ModuleProgress(string Key, string Text, double? Percent);

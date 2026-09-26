namespace RtxWindowsUpdater.Core;

/// <summary>Windows Depolama API'sinin bildirdiği fiziksel disk (MSFT_PhysicalDisk).</summary>
public sealed record PhysicalDiskInfo(
    string DeviceId,
    string Name,
    string? Model,
    int? MediaType,
    int? BusType,
    ulong? Size,
    int? HealthStatus,
    string? Firmware);

/// <summary>Diskin güvenilirlik sayaçları (MSFT_StorageReliabilityCounter; SMART / NVMe sağlık günlüğünden Windows'un okuduğu değerler).</summary>
public sealed record DiskReliability(
    string DeviceId,
    double? Temperature,
    double? TemperatureMax,
    double? Wear,
    long? ReadErrorsTotal,
    long? ReadErrorsUncorrected,
    long? WriteErrorsTotal,
    long? WriteErrorsUncorrected,
    long? PowerOnHours,
    long? StartStopCycles);

/// <summary>
/// Fiziksel disklerin ve güvenilirlik sayaçlarının tek okuma noktası (Cihaz Durumu sıcaklığı ve Depolama Sağlığı aynı kodu kullanır).
/// Sayaçlar yönetici yetkisi ister; sürücü bildirmediği değer null kalır (0 veya tahmin yazılmaz).
/// </summary>
public static class StorageInfo
{
    private const string Scope = @"\\.\root\Microsoft\Windows\Storage";

    public static WmiResult QueryPhysicalDisks(TimeSpan timeout) =>
        Wmi.Query(Scope, "SELECT DeviceId, FriendlyName, Model, MediaType, BusType, Size, HealthStatus, FirmwareVersion FROM MSFT_PhysicalDisk", timeout);

    public static (IReadOnlyList<PhysicalDiskInfo> Disks, WmiResult Raw) ReadPhysicalDisks(TimeSpan timeout)
    {
        var raw = QueryPhysicalDisks(timeout);
        var list = raw.Rows
            .Where(r => r.Str("DeviceId") is not null)
            .Select(r => new PhysicalDiskInfo(r.Str("DeviceId")!, r.Str("FriendlyName") ?? $"Disk {r.Str("DeviceId")}", r.Str("Model"),
                (int?)r.Long("MediaType"), (int?)r.Long("BusType"), r.ULong("Size"), (int?)r.Long("HealthStatus"), r.Str("FirmwareVersion")))
            .ToList();
        return (list, raw);
    }

    public static (IReadOnlyList<DiskReliability> Counters, WmiResult Raw) ReadReliability(TimeSpan timeout)
    {
        var raw = Wmi.Query(Scope,
            "SELECT DeviceId, Temperature, TemperatureMax, Wear, ReadErrorsTotal, ReadErrorsUncorrected, WriteErrorsTotal, " +
            "WriteErrorsUncorrected, PowerOnHours, StartStopCycleCount FROM MSFT_StorageReliabilityCounter", timeout);
        var list = raw.Rows
            .Where(r => r.Str("DeviceId") is not null)
            .Select(r => new DiskReliability(r.Str("DeviceId")!, Positive(r.Long("Temperature")), Positive(r.Long("TemperatureMax")),
                r.Long("Wear") is { } w and >= 0 and <= 100 ? w : null,
                r.Long("ReadErrorsTotal"), r.Long("ReadErrorsUncorrected"), r.Long("WriteErrorsTotal"), r.Long("WriteErrorsUncorrected"),
                r.Long("PowerOnHours"), r.Long("StartStopCycleCount")))
            .ToList();
        return (list, raw);

        // Sürücü değeri bildirmediğinde Windows 0 döndürür: 0 °C "okunamadı" sayılır, uydurulmaz.
        static double? Positive(long? v) => v is > 0 ? v : null;
    }

    public static string BusTypeText(int? bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Sanal (dosya)", 16 => "Depolama Alanları", 17 => "NVMe",
        18 => "SCM", 19 => "UFS", null => "—", _ => $"Bilinmiyor ({bus})"
    };

    public static string MediaTypeText(int? media) => media switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Belirtilmemiş"
    };

    public static string HealthText(int? health) => health switch
    {
        0 => "Sağlıklı", 1 => "Uyarı", 2 => "Sağlıksız", 5 => "Bilinmiyor", null => "Bildirilmedi", _ => $"Bilinmiyor ({health})"
    };
}

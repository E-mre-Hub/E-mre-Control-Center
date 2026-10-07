using System.IO;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

public sealed record VolumeInfo(string Letter, string Label, string FileSystem, long Total, long Free)
{
    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? Used * 100.0 / Total : 0;
    public string UsageText => L.T($"{Formats.Bytes(Used)} / {Formats.Bytes(Total)} kullanılıyor · {Formats.Bytes(Free)} boş (%{100 - UsedPercent:0})", $"{Formats.Bytes(Used)} / {Formats.Bytes(Total)} used · {Formats.Bytes(Free)} free ({100 - UsedPercent:0}%)");
}

public sealed record DiskHealth(
    PhysicalDiskInfo Disk,
    DiskReliability? Reliability,
    IReadOnlyList<VolumeInfo> Volumes,
    CheckState State,
    string StateText,
    IReadOnlyList<string> Findings)
{
    public string Name => Disk.Name;
    public string TypeText => $"{StorageInfo.MediaTypeText(Disk.MediaType)} · {StorageInfo.BusTypeText(Disk.BusType)}";
    public string SizeText => Disk.Size is { } s ? Formats.Bytes(s) : "—";
    public string TemperatureText => Reliability?.Temperature is { } t ? $"{t:0} °C" + (Reliability.TemperatureMax is { } m ? L.T($" (en yüksek {m:0} °C)", $" (max {m:0} °C)") : "") : L.T("Bildirilmedi", "Not reported");
    public string WearText => Reliability?.Wear is { } w ? L.T($"%{w:0} kullanıldı", $"{w:0}% used") : L.T("Bildirilmedi", "Not reported");
    public string PowerOnText => Reliability?.PowerOnHours is { } h ? L.T($"{h:N0} saat", $"{h:N0} hours") : L.T("Bildirilmedi", "Not reported");
    public string ErrorsText => Reliability is null ? L.T("Bildirilmedi", "Not reported")
        : L.T($"Okuma: {Reliability.ReadErrorsTotal?.ToString() ?? "—"} (düzeltilemeyen {Reliability.ReadErrorsUncorrected?.ToString() ?? "—"}) · ", $"Read: {Reliability.ReadErrorsTotal?.ToString() ?? "—"} (uncorrectable {Reliability.ReadErrorsUncorrected?.ToString() ?? "—"}) · ") +
          L.T($"Yazma: {Reliability.WriteErrorsTotal?.ToString() ?? "—"} (düzeltilemeyen {Reliability.WriteErrorsUncorrected?.ToString() ?? "—"})", $"Write: {Reliability.WriteErrorsTotal?.ToString() ?? "—"} (uncorrectable {Reliability.WriteErrorsUncorrected?.ToString() ?? "—"})");
    public string VolumesText => Volumes.Count == 0 ? L.T("Harfli bölüm yok", "No lettered partition") : string.Join(" · ", Volumes.Select(v => L.T($"{v.Letter} %{v.UsedPercent:0} dolu", $"{v.Letter} {v.UsedPercent:0}% full")));
    public string FindingsText => Findings.Count == 0 ? L.T("Sorun bulunmadı", "No problems found") : string.Join(" · ", Findings);
}

public sealed record StorageScan(IReadOnlyList<DiskHealth> Disks, IReadOnlyList<VolumeInfo> Volumes, string? Error, string? ReliabilityNote)
{
    public CheckState Overall => Error is not null ? CheckState.Unknown : CheckStates.Worst(Disks.Select(d => d.State));
}

/// <summary>
/// Depolama sağlığı: Windows Depolama API'si (MSFT_PhysicalDisk – Windows'un kendi sağlık değerlendirmesi), güvenilirlik sayaçları
/// (sıcaklık, aşınma, okuma/yazma hataları, çalışma saati; yönetici gerekir) ve harfli bölümlerin gerçek doluluğu. Sürücünün
/// bildirmediği değer "Bildirilmedi" yazar; eşik uyarıları açıkça belirtilir (%10'dan az boş alan, aşınma ≥ %90, ≥ 70 °C, düzeltilemeyen hata).
/// </summary>
public sealed class StorageHealthService(Logger logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public Task<StorageScan> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    private StorageScan Scan(CancellationToken ct)
    {
        var volumes = ReadVolumes();
        var (disks, rawDisks) = StorageInfo.ReadPhysicalDisks(Timeout);
        if (!rawDisks.Ok)
            return new StorageScan([], volumes, L.T("Fiziksel diskler okunamadı: ", "Could not read the physical disks: ") + rawDisks.Error, null);
        ct.ThrowIfCancellationRequested();

        var (counters, rawCounters) = StorageInfo.ReadReliability(Timeout);
        var note = rawCounters.Ok ? null
            : rawCounters.AccessDenied ? L.T("Güvenilirlik sayaçları (sıcaklık, aşınma, hata sayıları) yönetici yetkisi gerektirir.", "Reliability counters (temperature, wear, error counts) require administrator rights.")
            : L.T("Güvenilirlik sayaçları okunamadı: ", "Could not read the reliability counters: ") + rawCounters.Error;
        var byId = counters.ToDictionary(c => c.DeviceId, StringComparer.OrdinalIgnoreCase);

        // Harfli bölüm → disk numarası (MSFT_Partition); okunamazsa bölümler diske bağlanmaz ama ayrı listede gösterilir.
        var letterToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = Wmi.Query(@"\\.\root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Timeout);
        foreach (var p in parts.Rows)
        {
            var letter = p.Str("DriveLetter");
            if (letter is { Length: 1 } && char.IsLetter(letter[0]) && p.Long("DiskNumber") is { } n)
                letterToDisk[letter.ToUpperInvariant() + ":"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var result = new List<DiskHealth>();
        foreach (var d in disks)
        {
            byId.TryGetValue(d.DeviceId, out var rel);
            var vols = volumes.Where(v => letterToDisk.TryGetValue(v.Letter, out var n) && n == d.DeviceId).ToList();
            var findings = new List<string>();
            var state = d.HealthStatus switch
            {
                0 => CheckState.Healthy,
                1 => CheckState.Warning,
                2 => CheckState.Error,
                _ => CheckState.Unknown
            };
            if (d.HealthStatus == 1) findings.Add(L.T("Windows diski \"Uyarı\" durumunda bildiriyor", "Windows reports the disk in \"Warning\" state"));
            if (d.HealthStatus == 2) findings.Add(L.T("Windows diski \"Sağlıksız\" olarak bildiriyor – yedek alın", "Windows reports the disk as \"Unhealthy\" – back up your data"));
            if (rel?.Wear is >= 90) findings.Add(L.T($"Aşınma %{rel.Wear:0} (ömrünün sonuna yakın)", $"Wear {rel.Wear:0}% (near the end of its life)"));
            if (rel?.Temperature is >= 70) findings.Add(L.T($"Sıcaklık {rel.Temperature:0} °C (yüksek)", $"Temperature {rel.Temperature:0} °C (high)"));
            if (rel?.ReadErrorsUncorrected is > 0) findings.Add(L.T($"{rel.ReadErrorsUncorrected} düzeltilemeyen okuma hatası", $"{rel.ReadErrorsUncorrected} uncorrectable read error(s)"));
            if (rel?.WriteErrorsUncorrected is > 0) findings.Add(L.T($"{rel.WriteErrorsUncorrected} düzeltilemeyen yazma hatası", $"{rel.WriteErrorsUncorrected} uncorrectable write error(s)"));
            foreach (var v in vols.Where(v => v.Total > 0 && v.Free * 10 < v.Total))
                findings.Add(L.T($"{v.Letter} boş alan az (%{100 - v.UsedPercent:0})", $"{v.Letter} low free space ({100 - v.UsedPercent:0}%)"));
            if (state == CheckState.Healthy && findings.Count > 0) state = CheckState.Warning;
            var text = state switch
            {
                CheckState.Healthy => L.T("Sağlıklı", "Healthy"),
                CheckState.Warning => L.T("Uyarı", "Warning"),
                CheckState.Error => L.T("Sağlıksız", "Unhealthy"),
                _ => L.T("Windows sağlık durumu bildirmedi", "Windows did not report a health status")
            };
            result.Add(new DiskHealth(d, rel, vols, state, text, findings));
        }
        logger.Info(L.T($"Depolama sağlığı okundu: {result.Count} disk ({string.Join(", ", result.Select(r => $"{r.Name}: {r.StateText}"))})", $"Storage health read: {result.Count} disk(s) ({string.Join(", ", result.Select(r => $"{r.Name}: {r.StateText}"))})") +
                    (note is null ? "." : $"; {note}"));
        return new StorageScan(result, volumes, null, note);
    }

    /// <summary>Hazır (erişilebilir) harfli sürücülerin gerçek boyut / boş alanı.</summary>
    public static IReadOnlyList<VolumeInfo> ReadVolumes()
    {
        var list = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                list.Add(new VolumeInfo(drive.Name.TrimEnd('\\'), drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // sürücü okunamadı (çıkarılmış / kilitli) – listelenmez
            }
        }
        return list;
    }
}

using System.IO;
using System.Management;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>Sistem Bilgileri panelindeki tek satır.</summary>
public sealed record SystemInfoField(string Label, string Value, bool Available, bool Primary);

/// <summary>Sistem Bilgileri panelinin anlık görüntüsü.</summary>
public sealed class SystemInfoSnapshot
{
    public required IReadOnlyList<SystemInfoField> Fields { get; init; }
    public DateTime CollectedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// Bilgisayarın temel sistem bilgilerini GERÇEK kaynaklardan okur:
/// WMI (Win32_OperatingSystem, Win32_Processor, Win32_ComputerSystem, Win32_PhysicalMemory, Win32_VideoController),
/// kayıt defteri (DisplayVersion / CurrentBuildNumber / UBR), nvidia-smi (NVIDIA sürücü sürümü),
/// DriveInfo (disk alanı) ve Environment.TickCount64 (çalışma süresi).
/// Okunamayan her alan "Bilgi alınamadı" olarak gösterilir ve nedeni günlüğe yazılır. Hiçbir değer tahmin edilmez.
/// Değişmeyen bilgiler oturumda bir kez okunur; yenilemede yalnızca sürücü sürümü, disk alanı ve çalışma süresi yeniden okunur.
/// </summary>
public sealed class SystemInfoService(Logger logger)
{
    public const string NotAvailable = "Bilgi alınamadı";

    private readonly object _lock = new();

    /// <summary>Oturum boyunca değişmeyen alanlar (işletim sistemi, CPU, RAM, GPU, mimari, bilgisayar adı) – bir kez okunur.</summary>
    private List<SystemInfoField>? _staticHead;
    private List<SystemInfoField>? _staticTail;
    private GpuInfo? _nvidiaGpu;

    /// <summary>
    /// Sistem bilgilerini döndürür. İlk çağrıda tüm alanlar gerçek kaynaklardan okunur; sonraki çağrılarda (Yenile butonu)
    /// yalnızca değişebilen alanlar (NVIDIA sürücü sürümü, disk alanı, çalışma süresi) yeniden okunur, pahalı WMI sorguları tekrarlanmaz.
    /// </summary>
    public Task<SystemInfoSnapshot> CollectAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        List<SystemInfoField>? head, tail;
        GpuInfo? rtx;
        lock (_lock)
        {
            head = _staticHead;
            tail = _staticTail;
            rtx = _nvidiaGpu;
        }

        var first = head is null || tail is null;
        if (first)
        {
            (head, tail, rtx) = ReadStatic();
            lock (_lock)
            {
                _staticHead = head;
                _staticTail = tail;
                _nvidiaGpu = rtx;
            }
        }

        // --- Değişebilen alanlar: her çağrıda GERÇEK okuma ---
        var driverField = new List<SystemInfoField>();
        string? driver = null;
        if (rtx is not null)
        {
            try { driver = await NvidiaDriverManager.ReadInstalledDriverVersionAsync(rtx, ct, logger); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.Warning($"NVIDIA sürücü sürümü okunamadı: {ex.Message}"); }
        }
        Add(driverField, "NVIDIA Driver", () => driver);

        var dynamicTail = new List<SystemInfoField>();
        DriveInfo? drive = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            drive = new DriveInfo(root);
            if (!drive.IsReady) drive = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.Warning($"Sistem sürücüsü okunamadı: {ex.Message}");
        }
        Add(dynamicTail, "Disk", () => drive is null ? null
            : $"{drive.Name.TrimEnd('\\')} {(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : drive.VolumeLabel + " ")}({drive.DriveFormat})".Replace("  ", " "));
        Add(dynamicTail, "Boş disk alanı", () => drive is null ? null : FormatBytes(drive.AvailableFreeSpace), primary: true);
        Add(dynamicTail, "Toplam disk alanı", () => drive is null ? null : FormatBytes(drive.TotalSize));
        Add(dynamicTail, "Çalışma süresi (Uptime)", () => FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)), primary: true);

        var fields = new List<SystemInfoField>();
        fields.AddRange(head!);
        fields.AddRange(driverField);
        fields.AddRange(tail!);
        fields.AddRange(dynamicTail);

        var missing = fields.Count(f => !f.Available);
        if (first)
        {
            if (missing == 0) logger.Info("Sistem bilgileri okundu.");
            else logger.Warning($"Sistem bilgileri okundu; {missing} alan alınamadı.");
        }
        else
        {
            logger.Info("Sistem bilgileri yenilendi (NVIDIA sürücü sürümü, disk alanı, çalışma süresi; değişmeyen bilgiler yeniden sorgulanmadı).");
        }

        return new SystemInfoSnapshot { Fields = fields };
    }, ct);

    private void Add(List<SystemInfoField> fields, string label, Func<string?> read, bool primary = false)
    {
        try
        {
            var value = read();
            if (string.IsNullOrWhiteSpace(value))
            {
                logger.Warning($"Sistem bilgisi alınamadı ({label}): kaynak değer döndürmedi.");
                fields.Add(new SystemInfoField(label, NotAvailable, false, primary));
            }
            else
            {
                fields.Add(new SystemInfoField(label, value.Trim(), true, primary));
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Sistem bilgisi alınamadı ({label}): {ex.Message}");
            fields.Add(new SystemInfoField(label, NotAvailable, false, primary));
        }
    }

    /// <summary>Değişmeyen alanları gerçek kaynaklardan (WMI, kayıt defteri) bir kez okur.</summary>
    private (List<SystemInfoField> Head, List<SystemInfoField> Tail, GpuInfo? Rtx) ReadStatic()
    {
        var head = new List<SystemInfoField>();

        // --- İşletim sistemi (WMI) ---
        Dictionary<string, object?>? os = null;
        try { os = QuerySingle("SELECT Caption, OSArchitecture, BuildNumber FROM Win32_OperatingSystem"); }
        catch (Exception ex) { logger.Warning($"Win32_OperatingSystem okunamadı: {ex.Message}"); }

        Add(head, "İşletim Sistemi", () => os?["Caption"]?.ToString(), primary: true);
        Add(head, "Windows sürümü", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (key?.GetValue("DisplayVersion") ?? key?.GetValue("ReleaseId"))?.ToString();
        });
        Add(head, "Windows Build", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? os?["BuildNumber"]?.ToString();
            var ubr = key?.GetValue("UBR");
            return build is null ? null : ubr is null ? build : $"{build}.{ubr}";
        }, primary: true);

        // --- CPU ---
        Add(head, "CPU", () =>
        {
            var cpu = QuerySingle("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            var name = cpu["Name"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            var cores = cpu["NumberOfCores"]?.ToString();
            var threads = cpu["NumberOfLogicalProcessors"]?.ToString();
            return cores is null ? name : $"{name} ({cores} çekirdek / {threads} iş parçacığı)";
        }, primary: true);

        // --- RAM ---
        Add(head, "RAM", () =>
        {
            ulong installed = 0;
            using (var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
            using (var res = s.Get())
            {
                foreach (ManagementObject mo in res)
                    using (mo)
                        installed += Convert.ToUInt64(mo["Capacity"] ?? 0UL);
            }
            var cs = QuerySingle("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            var usable = Convert.ToUInt64(cs["TotalPhysicalMemory"] ?? 0UL);
            if (installed == 0 && usable == 0) return null;
            return installed > 0
                ? $"{installed / 1073741824.0:0.#} GB (kullanılabilir {usable / 1073741824.0:0.0} GB)"
                : $"{usable / 1073741824.0:0.0} GB kullanılabilir";
        }, primary: true);

        // --- GPU (gereksinim kontrolünün okuduğu liste paylaşılır; WMI yeniden sorgulanmaz) ---
        List<GpuInfo> gpus = [];
        try { gpus = SystemRequirementsChecker.GetGpus(); }
        catch (Exception ex) { logger.Warning($"Win32_VideoController okunamadı: {ex.Message}"); }

        Add(head, "GPU", () => gpus.Count == 0 ? null : string.Join(" · ", gpus.Select(g => g.Name)), primary: true);
        var rtx = gpus.FirstOrDefault(g => g.IsRtx) ?? gpus.FirstOrDefault(g => g.IsNvidia);
        Add(head, "NVIDIA GPU modeli", () => rtx?.Name);

        // --- Mimari / bilgisayar adı ---
        var tail = new List<SystemInfoField>();
        Add(tail, "Sistem mimarisi", () =>
        {
            var arch = os?["OSArchitecture"]?.ToString();
            var rt = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            return arch is null ? rt : $"{arch} ({rt})";
        });
        Add(tail, "Bilgisayar adı", () => Environment.MachineName);

        return (head, tail, rtx);
    }

    private static Dictionary<string, object?> QuerySingle(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in mo.Properties) dict[p.Name] = p.Value;
                return dict;
            }
        }
        throw new InvalidOperationException($"WMI sorgusu sonuç döndürmedi: {wql}");
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.00} TB" : $"{bytes / (double)(1L << 30):0.0} GB";

    public static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} gün {t.Hours} saat";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} saat {t.Minutes} dk";
        return $"{Math.Max(0, t.Minutes)} dk";
    }
}

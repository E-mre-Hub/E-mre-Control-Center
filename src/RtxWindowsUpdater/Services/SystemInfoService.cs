using System.IO;
using System.Management;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>Sistem Bilgileri panelindeki tek satır. <paramref name="Group"/>: Cihaz Bilgileri'nde hangi kartta gösterileceği.</summary>
public sealed record SystemInfoField(string Label, string Value, bool Available, bool Primary, string Group = "");

/// <summary>Cihaz Bilgileri kartları (Monster tarzı gruplama).</summary>
public static class SystemInfoGroups
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Ram = "ram";
    public const string Storage = "storage";
    public const string Os = "os";
}

/// <summary>Sistem Bilgileri panelinin anlık görüntüsü.</summary>
public sealed class SystemInfoSnapshot
{
    public required IReadOnlyList<SystemInfoField> Fields { get; init; }
    public DateTime CollectedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// Bilgisayarın temel sistem bilgilerini GERÇEK kaynaklardan okur:
/// WMI (Win32_OperatingSystem, Win32_Processor, Win32_ComputerSystem, Win32_PhysicalMemory, Win32_VideoController, MSFT_PhysicalDisk),
/// kayıt defteri (DisplayVersion / CurrentBuildNumber / UBR, ekran kartı belleği), nvidia-smi (NVIDIA sürücü sürümü),
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
    /// <summary>Ekran kartı listesi gerçekten okundu ve NVIDIA kartı yok (liste okunamadıysa false: "Bilgi alınamadı").</summary>
    private bool _noNvidia;

    private const string NoNvidiaText = "NVIDIA ekran kartı yok";

    /// <summary>
    /// Sistem bilgilerini döndürür. İlk çağrıda tüm alanlar gerçek kaynaklardan okunur; sonraki çağrılarda (Yenile butonu)
    /// yalnızca değişebilen alanlar (NVIDIA sürücü sürümü, disk alanı, çalışma süresi) yeniden okunur, pahalı WMI sorguları tekrarlanmaz.
    /// </summary>
    public Task<SystemInfoSnapshot> CollectAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        List<SystemInfoField>? head, tail;
        GpuInfo? rtx;
        bool noNvidia;
        lock (_lock)
        {
            head = _staticHead;
            tail = _staticTail;
            rtx = _nvidiaGpu;
            noNvidia = _noNvidia;
        }

        var first = head is null || tail is null;
        if (first)
        {
            (head, tail, rtx, noNvidia) = ReadStatic();
            lock (_lock)
            {
                _staticHead = head;
                _staticTail = tail;
                _nvidiaGpu = rtx;
                _noNvidia = noNvidia;
            }
        }

        // --- Değişebilen alanlar: her çağrıda GERÇEK okuma ---
        var driverField = new List<SystemInfoField>();
        if (noNvidia)
        {
            // Ekran kartı listesi gerçekten okundu ve NVIDIA kartı yok: sürücü sürümü okunacak bir şey yoktur.
            driverField.Add(new SystemInfoField("NVIDIA Driver", NoNvidiaText, true, false, SystemInfoGroups.Gpu));
        }
        else
        {
            string? driver = null;
            if (rtx is not null)
            {
                try { driver = await NvidiaDriverManager.ReadInstalledDriverVersionAsync(rtx, ct, logger); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.Warning($"NVIDIA sürücü sürümü okunamadı: {ex.Message}"); }
            }
            Add(driverField, "NVIDIA Driver", () => driver, group: SystemInfoGroups.Gpu);
        }

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
            : $"{drive.Name.TrimEnd('\\')} {(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : drive.VolumeLabel + " ")}({drive.DriveFormat})".Replace("  ", " "),
            group: SystemInfoGroups.Storage);
        Add(dynamicTail, "Boş disk alanı", () => drive is null ? null : FormatBytes(drive.AvailableFreeSpace), primary: true, group: SystemInfoGroups.Storage);
        Add(dynamicTail, "Toplam disk alanı", () => drive is null ? null : FormatBytes(drive.TotalSize), group: SystemInfoGroups.Storage);
        Add(dynamicTail, "Çalışma süresi (Uptime)", () => FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)), primary: true,
            group: SystemInfoGroups.Os);

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

    private void Add(List<SystemInfoField> fields, string label, Func<string?> read, bool primary = false, string group = "")
    {
        try
        {
            var value = read();
            if (string.IsNullOrWhiteSpace(value))
            {
                logger.Warning($"Sistem bilgisi alınamadı ({label}): kaynak değer döndürmedi.");
                fields.Add(new SystemInfoField(label, NotAvailable, false, primary, group));
            }
            else
            {
                fields.Add(new SystemInfoField(label, value.Trim(), true, primary, group));
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Sistem bilgisi alınamadı ({label}): {ex.Message}");
            fields.Add(new SystemInfoField(label, NotAvailable, false, primary, group));
        }
    }

    /// <summary>Değişmeyen alanları gerçek kaynaklardan (WMI, kayıt defteri) bir kez okur.</summary>
    private (List<SystemInfoField> Head, List<SystemInfoField> Tail, GpuInfo? Rtx, bool NoNvidia) ReadStatic()
    {
        var head = new List<SystemInfoField>();

        // --- İşletim sistemi (WMI) ---
        Dictionary<string, object?>? os = null;
        try { os = QuerySingle("SELECT Caption, OSArchitecture, BuildNumber FROM Win32_OperatingSystem"); }
        catch (Exception ex) { logger.Warning($"Win32_OperatingSystem okunamadı: {ex.Message}"); }

        Add(head, "İşletim Sistemi", () => os?["Caption"]?.ToString(), primary: true, group: SystemInfoGroups.Os);
        Add(head, "Windows sürümü", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (key?.GetValue("DisplayVersion") ?? key?.GetValue("ReleaseId"))?.ToString();
        }, group: SystemInfoGroups.Os);
        Add(head, "Windows Build", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? os?["BuildNumber"]?.ToString();
            var ubr = key?.GetValue("UBR");
            return build is null ? null : ubr is null ? build : $"{build}.{ubr}";
        }, primary: true, group: SystemInfoGroups.Os);

        // --- CPU ---
        Dictionary<string, object?>? cpu = null;
        try { cpu = QuerySingle("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"); }
        catch (Exception ex) { logger.Warning($"Win32_Processor okunamadı: {ex.Message}"); }
        Add(head, "CPU", () =>
        {
            var name = cpu?["Name"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            var cores = cpu!["NumberOfCores"]?.ToString();
            var threads = cpu["NumberOfLogicalProcessors"]?.ToString();
            return cores is null ? name : $"{name} ({cores} çekirdek / {threads} iş parçacığı)";
        }, primary: true, group: SystemInfoGroups.Cpu);
        // Win32_Processor.MaxClockSpeed: işlemcinin bildirdiği temel (anma) saat hızı, MHz.
        Add(head, "Temel saat hızı", () => cpu?["MaxClockSpeed"] is { } mhz && Convert.ToDouble(mhz) > 0
            ? $"{Convert.ToDouble(mhz) / 1000:0.00} GHz" : null, group: SystemInfoGroups.Cpu);

        // --- RAM (Win32_PhysicalMemory: modül boyutu, hızı, üretici / parça numarası) ---
        var modules = new List<(ulong Capacity, uint Speed, string Maker, string Part)>();
        string? moduleError = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            using var res = s.Get();
            foreach (ManagementObject mo in res)
            {
                using (mo)
                {
                    var configured = mo["ConfiguredClockSpeed"] is { } c ? Convert.ToUInt32(c) : 0u;
                    var speed = configured > 0 ? configured : mo["Speed"] is { } sp ? Convert.ToUInt32(sp) : 0u;
                    modules.Add((Convert.ToUInt64(mo["Capacity"] ?? 0UL), speed,
                        (mo["Manufacturer"]?.ToString() ?? string.Empty).Trim(), (mo["PartNumber"]?.ToString() ?? string.Empty).Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            moduleError = ex.Message;
            logger.Warning($"Win32_PhysicalMemory okunamadı: {ex.Message}");
        }
        Add(head, "RAM", () =>
        {
            var installed = modules.Aggregate(0UL, (sum, m) => sum + m.Capacity);
            var cs = QuerySingle("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            var usable = Convert.ToUInt64(cs["TotalPhysicalMemory"] ?? 0UL);
            if (installed == 0 && usable == 0) return null;
            return installed > 0
                ? $"{installed / 1073741824.0:0.#} GB (kullanılabilir {usable / 1073741824.0:0.0} GB)"
                : $"{usable / 1073741824.0:0.0} GB kullanılabilir";
        }, primary: true, group: SystemInfoGroups.Ram);
        Add(head, "Bellek modülleri", () => moduleError is not null || modules.Count == 0 ? null
            : string.Join("\n", modules.GroupBy(m => (m.Capacity, m.Maker, m.Part))
                .Select(g => $"{g.Count()} × {g.Key.Capacity / 1073741824.0:0.#} GB" + DescribeModule(g.Key.Maker, g.Key.Part))),
            group: SystemInfoGroups.Ram);
        Add(head, "Bellek hızı", () =>
        {
            var speeds = modules.Where(m => m.Speed > 0).Select(m => m.Speed).Distinct().ToList();
            return speeds.Count == 0 ? null : string.Join(" / ", speeds.Select(v => $"{v} MHz"));
        }, group: SystemInfoGroups.Ram);

        // --- GPU (gereksinim kontrolünün okuduğu liste paylaşılır; WMI yeniden sorgulanmaz) ---
        List<GpuInfo> gpus = [];
        var gpusRead = false;
        try { gpus = SystemRequirementsChecker.GetGpus(); gpusRead = true; }
        catch (Exception ex) { logger.Warning($"Win32_VideoController okunamadı: {ex.Message}"); }

        Add(head, "GPU", () => gpus.Count == 0 ? null : string.Join(" · ", gpus.Select(g => g.Name)), primary: true, group: SystemInfoGroups.Gpu);
        var rtx = gpus.FirstOrDefault(g => g.IsRtx) ?? gpus.FirstOrDefault(g => g.IsNvidia);
        var noNvidia = gpusRead && gpus.Count > 0 && rtx is null;
        if (noNvidia) head.Add(new SystemInfoField("NVIDIA GPU modeli", NoNvidiaText, true, false, SystemInfoGroups.Gpu));
        else Add(head, "NVIDIA GPU modeli", () => rtx?.Name, group: SystemInfoGroups.Gpu);
        Add(head, "Ekran kartı belleği", () => ReadGpuMemory(gpus), group: SystemInfoGroups.Gpu);

        // --- Mimari / bilgisayar adı / depolama aygıtları ---
        var tail = new List<SystemInfoField>();
        Add(tail, "Sistem mimarisi", () =>
        {
            var arch = os?["OSArchitecture"]?.ToString();
            var rt = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            return arch is null ? rt : $"{arch} ({rt})";
        }, group: SystemInfoGroups.Os);
        Add(tail, "Bilgisayar adı", () => Environment.MachineName, group: SystemInfoGroups.Os);
        Add(tail, "Depolama aygıtları", ReadStorageDevices, group: SystemInfoGroups.Storage);

        return (head, tail, rtx, noNvidia);
    }

    /// <summary>Bellek modülünün üretici / parça numarası (Windows'un bildirdiği gibi; boş veya "Unknown" ise gösterilmez).</summary>
    private static string DescribeModule(string maker, string part)
    {
        var parts = new[] { maker, part }
            .Where(p => !string.IsNullOrWhiteSpace(p) && !p.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                        && !p.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return parts.Count == 0 ? string.Empty : $" ({string.Join(" ", parts)})";
    }

    /// <summary>
    /// Ekran kartlarının ayrılmış belleği: ekran bağdaştırıcısı sürücü anahtarındaki HardwareInformation.qwMemorySize
    /// (64 bit). WMI'daki AdapterRAM 4 GB'ta takıldığı için kullanılmaz. Yalnızca Win32_VideoController'daki kartlar eşleştirilir.
    /// </summary>
    private static string? ReadGpuMemory(IReadOnlyList<GpuInfo> gpus)
    {
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return null;
        var lines = new List<string>();
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            RegistryKey? key;
            try { key = cls.OpenSubKey(sub); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { continue; }
            using (key)
            {
                var desc = key?.GetValue("DriverDesc")?.ToString()?.Trim();
                if (string.IsNullOrEmpty(desc) || !gpus.Any(g => string.Equals(g.Name, desc, StringComparison.OrdinalIgnoreCase))) continue;
                var raw = key!.GetValue("HardwareInformation.qwMemorySize") ?? key.GetValue("HardwareInformation.MemorySize");
                long bytes = raw switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0
                };
                if (bytes > 0 && !lines.Any(l => l.StartsWith(desc + ":", StringComparison.Ordinal)))
                    lines.Add($"{desc}: {(bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB")}");
            }
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Fiziksel diskler: Windows depolama yönetimi (MSFT_PhysicalDisk) – model, SSD/HDD, bağlantı türü, boyut.</summary>
    private static string? ReadStorageDevices()
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk"));
        using var results = searcher.Get();
        var lines = new List<string>();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                var name = mo["FriendlyName"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var parts = new List<string> { name };
                var media = Convert.ToUInt16(mo["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
                if (media is not null) parts.Add(media);
                var bus = Convert.ToUInt16(mo["BusType"] ?? 0) switch
                {
                    3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
                    17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => null
                };
                if (bus is not null) parts.Add(bus);
                if (mo["Size"] is { } size && Convert.ToUInt64(size) > 0) parts.Add(FormatBytes((long)Convert.ToUInt64(size)));
                lines.Add(string.Join(" · ", parts));
            }
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
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

using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>Tek bir canlı ölçüm. <see cref="Value"/> null ise okunamadı; <see cref="Note"/> gerçek nedeni söyler.</summary>
public sealed record DeviceReading(double? Value, string? Note = null)
{
    public static DeviceReading Missing(string note) => new(null, note);
}

public sealed record GpuStatus(string Name, DeviceReading Usage, DeviceReading Temperature, DeviceReading MemoryUsage, DeviceReading Fan,
    ulong? MemoryUsedBytes = null, ulong? MemoryTotalBytes = null);

public sealed record DiskStatus(string Id, string Name, DeviceReading Temperature);

/// <summary>Fiziksel diskin anlık etkinliği (Windows "PhysicalDisk" performans sayaçları – Görev Yöneticisi ile aynı kaynak).</summary>
public sealed record DiskActivity(string Id, string Instance, DeviceReading ActiveTime, DeviceReading ReadBytesPerSec, DeviceReading WriteBytesPerSec);

/// <summary>Bağlı ağ bağdaştırıcılarının toplam anlık aktarımı (NetworkInterface bayt sayaçları farkı).</summary>
public sealed record NetworkThroughput(DeviceReading ReceiveBytesPerSec, DeviceReading SendBytesPerSec, string Adapters);

public sealed record FanStatus(string Name, DeviceReading Rpm);

/// <summary>Cihaz Durumu ekranının bir ölçüm anı (tüm değerler gerçek kaynaklardan).</summary>
public sealed class DeviceStatusSnapshot
{
    public required DeviceReading CpuUsage { get; init; }
    public required DeviceReading ThermalZone { get; init; }
    public required DeviceReading MemoryUsage { get; init; }
    public ulong MemoryUsedBytes { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public required IReadOnlyList<GpuStatus> Gpus { get; init; }
    public string? GpuNote { get; init; }
    public required IReadOnlyList<DiskStatus> Disks { get; init; }
    public string? DiskNote { get; init; }
    public required IReadOnlyList<FanStatus> Fans { get; init; }
    public IReadOnlyList<DiskActivity> DiskActivity { get; init; } = [];
    public string? DiskActivityNote { get; init; }
    public NetworkThroughput? Network { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>
/// Cihaz Durumu (canlı) ölçümleri. Sistemi değiştirmez; yalnızca okur:
///  - İşlemci kullanımı: kernel32 GetSystemTimes (iki ölçüm arasındaki boşta / toplam süre farkı).
///  - Bellek: kernel32 GlobalMemoryStatusEx.
///  - Termal bölge: Windows "Thermal Zone Information" sayacı (ACPI). İşlemci çekirdek sıcaklığı DEĞİLDİR; Windows standart bir
///    CPU sıcaklığı arayüzü sunmaz. Bölge bildirilmiyorsa "okunamıyor".
///  - NVIDIA ekran kartı: sürücüyle gelen resmi NVML kitaplığı (nvml.dll) – kullanım, sıcaklık, bellek, fan.
///  - Disk sıcaklığı: Windows depolama güvenilirlik sayaçları (MSFT_StorageReliabilityCounter; yönetici yetkisi gerekir), 15 sn'de bir.
///  - Fan: Win32_Fan (çoğu dizüstünde üretici yazılımı olmadan bildirilmez).
/// Okunamayan her değer tahmin edilmeden "okunamıyor" + gerçek neden olarak döner.
/// </summary>
public sealed class DeviceMonitorService(Logger logger) : IDisposable
{
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly HashSet<string> _loggedNotes = [];
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;
    private bool _nvmlReady;
    private string? _nvmlError;
    private DateTime _lastSlowRead = DateTime.MinValue;
    private (IReadOnlyList<DiskStatus> Disks, string? DiskNote, IReadOnlyList<FanStatus> Fans) _slowCache = ([], null, []);

    /// <summary>Tek ölçüm (arka plan iş parçacığında çağrılır; aynı anda yalnızca bir ölçüm yapılır).</summary>
    public DeviceStatusSnapshot Sample()
    {
        lock (_lock)
        {
            PrimeRates(); // ilk ölçümde sayaçların başlangıç değeri (işlemci ölçümünün 300 ms beklemesi fark aralığı olur)
            var cpu = ReadCpuUsage();
            var (memory, used, total) = ReadMemory();
            var thermal = ReadThermalZone();
            var (gpus, gpuNote) = ReadGpus();
            var (activity, activityNote) = ReadDiskActivity();
            var network = ReadNetwork();
            if (DateTime.UtcNow - _lastSlowRead >= SlowInterval)
            {
                var (disks, diskNote) = ReadDisks();
                _slowCache = (disks, diskNote, ReadFans());
                _lastSlowRead = DateTime.UtcNow;
            }

            var snapshot = new DeviceStatusSnapshot
            {
                CpuUsage = cpu,
                ThermalZone = thermal,
                MemoryUsage = memory,
                MemoryUsedBytes = used,
                MemoryTotalBytes = total,
                Gpus = gpus,
                GpuNote = gpuNote,
                Disks = _slowCache.Disks,
                DiskNote = _slowCache.DiskNote,
                Fans = _slowCache.Fans,
                DiskActivity = activity,
                DiskActivityNote = activityNote,
                Network = network
            };
            LogNotesOnce(snapshot);
            return snapshot;
        }
    }

    /// <summary>İzleme durdu (ekran kapandı): NVML serbest bırakılır, ekran kartı yeniden uyku durumuna geçebilir.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_nvmlReady)
            {
                try { Nvml.Shutdown(); } catch { /* sürücü kaldırılmış olabilir */ }
            }
            _nvmlReady = false;
            _nvmlError = null;
            _hasPrev = false;
            _lastSlowRead = DateTime.MinValue;
            DisposeDiskCounters();
            _netPrev = null;
        }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ İşlemci

    private DeviceReading ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return DeviceReading.Missing($"GetSystemTimes başarısız (Win32 hata {Marshal.GetLastWin32Error()})");
        if (!_hasPrev)
        {
            // İlk ölçüm: kısa aralıklı ikinci okuma ile gerçek fark alınır.
            (_prevIdle, _prevKernel, _prevUser, _hasPrev) = (idle, kernel, user, true);
            Thread.Sleep(300);
            if (!GetSystemTimes(out idle, out kernel, out user))
                return DeviceReading.Missing($"GetSystemTimes başarısız (Win32 hata {Marshal.GetLastWin32Error()})");
        }
        var idleDelta = idle - _prevIdle;
        var totalDelta = kernel - _prevKernel + (user - _prevUser); // kernel süresi boşta süresini de içerir
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        if (totalDelta <= 0) return DeviceReading.Missing("Ölçüm aralığı çok kısa");
        return new DeviceReading(Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100));
    }

    // ------------------------------------------------------------------ Bellek

    private static (DeviceReading Usage, ulong Used, ulong Total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return (DeviceReading.Missing($"GlobalMemoryStatusEx başarısız (Win32 hata {Marshal.GetLastWin32Error()})"), 0, 0);
        var used = status.TotalPhys - status.AvailPhys;
        return (new DeviceReading(100.0 * used / status.TotalPhys), used, status.TotalPhys);
    }

    // ------------------------------------------------------------------ Termal bölge (ACPI)

    private static DeviceReading ReadThermalZone()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            using var results = searcher.Get();
            double? max = null;
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    // HighPrecisionTemperature: onda bir Kelvin; Temperature: Kelvin.
                    double kelvin = mo["HighPrecisionTemperature"] is { } hp && Convert.ToDouble(hp) > 0
                        ? Convert.ToDouble(hp) / 10.0
                        : mo["Temperature"] is { } t ? Convert.ToDouble(t) : 0;
                    if (kelvin <= 0) continue;
                    var celsius = kelvin - 273.15;
                    if (celsius is < -20 or > 150) continue; // bölge geçerli bir değer bildirmiyor
                    max = max is null ? celsius : Math.Max(max.Value, celsius);
                }
            }
            return max is null
                ? DeviceReading.Missing("Windows bu cihazda ACPI termal bölge sıcaklığı bildirmiyor")
                : new DeviceReading(max);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return DeviceReading.Missing("Termal bölge sayacı okunamadı: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ NVIDIA (NVML)

    private (IReadOnlyList<GpuStatus> Gpus, string? Note) ReadGpus()
    {
        List<GpuInfo> adapters;
        try { adapters = SystemRequirementsChecker.GetGpus(); }
        catch (Exception ex) { return ([], "Ekran kartı listesi okunamadı: " + ex.Message); }
        if (!adapters.Any(g => g.IsNvidia))
            return ([], "NVIDIA ekran kartı yok. Ekran kartı kullanımı ve sıcaklığı NVIDIA sürücüsünün NVML arayüzünden okunur.");

        if (!_nvmlReady)
        {
            if (_nvmlError is not null) return ([], _nvmlError);
            try
            {
                var init = Nvml.Init();
                if (init != 0)
                {
                    _nvmlError = $"NVIDIA NVML başlatılamadı ({NvmlError(init)})";
                    return ([], _nvmlError);
                }
                _nvmlReady = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _nvmlError = "NVIDIA NVML kitaplığı (nvml.dll) bulunamadı veya yüklenemedi: " + ex.Message;
                return ([], _nvmlError);
            }
        }

        var countResult = Nvml.GetCount(out var count);
        if (countResult != 0) return ([], $"NVML ekran kartı sayısını bildirmedi ({NvmlError(countResult)})");
        var list = new List<GpuStatus>();
        for (uint i = 0; i < count; i++)
        {
            var h = Nvml.GetHandle(i, out var device);
            if (h != 0) continue;
            var nameBuffer = new byte[96];
            var name = Nvml.GetName(device, nameBuffer, (uint)nameBuffer.Length) == 0
                ? Encoding.ASCII.GetString(nameBuffer).TrimEnd('\0').Trim()
                : $"NVIDIA GPU {i}";

            var usageResult = Nvml.GetUtilization(device, out var utilization);
            if (usageResult == 999)
            {
                // NVML_ERROR_UNKNOWN: kart güç tasarrufu durumundan uyanırken geçici olabilir; bir kez kısa aralıkla yeniden denenir.
                Thread.Sleep(150);
                usageResult = Nvml.GetUtilization(device, out utilization);
            }
            var usage = usageResult == 0 ? new DeviceReading(utilization.Gpu) : DeviceReading.Missing($"Kullanım okunamadı ({NvmlError(usageResult)})");
            var tempResult = Nvml.GetTemperature(device, 0, out var temperature);
            var temp = tempResult == 0 ? new DeviceReading(temperature) : DeviceReading.Missing($"Sıcaklık okunamadı ({NvmlError(tempResult)})");
            var memResult = Nvml.GetMemory(device, out var memory);
            var mem = memResult == 0 && memory.Total > 0
                ? new DeviceReading(100.0 * memory.Used / memory.Total)
                : DeviceReading.Missing($"Bellek okunamadı ({NvmlError(memResult)})");
            ulong? vramUsed = memResult == 0 && memory.Total > 0 ? memory.Used : null;
            ulong? vramTotal = memResult == 0 && memory.Total > 0 ? memory.Total : null;
            var fanResult = Nvml.GetFanSpeed(device, out var fan);
            var fanReading = fanResult == 0
                ? new DeviceReading(fan)
                : DeviceReading.Missing(fanResult == 3 ? "Bu ekran kartı fan hızını bildirmiyor" : $"Fan hızı okunamadı ({NvmlError(fanResult)})");
            list.Add(new GpuStatus(name, usage, temp, mem, fanReading, vramUsed, vramTotal));
        }
        return list.Count == 0 ? ([], "NVML hiçbir NVIDIA ekran kartı bildirmedi") : (list, null);
    }

    private static string NvmlError(int code) => code switch
    {
        1 => "NVML başlatılmadı",
        2 => "geçersiz bağımsız değişken",
        3 => "bu kartta desteklenmiyor",
        4 => "izin yok",
        6 => "bulunamadı",
        9 => "NVIDIA sürücüsü yüklü değil",
        12 => "NVML kitaplığı bulunamadı",
        15 => "ekran kartına erişilemiyor",
        999 => "NVML bilinmeyen hata (999)",
        _ => $"NVML hata kodu {code}"
    };

    // ------------------------------------------------------------------ Disk etkinliği ve ağ aktarımı (Performans)

    private readonly List<(string Id, string Instance, System.Diagnostics.PerformanceCounter Idle,
        System.Diagnostics.PerformanceCounter Read, System.Diagnostics.PerformanceCounter Write)> _diskCounters = [];
    private string? _diskCounterError;
    private (long Received, long Sent, DateTime Time)? _netPrev;

    /// <summary>Oran sayaçlarının ilk okuması: değer ancak ikinci okumada (aradaki farktan) hesaplanır.</summary>
    private void PrimeRates()
    {
        if (_diskCounters.Count == 0 && _diskCounterError is null)
        {
            try
            {
                var category = new System.Diagnostics.PerformanceCounterCategory("PhysicalDisk");
                foreach (var instance in category.GetInstanceNames().Where(i => i != "_Total").OrderBy(i => i, StringComparer.Ordinal))
                {
                    var id = instance.Split(' ')[0];
                    var idle = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "% Idle Time", instance, readOnly: true);
                    var read = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, readOnly: true);
                    var write = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, readOnly: true);
                    idle.NextValue();
                    read.NextValue();
                    write.NextValue();
                    _diskCounters.Add((id, instance, idle, read, write));
                }
                if (_diskCounters.Count == 0) _diskCounterError = "Windows fiziksel disk performans sayacı bildirmedi";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or FormatException)
            {
                DisposeDiskCounters();
                _diskCounterError = "Disk performans sayaçları okunamadı: " + ex.Message;
            }
        }
        if (_netPrev is null)
        {
            var (rx, tx, _) = ReadNetworkTotals();
            _netPrev = (rx, tx, DateTime.UtcNow);
        }
    }

    private (IReadOnlyList<DiskActivity> Disks, string? Note) ReadDiskActivity()
    {
        if (_diskCounterError is not null) return ([], _diskCounterError);
        var list = new List<DiskActivity>();
        try
        {
            foreach (var (id, instance, idle, read, write) in _diskCounters)
            {
                // Etkin süre = 100 − boşta süre (Görev Yöneticisi "Etkin süre" ile aynı tanım).
                var active = Math.Clamp(100 - idle.NextValue(), 0, 100);
                list.Add(new DiskActivity(id, instance, new DeviceReading(active), new DeviceReading(read.NextValue()), new DeviceReading(write.NextValue())));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Disk takıldı / çıkarıldı: sayaçlar bir sonraki ölçümde yeniden oluşturulur.
            DisposeDiskCounters();
            return ([], "Disk listesi değişti; sayaçlar yeniden oluşturuluyor (" + ex.Message + ")");
        }
        return (list, null);
    }

    private void DisposeDiskCounters()
    {
        foreach (var c in _diskCounters)
        {
            c.Idle.Dispose();
            c.Read.Dispose();
            c.Write.Dispose();
        }
        _diskCounters.Clear();
        _diskCounterError = null;
    }

    private NetworkThroughput ReadNetwork()
    {
        var (rx, tx, names) = ReadNetworkTotals();
        var now = DateTime.UtcNow;
        var prev = _netPrev;
        _netPrev = (rx, tx, now);
        if (names.Length == 0)
            return new NetworkThroughput(DeviceReading.Missing("Bağlı ağ bağdaştırıcısı yok"), DeviceReading.Missing("Bağlı ağ bağdaştırıcısı yok"), string.Empty);
        var seconds = prev is null ? 0 : (now - prev.Value.Time).TotalSeconds;
        if (seconds <= 0.05 || rx < prev!.Value.Received || tx < prev.Value.Sent)
            return new NetworkThroughput(DeviceReading.Missing("Ölçülüyor"), DeviceReading.Missing("Ölçülüyor"), names);
        return new NetworkThroughput(new DeviceReading((rx - prev.Value.Received) / seconds), new DeviceReading((tx - prev.Value.Sent) / seconds), names);
    }

    /// <summary>Bağlı (Up) fiziksel / kablosuz bağdaştırıcıların toplam alınan / gönderilen baytı.</summary>
    private static (long Received, long Sent, string Names) ReadNetworkTotals()
    {
        long rx = 0, tx = 0;
        var names = new List<string>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
                names.Add(nic.Name);
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                // bu bağdaştırıcının sayacı okunamadı; diğerleri toplanır
            }
        }
        return (rx, tx, string.Join(", ", names));
    }

    // ------------------------------------------------------------------ Diskler (15 sn'de bir)

    private static (IReadOnlyList<DiskStatus> Disks, string? Note) ReadDisks()
    {
        // Ortak okuma (Core/StorageInfo): Depolama Sağlığı bölmesi de aynı sınıfları kullanır.
        var timeout = TimeSpan.FromSeconds(10);
        var (physical, rawDisks) = StorageInfo.ReadPhysicalDisks(timeout);
        if (!rawDisks.Ok)
            return ([], rawDisks.AccessDenied ? "Disk sıcaklığı yönetici yetkisi gerektirir" : "Disk sıcaklığı okunamadı: " + rawDisks.Error);
        if (physical.Count == 0) return ([], "Windows fiziksel disk bildirmedi");

        var (counters, rawCounters) = StorageInfo.ReadReliability(timeout);
        if (!rawCounters.Ok)
        {
            // Disk adları yine gösterilir (Performans ekranında etkinlik satırları); sıcaklık gerçek nedenle "okunamıyor".
            var note = rawCounters.AccessDenied ? "Disk sıcaklığı yönetici yetkisi gerektirir" : "Disk sıcaklığı okunamadı: " + rawCounters.Error;
            return (physical.Select(d => new DiskStatus(d.DeviceId, d.Name, DeviceReading.Missing(note))).ToList(), note);
        }

        var byId = counters.ToDictionary(c => c.DeviceId, StringComparer.OrdinalIgnoreCase);
        var disks = physical.Select(d => byId.TryGetValue(d.DeviceId, out var c)
                ? new DiskStatus(d.DeviceId, d.Name, c.Temperature is { } t ? new DeviceReading(t) : DeviceReading.Missing("Sürücü sıcaklık bildirmedi"))
                : new DiskStatus(d.DeviceId, d.Name, DeviceReading.Missing("Sürücü güvenilirlik sayacı bildirmedi")))
            .ToList();
        return (disks, null);
    }

    private static IReadOnlyList<FanStatus> ReadFans()
    {
        var fans = new List<FanStatus>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DesiredSpeed FROM Win32_Fan");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var rpm = mo["DesiredSpeed"] is { } s ? Convert.ToDouble(s) : 0;
                    if (rpm > 0) fans.Add(new FanStatus(mo["Name"]?.ToString() ?? "Fan", new DeviceReading(rpm)));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Win32_Fan desteklenmiyor: fan listesi boş kalır (arayüz nedeni açıklar).
        }
        return fans;
    }

    private void LogNotesOnce(DeviceStatusSnapshot s)
    {
        IEnumerable<string?> notes =
        [
            s.CpuUsage.Note, s.ThermalZone.Note, s.MemoryUsage.Note, s.GpuNote, s.DiskNote,
            .. s.Gpus.SelectMany(g => new[] { g.Usage.Note, g.Temperature.Note, g.MemoryUsage.Note, g.Fan.Note }),
            .. s.Disks.Select(d => d.Temperature.Note)
        ];
        foreach (var note in notes.OfType<string>())
            if (_loggedNotes.Add(note))
                logger.Info("Cihaz Durumu: " + note);
    }

    // ------------------------------------------------------------------ Win32 / NVML

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>NVIDIA Management Library (sürücüyle birlikte System32'ye kurulur). Yalnızca okuma işlevleri kullanılır.</summary>
    private static class Nvml
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Utilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] public static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] public static extern int GetCount(out uint count);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandle(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] public static extern int GetName(IntPtr device, byte[] name, uint length);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern int GetUtilization(IntPtr device, out Utilization utilization);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemperature(IntPtr device, int sensor, out uint temperature);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMemory(IntPtr device, out MemoryInfo memory);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")] public static extern int GetFanSpeed(IntPtr device, out uint speed);
    }
}

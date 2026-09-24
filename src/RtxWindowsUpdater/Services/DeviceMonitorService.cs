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

public sealed record GpuStatus(string Name, DeviceReading Usage, DeviceReading Temperature, DeviceReading MemoryUsage, DeviceReading Fan);

public sealed record DiskStatus(string Name, DeviceReading Temperature);

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
            var cpu = ReadCpuUsage();
            var (memory, used, total) = ReadMemory();
            var thermal = ReadThermalZone();
            var (gpus, gpuNote) = ReadGpus();
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
                Fans = _slowCache.Fans
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
            var fanResult = Nvml.GetFanSpeed(device, out var fan);
            var fanReading = fanResult == 0
                ? new DeviceReading(fan)
                : DeviceReading.Missing(fanResult == 3 ? "Bu ekran kartı fan hızını bildirmiyor" : $"Fan hızı okunamadı ({NvmlError(fanResult)})");
            list.Add(new GpuStatus(name, usage, temp, mem, fanReading));
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

    // ------------------------------------------------------------------ Diskler (15 sn'de bir)

    private static (IReadOnlyList<DiskStatus> Disks, string? Note) ReadDisks()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName FROM MSFT_PhysicalDisk")))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                    using (mo)
                        if (mo["DeviceId"]?.ToString() is { } id)
                            names[id] = mo["FriendlyName"]?.ToString()?.Trim() ?? $"Disk {id}";
            }
            if (names.Count == 0) return ([], "Windows fiziksel disk bildirmedi");

            var disks = new List<DiskStatus>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter")))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        var id = mo["DeviceId"]?.ToString();
                        if (id is null || !names.TryGetValue(id, out var name)) continue;
                        var t = mo["Temperature"] is { } v ? Convert.ToDouble(v) : 0;
                        disks.Add(new DiskStatus(name, t > 0 ? new DeviceReading(t) : DeviceReading.Missing("Sürücü sıcaklık bildirmedi")));
                        names.Remove(id);
                    }
                }
            }
            foreach (var rest in names.Values)
                disks.Add(new DiskStatus(rest, DeviceReading.Missing("Sürücü güvenilirlik sayacı bildirmedi")));
            return (disks, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return ([], "Disk sıcaklığı yönetici yetkisi gerektirir");
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return ([], "Disk sıcaklığı okunamadı: " + ex.Message);
        }
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

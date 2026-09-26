using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Çalışan işlemin bir örneklemesi. CpuPercent / GpuPercent ilk örneklemede null (iki ölçüm arası fark gerekir).</summary>
public sealed record ProcessEntry(
    int Pid,
    string Name,
    long CreateTime,
    int SessionId,
    int Threads,
    long PrivateBytes,
    double? CpuPercent,
    double? GpuPercent,
    string? Path,
    string? Publisher,
    string? ProtectedReason)
{
    public string CpuText => CpuPercent is { } c ? $"%{c:0.0}" : "—";
    public string GpuText => GpuPercent is { } g ? $"%{g:0.0}" : "—";
    public string MemoryText => Formats.Bytes(PrivateBytes);
    public string PathText => Path ?? "—";
    public string PublisherText => Publisher ?? "—";
    public bool CanEnd => ProtectedReason is null;
}

public sealed record ProcessSnapshot(IReadOnlyList<ProcessEntry> Processes, double TotalCpuPercent, string? GpuNote, string? Error);

/// <summary>
/// İşlemler: tüm işlemlerin CPU süresi / özel bellek / iş parçacığı sayısı tek sistem çağrısıyla okunur (NtQuerySystemInformation –
/// Görev Yöneticisi'nin kaynağı; yönetici gerekmez). CPU % = iki örnekleme arasındaki gerçek CPU süresi farkı; GPU % = Windows "GPU Engine"
/// performans sayaçları (Görev Yöneticisi ile aynı). Örnekleme yalnızca İşlemler ekranı açıkken yapılır. Sonlandırma yalnızca kullanıcı
/// onayıyla, korunmayan işlemde, PID + oluşturulma zamanı doğrulanarak (önce normal kapatma) yapılır.
/// </summary>
public sealed class ProcessService(Logger logger)
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe",
        "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "audiodg.exe", "spoolsv.exe",
        "MsMpEng.exe", "NisSrv.exe", "SecurityHealthService.exe", "MemCompression", "Secure System", "conhost.exe", "dllhost.exe",
        "RuntimeBroker.exe", "taskhostw.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe",
        "LogonUI.exe", "userinit.exe", "WUDFHost.exe", "dasHost.exe", "SgrmBroker.exe", "MpDefenderCoreService.exe"
    };

    private readonly Dictionary<(int Pid, long Create), long> _lastCpu = new();
    private readonly Dictionary<string, string?> _publisherCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Pid, long Create), (string? Path, string? Reason)> _identity = new();
    private readonly Dictionary<string, CounterSample> _lastGpu = new(StringComparer.Ordinal);
    private DateTime _lastSample;
    private bool _gpuUnavailable;
    private string? _gpuNote;

    /// <summary>Önceki örnekleme verisini temizler (ekran yeniden açıldığında ilk CPU değerleri "—").</summary>
    public void Reset()
    {
        lock (_lastCpu)
        {
            _lastCpu.Clear();
            _lastGpu.Clear();
            _identity.Clear();
            _lastSample = default;
        }
    }

    public Task<ProcessSnapshot> SampleAsync(bool includeGpu, CancellationToken ct) => Task.Run(() => Sample(includeGpu), ct);

    private ProcessSnapshot Sample(bool includeGpu)
    {
        List<RawProcess> raw;
        try
        {
            raw = ReadRaw();
        }
        catch (Exception ex) when (ex is Win32Exception or OutOfMemoryException or ExternalException)
        {
            return new ProcessSnapshot([], 0, null, "İşlem listesi okunamadı: " + ex.Message);
        }

        var gpu = includeGpu ? ReadGpu() : null;
        var now = DateTime.UtcNow;
        var mySession = Process.GetCurrentProcess().SessionId;
        var list = new List<ProcessEntry>(raw.Count);
        double totalCpu = 0;
        lock (_lastCpu)
        {
            var elapsedTicks = _lastSample == default ? 0 : (now - _lastSample).Ticks * Environment.ProcessorCount;
            var next = new Dictionary<(int, long), long>(raw.Count);
            foreach (var p in raw)
            {
                var key = (p.Pid, p.CreateTime);
                next[key] = p.CpuTicks;
                double? cpu = null;
                if (elapsedTicks > 0 && _lastCpu.TryGetValue(key, out var prev))
                {
                    cpu = Math.Clamp((p.CpuTicks - prev) * 100.0 / elapsedTicks, 0, 100);
                    if (p.Pid != 0) totalCpu += cpu.Value;
                }
                if (!_identity.TryGetValue(key, out var id))
                {
                    var path = p.Pid is 0 or 4 ? null : RunningAppManager.QueryImagePath(p.Pid);
                    id = (path, ProtectedReason(p, path, mySession));
                    _identity[key] = id;
                }
                list.Add(new ProcessEntry(p.Pid, p.Name, p.CreateTime, p.SessionId, p.Threads, p.PrivateBytes, cpu,
                    gpu is null ? null : Math.Min(gpu.GetValueOrDefault(p.Pid), 100),
                    id.Path, Publisher(id.Path), id.Reason));
            }
            _lastCpu.Clear();
            foreach (var kv in next) _lastCpu[kv.Key] = kv.Value;
            foreach (var gone in _identity.Keys.Where(k => !next.ContainsKey(k)).ToList()) _identity.Remove(gone);
            _lastSample = now;
        }
        return new ProcessSnapshot(list, Math.Min(totalCpu, 100), includeGpu ? _gpuNote : null, null);
    }

    /// <summary>Sonlandırılamayacak işlemin nedeni (null = kullanıcı onayıyla sonlandırılabilir).</summary>
    internal static string? ProtectedReason(RawProcess p, string? path, int mySession)
    {
        if (p.Pid is 0 or 4) return "Windows çekirdeği – korunur";
        if (p.Pid == Environment.ProcessId) return "Bu uygulama";
        if (ProtectedNames.Contains(p.Name)) return "Windows sistem işlemi – korunur";
        if (p.SessionId == 0) return "Hizmet / sistem oturumu işlemi – korunur (Servisler ekranından yönetin)";
        if (p.SessionId != mySession) return "Başka bir kullanıcı oturumunda – korunur";
        if (IsCritical(p.Pid)) return "Windows kritik işlem olarak işaretlemiş – sonlandırılırsa sistem durur";
        if (path is null) return "İşlem bilgisi okunamadı – korunur";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return "Windows bileşeni – korunur";
        return null;
    }

    /// <summary>
    /// Kullanıcının onayladığı işlemi kapatır: işlem yeniden okunur (PID + oluşturulma zamanı aynı olmalı, korumalı olmamalı), sonra
    /// mevcut <see cref="RunningAppManager.CloseAsync"/> ile önce normal kapatma, yanıt yoksa sonlandırma. Sonuç gerçek durumdur.
    /// </summary>
    public async Task<(bool Success, string Message)> EndAsync(ProcessEntry entry)
    {
        // Sonlanmış ama açık tutamacı kalan işlem listede iş parçacığı 0 olarak görünür: çalışıyor sayılmaz.
        var current = ReadRaw().FirstOrDefault(p => p.Pid == entry.Pid && p.CreateTime == entry.CreateTime && p.Threads > 0);
        if (current is null) return (true, $"{entry.Name} (PID {entry.Pid}) zaten kapanmış.");
        var path = RunningAppManager.QueryImagePath(current.Pid);
        var reason = ProtectedReason(current, path, Process.GetCurrentProcess().SessionId);
        if (reason is not null) return (false, $"{entry.Name} sonlandırılmadı: {reason}.");
        var info = new RunningProcessInfo(current.Pid, current.CreateTime, current.Name, path, "Uygulama", true, null);
        var report = await RunningAppManager.CloseAsync([info], logger);
        var text = report.FirstOrDefault() ?? "sonuç alınamadı";
        // Doğrulama: işlem listesinden gerçekten çıkmalı (Windows listeyi kısa bir gecikmeyle günceller; en fazla 3 sn beklenir).
        var gone = false;
        for (var i = 0; i < 15 && !gone; i++)
        {
            gone = !ReadRaw().Any(p => p.Pid == entry.Pid && p.CreateTime == entry.CreateTime && p.Threads > 0);
            if (!gone) await Task.Delay(200);
        }
        logger.Info($"İşlem sonlandırma: {text} (doğrulama: {(gone ? "işlem yok" : "işlem hâlâ çalışıyor")})");
        return (gone, gone ? text : text + " – işlem hâlâ çalışıyor.");
    }

    private string? Publisher(string? path)
    {
        if (path is null) return null;
        lock (_publisherCache)
        {
            if (_publisherCache.TryGetValue(path, out var cached)) return cached;
        }
        string? company = null;
        try { company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim(); }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or UnauthorizedAccessException or System.IO.IOException) { }
        if (string.IsNullOrEmpty(company)) company = null;
        lock (_publisherCache) _publisherCache[path] = company;
        return company;
    }

    // ------------------------------------------------------------------ GPU (Windows "GPU Engine" sayaçları)

    private Dictionary<int, double>? ReadGpu()
    {
        if (_gpuUnavailable) return null;
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            if (!data.Contains("Utilization Percentage")) throw new InvalidOperationException("sayaç yok");
            var util = data["Utilization Percentage"];
            var result = new Dictionary<int, double>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hadPrevious = false;
            lock (_lastCpu)
            {
                hadPrevious = _lastGpu.Count > 0;
                foreach (InstanceData inst in util.Values)
                {
                    // Örnek adı: pid_1234_luid_0x…_phys_0_eng_0_engtype_3D
                    var name = inst.InstanceName;
                    seen.Add(name);
                    var sample = inst.Sample;
                    if (_lastGpu.TryGetValue(name, out var prev))
                    {
                        var v = CounterSample.Calculate(prev, sample);
                        if (v > 0 && TryPid(name, out var pid))
                        {
                            // Görev Yöneticisi gibi: işlemin en yoğun motoru (motorlar toplanmaz).
                            result[pid] = Math.Max(result.GetValueOrDefault(pid), v);
                        }
                    }
                    _lastGpu[name] = sample;
                }
                foreach (var gone in _lastGpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastGpu.Remove(gone);
            }
            _gpuNote = null;
            return hadPrevious ? result : null; // ilk okumada fark yok: değer gösterilmez
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or FormatException)
        {
            _gpuUnavailable = true;
            _gpuNote = "İşlem başına GPU kullanımı okunamadı (Windows \"GPU Engine\" performans sayaçları): " + ex.Message;
            logger.Warning(_gpuNote);
            return null;
        }

        static bool TryPid(string name, out int pid)
        {
            pid = 0;
            if (!name.StartsWith("pid_", StringComparison.Ordinal)) return false;
            var end = name.IndexOf('_', 4);
            return end > 4 && int.TryParse(name.AsSpan(4, end - 4), out pid);
        }
    }

    // ------------------------------------------------------------------ NtQuerySystemInformation

    internal sealed record RawProcess(int Pid, string Name, long CreateTime, int SessionId, int Threads, long PrivateBytes, long CpuTicks);

    /// <summary>SYSTEM_PROCESS_INFORMATION (x64) listesi: tek çağrıda tüm işlemler.</summary>
    internal static List<RawProcess> ReadRaw()
    {
        const int SystemProcessInformation = 5;
        const uint StatusInfoLengthMismatch = 0xC0000004;
        var size = 1 << 20;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 65536);
                    continue;
                }
                if (status != 0) throw new Win32Exception(RtlNtStatusToDosError(status));
                var list = new List<RawProcess>(512);
                var offset = 0;
                while (true)
                {
                    var p = buffer + offset;
                    var next = Marshal.ReadInt32(p, 0x00);
                    var threads = Marshal.ReadInt32(p, 0x04);
                    var privateWs = Marshal.ReadInt64(p, 0x08);
                    var create = Marshal.ReadInt64(p, 0x20);
                    var user = Marshal.ReadInt64(p, 0x28);
                    var kernel = Marshal.ReadInt64(p, 0x30);
                    var nameLen = (ushort)Marshal.ReadInt16(p, 0x38);
                    var namePtr = Marshal.ReadIntPtr(p, 0x40);
                    var pid = (int)Marshal.ReadInt64(p, 0x50);
                    var session = Marshal.ReadInt32(p, 0x64);
                    var name = namePtr == IntPtr.Zero ? (pid == 0 ? "Idle" : $"PID {pid}") : Marshal.PtrToStringUni(namePtr, nameLen / 2);
                    list.Add(new RawProcess(pid, name, create, session, threads, privateWs, user + kernel));
                    if (next == 0) break;
                    offset += next;
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new Win32Exception("İşlem listesi arabelleği yetersiz kaldı.");
    }

    private static bool IsCritical(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return false;
        try { return IsProcessCritical(h, out var critical) && critical; }
        finally { CloseHandle(h); }
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(uint status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(IntPtr process, out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

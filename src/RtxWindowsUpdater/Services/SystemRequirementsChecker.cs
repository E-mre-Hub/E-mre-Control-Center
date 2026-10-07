using System.Management;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows 11 (build 22000+), NVIDIA RTX GPU ve yönetici yetkisini gerçek sistemden okur.
/// Windows 11 zorunludur; RTX yoksa uygulama kartsız kullanılır (NVIDIA Driver kartı kullanım dışı).
/// </summary>
public sealed class SystemRequirementsChecker(Logger logger)
{
    public const int Windows11MinBuild = 22000;

    public Task<RequirementsResult> CheckAsync() => Task.Run(Check);

    private RequirementsResult Check()
    {
        logger.Info(L.T("Sistem kontrol ediliyor...", "Checking the system..."));

        // --- Windows 11 ---
        // .NET 5+ Environment.OSVersion gerçek sürümü döndürür (uyumluluk katmanı yok).
        var ver = Environment.OSVersion.Version;
        var build = ver.Build;
        var ubr = ReadUbr();
        var isWin11 = OperatingSystem.IsWindows() && ver.Major == 10 && build >= Windows11MinBuild;
        var caption = ReadOsCaption() ?? "Windows";
        var osText = L.T($"{caption} (Derleme {build}{(ubr is null ? "" : "." + ubr)})", $"{caption} (Build {build}{(ubr is null ? "" : "." + ubr)})");

        if (isWin11) logger.Success(L.T($"Windows 11 tespit edildi: {osText}", $"Windows 11 detected: {osText}"));
        else logger.Error(L.T($"Windows 11 değil: {osText}", $"Not Windows 11: {osText}"));

        // --- GPU ---
        string? gpuError = null;
        List<GpuInfo> gpus;
        try
        {
            gpus = GetGpus();
        }
        catch (Exception ex)
        {
            gpus = [];
            gpuError = L.T($"Ekran kartı bilgisi okunamadı (WMI): {ex.Message}", $"Could not read graphics card information (WMI): {ex.Message}");
            logger.Error(gpuError);
        }

        foreach (var g in gpus)
            logger.Info(L.T($"Ekran kartı bulundu: {g.Name}", $"Graphics card found: {g.Name}"));

        var rtx = gpus.FirstOrDefault(g => g.IsRtx);
        var nvidiaNonRtx = gpus.FirstOrDefault(g => g.IsNvidia && !g.IsRtx);
        string gpuText;
        if (rtx is not null)
        {
            gpuText = rtx.Name;
            logger.Success(L.T($"NVIDIA RTX GPU tespit edildi: {rtx.Name}", $"NVIDIA RTX GPU detected: {rtx.Name}"));
        }
        else if (nvidiaNonRtx is not null)
        {
            gpuText = L.T($"{nvidiaNonRtx.Name} (RTX serisi değil)", $"{nvidiaNonRtx.Name} (not RTX series)");
            logger.Warning(L.T($"NVIDIA GPU bulundu ancak RTX serisi değil: {nvidiaNonRtx.Name}. NVIDIA Driver kartı kullanım dışı olacak.", $"NVIDIA GPU found but it is not RTX series: {nvidiaNonRtx.Name}. The NVIDIA Driver card will be unavailable."));
        }
        else
        {
            gpuText = gpuError ?? (gpus.Count == 0
                ? L.T("Ekran kartı bulunamadı", "No graphics card found")
                : L.T("NVIDIA ekran kartı yok (", "No NVIDIA graphics card (") + string.Join(", ", gpus.Select(g => g.Name)) + ")");
            if (gpuError is null) logger.Warning(L.T("NVIDIA RTX GPU bulunamadı. NVIDIA Driver kartı kullanım dışı olacak.", "No NVIDIA RTX GPU found. The NVIDIA Driver card will be unavailable."));
        }

        // --- Yönetici ---
        var isAdmin = AdminPrivilegeManager.IsElevated;
        if (isAdmin) logger.Success(L.T("Yönetici yetkisi doğrulandı.", "Administrator rights verified."));
        else logger.Warning(L.T("Uygulama şu anda yönetici yetkisiyle çalışmıyor.", "The app is currently not running with administrator rights."));

        return new RequirementsResult
        {
            IsWindows11 = isWin11,
            OsDescription = osText,
            HasRtxGpu = rtx is not null,
            GpuDescription = gpuText,
            RtxGpu = rtx,
            IsAdministrator = isAdmin,
            Error = gpuError
        };
    }

    private static readonly object GpuLock = new();
    private static List<GpuInfo>? _cachedGpus;

    /// <summary>
    /// Ekran kartı listesi (WMI Win32_VideoController) oturum boyunca BİR KEZ okunur ve paylaşılır
    /// (gereksinim kontrolü, Sistem Bilgileri ve NVIDIA kartı aynı sonucu kullanır). Değişebilen sürücü sürümü gereken
    /// yerlerde <see cref="ReadGpus"/> doğrudan çağrılır.
    /// </summary>
    public static List<GpuInfo> GetGpus()
    {
        lock (GpuLock)
        {
            if (_cachedGpus is not null) return _cachedGpus;
        }
        var list = ReadGpus();
        lock (GpuLock)
        {
            _cachedGpus ??= list;
            return _cachedGpus;
        }
    }

    /// <summary>Ekran kartlarını WMI'dan her çağrıda yeniden okur.</summary>
    public static List<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                var name = mo["Name"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new GpuInfo
                {
                    Name = name,
                    WmiDriverVersion = mo["DriverVersion"]?.ToString() ?? string.Empty,
                    PnpDeviceId = mo["PNPDeviceID"]?.ToString() ?? string.Empty
                });
            }
        }
        return list;
    }

    private static string? ReadOsCaption()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
                using (mo)
                    return mo["Caption"]?.ToString()?.Trim();
        }
        catch
        {
            // Başlık yalnızca bilgi amaçlıdır.
        }
        return null;
    }

    private static int? ReadUbr()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("UBR") is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }
}

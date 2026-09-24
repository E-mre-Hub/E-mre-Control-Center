using System.Management;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows 11 (build 22000+), NVIDIA RTX GPU ve yönetici yetkisini gerçek sistemden okur.
/// </summary>
public sealed class SystemRequirementsChecker(Logger logger)
{
    public const int Windows11MinBuild = 22000;

    public Task<RequirementsResult> CheckAsync() => Task.Run(Check);

    private RequirementsResult Check()
    {
        logger.Info("Sistem kontrol ediliyor...");

        // --- Windows 11 ---
        // .NET 5+ Environment.OSVersion gerçek sürümü döndürür (uyumluluk katmanı yok).
        var ver = Environment.OSVersion.Version;
        var build = ver.Build;
        var ubr = ReadUbr();
        var isWin11 = OperatingSystem.IsWindows() && ver.Major == 10 && build >= Windows11MinBuild;
        var caption = ReadOsCaption() ?? "Windows";
        var osText = $"{caption} (Derleme {build}{(ubr is null ? "" : "." + ubr)})";

        if (isWin11) logger.Success($"Windows 11 tespit edildi: {osText}");
        else logger.Error($"Windows 11 değil: {osText}");

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
            gpuError = $"Ekran kartı bilgisi okunamadı (WMI): {ex.Message}";
            logger.Error(gpuError);
        }

        foreach (var g in gpus)
            logger.Info($"Ekran kartı bulundu: {g.Name}");

        var rtx = gpus.FirstOrDefault(g => g.IsRtx);
        var nvidiaNonRtx = gpus.FirstOrDefault(g => g.IsNvidia && !g.IsRtx);
        string gpuText;
        if (rtx is not null)
        {
            gpuText = rtx.Name;
            logger.Success($"NVIDIA RTX GPU tespit edildi: {rtx.Name}");
        }
        else if (nvidiaNonRtx is not null)
        {
            gpuText = $"{nvidiaNonRtx.Name} (RTX serisi değil – desteklenmiyor)";
            logger.Error($"NVIDIA GPU bulundu ancak RTX serisi değil: {nvidiaNonRtx.Name}");
        }
        else
        {
            gpuText = gpuError ?? (gpus.Count == 0 ? "Ekran kartı bulunamadı" : "NVIDIA GPU bulunamadı");
            if (gpuError is null) logger.Error("NVIDIA RTX GPU bulunamadı.");
        }

        // --- Yönetici ---
        var isAdmin = AdminPrivilegeManager.IsElevated;
        if (isAdmin) logger.Success("Yönetici yetkisi doğrulandı.");
        else logger.Warning("Uygulama şu anda yönetici yetkisiyle çalışmıyor.");

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

using System.Globalization;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

public sealed record InstalledApp(
    string Name,
    string Publisher,
    string Version,
    string? InstallLocation,
    DateTime? InstallDate,
    long? SizeBytes,
    string Source)
{
    public string InstallDateText => InstallDate is { } d ? d.ToString("dd.MM.yyyy") : "—";
    public string SizeText => SizeBytes is { } s ? Formats.Bytes(s) : "—";
    public string LocationText => string.IsNullOrEmpty(InstallLocation) ? "—" : InstallLocation;
}

public sealed record AppScan(IReadOnlyList<InstalledApp> Apps, string? StoreError);

/// <summary>
/// Kurulu uygulamalar: Windows'un kaldırma kayıtları (HKLM 64 / 32 bit ve HKCU – Ayarlar → Uygulamalar ile aynı kaynak; sistem bileşenleri
/// ve güncelleme kayıtları gösterilmez) + bu kullanıcının Microsoft Store uygulamaları (PackageManager API). Hiçbir uygulama sessizce
/// kaldırılmaz: kaldırma Windows Ayarlar'ın kendi ekranına bırakılır. Güncelleme denetimi mevcut Winget modülüyle yapılır.
/// </summary>
public sealed class ApplicationService(Logger logger)
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public async Task<AppScan> ListAsync(bool includeStore, CancellationToken ct = default)
    {
        var apps = await Task.Run(ReadRegistry, ct);
        string? storeError = null;
        if (includeStore)
        {
            try
            {
                var packages = await Task.Run(StorePackages.Read, ct);
                foreach (var p in packages.Where(p => p.FromStore))
                    apps.Add(new InstalledApp(p.DisplayName, p.Publisher, p.Version, p.InstalledPath, p.InstalledDate, null, "Microsoft Store"));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                storeError = "Microsoft Store uygulamaları okunamadı: " + ex.Message;
            }
        }
        var ordered = apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        logger.Info($"Kurulu uygulamalar okundu: {ordered.Count} ({ordered.Count(a => a.Source == "Microsoft Store")} Store)" +
                    (storeError is null ? "." : $"; {storeError}"));
        return new AppScan(ordered, storeError);
    }

    private static List<InstalledApp> ReadRegistry()
    {
        var result = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (RegistryHive Hive, RegistryView View, string Source)[] roots =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64, "Masaüstü (tüm kullanıcılar)"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "Masaüstü (32 bit)"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "Masaüstü (bu kullanıcı)")
        ];
        foreach (var (hive, view, source) in roots)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var display = (key.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrEmpty(display)) continue;
                    // Ayarlar → Uygulamalar'ın da göstermediği kayıtlar: sistem bileşenleri ve güncellemeler.
                    if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (key.GetValue("ParentKeyName") is string) continue;
                    if (key.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "—";
                    if (!seen.Add(display + "|" + version)) continue;
                    DateTime? date = DateTime.TryParseExact(key.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d) ? d : null;
                    long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
                    result.Add(new InstalledApp(display, (key.GetValue("Publisher") as string)?.Trim() is { Length: > 0 } p ? p : "—", version,
                        (key.GetValue("InstallLocation") as string)?.Trim(), date, size, source));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                // Bu kök okunamadı: diğerleri yine listelenir.
            }
        }
        return result;
    }
}

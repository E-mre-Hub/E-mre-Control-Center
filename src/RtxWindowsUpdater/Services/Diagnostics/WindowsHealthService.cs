using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Windows sürüm bilgisi (Win32_OperatingSystem + kayıt defteri CurrentVersion).</summary>
public sealed record WindowsVersionInfo(string Caption, string? DisplayVersion, string Build, int? BuildNumber, string? Architecture, DateTime? InstallDate)
{
    public string Text => $"{Caption}{(DisplayVersion is null ? "" : " " + DisplayVersion)} · Derleme {Build}";
}

/// <summary>
/// Windows'un kendi durum kaynaklarından sistem sağlığı satırları: sürüm / derleme, etkinleştirme (SoftwareLicensingProduct),
/// bekleyen yeniden başlatma (CBS / Windows Update / dosya yeniden adlandırma kayıtları), kritik hizmetlerin gerçek durumu
/// (Win32_Service) ve çalışma süresi. Hiçbir ayarı değiştirmez; okunamayan satır "Kontrol edilemedi" + gerçek nedendir.
/// SFC / DISM / Windows Update / disk / sürücü / olay günlüğü satırları mevcut modüllerden gelir (<see cref="DiagnosticOrchestrator"/>).
/// </summary>
public sealed class WindowsHealthService(Logger logger)
{
    private const string WindowsAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    /// <summary>Çalışıyor olması gereken çekirdek hizmetler (durmuşsa Windows'un temel işlevleri etkilenir).</summary>
    internal static readonly (string Name, string Title)[] MustRun =
    [
        ("RpcSs", "Uzak Yordam Çağrısı (RPC)"),
        ("EventLog", "Windows Olay Günlüğü"),
        ("Winmgmt", "Windows Yönetim Araçları (WMI)"),
        ("Schedule", "Görev Zamanlayıcı"),
        ("CryptSvc", "Şifreleme Hizmetleri"),
        ("Dnscache", "DNS İstemcisi"),
        ("Dhcp", "DHCP İstemcisi"),
        ("nsi", "Ağ Deposu Arabirimi"),
        ("BFE", "Temel Filtreleme Altyapısı"),
        ("mpssvc", "Windows Güvenlik Duvarı"),
        ("ProfSvc", "Kullanıcı Profili Hizmeti"),
        ("Power", "Güç"),
        ("SamSs", "Güvenlik Hesapları Yöneticisi"),
        ("wscsvc", "Güvenlik Merkezi")
    ];

    /// <summary>İsteğe bağlı başlayan ama devre dışı bırakılırsa güncelleme / kurulumu bozan hizmetler.</summary>
    internal static readonly (string Name, string Title)[] MustNotBeDisabled =
    [
        ("wuauserv", "Windows Update"),
        ("BITS", "Arka Plan Akıllı Aktarım Hizmeti"),
        ("UsoSvc", "Güncelleme Düzenleyici Hizmeti"),
        ("TrustedInstaller", "Windows Modül Yükleyici"),
        ("msiserver", "Windows Installer")
    ];

    public Task<WindowsVersionInfo?> ReadVersionAsync(CancellationToken ct = default) => Task.Run(ReadVersion, ct);

    /// <summary>Sürüm, etkinleştirme, bekleyen yeniden başlatma, kritik hizmetler, güncelleme hizmetleri ve çalışma süresi satırları.</summary>
    public async Task<IReadOnlyList<CheckResult>> CheckAsync(CancellationToken ct = default)
    {
        var version = await Task.Run(ReadVersion, ct);
        var rows = new List<CheckResult>
        {
            version is null
                ? new CheckResult("Windows sürümü", CheckState.Unknown, "Windows sürüm bilgisi okunamadı.")
                : new CheckResult("Windows sürümü", version.BuildNumber >= SystemRequirementsChecker.Windows11MinBuild ? CheckState.Healthy : CheckState.Warning,
                    version.Text, version.Architecture is null ? null : "Mimari: " + version.Architecture)
        };
        ct.ThrowIfCancellationRequested();
        rows.Add(await Task.Run(CheckActivation, ct));
        rows.Add(await Task.Run(CheckPendingReboot, ct));
        ct.ThrowIfCancellationRequested();
        rows.AddRange(await Task.Run(CheckServices, ct));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        rows.Add(new CheckResult("Çalışma süresi", CheckState.Info,
            $"{(int)up.TotalDays} gün {up.Hours} sa {up.Minutes} dk (son başlatma {Formats.Date(DateTime.Now - up)})"));
        logger.Info("Windows sağlık satırları: " + string.Join(" | ", rows.Select(r => $"{r.Title}: {CheckStates.Text(r.State)}")));
        return rows;
    }

    private static WindowsVersionInfo? ReadVersion()
    {
        string? caption = null, arch = null;
        DateTime? installed = null;
        var os = Wmi.Query(@"\\.\root\cimv2", "SELECT Caption, OSArchitecture, InstallDate FROM Win32_OperatingSystem", Wmi.DefaultTimeout);
        if (os.Rows.FirstOrDefault() is { } row)
        {
            caption = row.Str("Caption")?.Replace("Microsoft ", "", StringComparison.Ordinal);
            arch = row.Str("OSArchitecture");
            installed = row.Date("InstallDate");
        }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null && caption is null) return null;
            var build = key?.GetValue("CurrentBuildNumber") as string ?? key?.GetValue("CurrentBuild") as string;
            var ubr = key?.GetValue("UBR") is int u ? u : (int?)null;
            int? buildNumber = int.TryParse(build, out var b) ? b : null;
            caption ??= key?.GetValue("ProductName") as string ?? "Windows";
            return new WindowsVersionInfo(caption, key?.GetValue("DisplayVersion") as string,
                build is null ? "—" : ubr is null ? build : $"{build}.{ubr}", buildNumber, arch, installed);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return caption is null ? null : new WindowsVersionInfo(caption, null, "—", null, arch, installed);
        }
    }

    private static CheckResult CheckActivation()
    {
        const string title = "Windows etkinleştirme";
        // Yalnızca durum okunur; ürün anahtarı okunmaz ve gösterilmez (PartialProductKey yalnızca filtrede).
        var r = Wmi.Query(@"\\.\root\cimv2",
            $"SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='{WindowsAppId}' AND PartialProductKey IS NOT NULL",
            TimeSpan.FromSeconds(45));
        if (!r.Ok) return new CheckResult(title, CheckState.Unknown, "Etkinleştirme durumu okunamadı.", r.Error);
        var rows = r.Rows.Select(x => (Name: x.Str("Name") ?? "Windows", Status: x.Long("LicenseStatus"))).ToList();
        if (rows.Count == 0) return new CheckResult(title, CheckState.Unknown, "Windows lisans bilgisi bildirmedi.");
        var best = rows.FirstOrDefault(x => x.Status == 1);
        if (best == default) best = rows[0];
        var (state, text) = best.Status switch
        {
            1 => (CheckState.Healthy, "Windows etkin (lisanslı)"),
            0 => (CheckState.Error, "Windows lisanssız"),
            2 => (CheckState.Warning, "İlk kurulum ek süresinde (henüz etkinleştirilmedi)"),
            3 => (CheckState.Warning, "Donanım değişikliği ek süresinde"),
            4 => (CheckState.Warning, "Orijinal olmayan lisans ek süresinde"),
            5 => (CheckState.Error, "Windows etkinleştirilmemiş (bildirim modu)"),
            6 => (CheckState.Warning, "Uzatılmış ek sürede"),
            _ => (CheckState.Unknown, $"Bilinmeyen lisans durumu ({best.Status?.ToString() ?? "—"})")
        };
        return new CheckResult(title, state, text, "Lisans: " + best.Name);
    }

    private static CheckResult CheckPendingReboot()
    {
        const string title = "Bekleyen yeniden başlatma";
        var reasons = new List<string>();
        var fileRenames = false;
        try
        {
            using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) reasons.Add("Windows bileşen güncellemesi (CBS)");
            using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (wu is not null) reasons.Add("Windows Update");
            using (var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                fileRenames = sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return new CheckResult(title, CheckState.Unknown, "Yeniden başlatma kayıtları okunamadı.", ex.Message);
        }
        if (reasons.Count > 0)
            return new CheckResult(title, CheckState.Warning, "Yeniden başlatma bekleniyor: " + string.Join(", ", reasons),
                fileRenames ? "Ayrıca yeniden başlatmada tamamlanacak dosya işlemleri var." : null);
        return fileRenames
            ? new CheckResult(title, CheckState.Info, "Güncelleme için yeniden başlatma gerekmiyor",
                "Yeniden başlatmada tamamlanacak dosya işlemleri kayıtlı (kurulum / kaldırma programları bırakır).")
            : new CheckResult(title, CheckState.Healthy, "Yeniden başlatma gerekmiyor");
    }

    private static IReadOnlyList<CheckResult> CheckServices()
    {
        var names = MustRun.Concat(MustNotBeDisabled).Select(s => $"Name='{s.Name}'");
        var r = Wmi.Query(@"\\.\root\cimv2", "SELECT Name, State, StartMode FROM Win32_Service WHERE " + string.Join(" OR ", names), Wmi.DefaultTimeout);
        if (!r.Ok)
            return
            [
                new CheckResult("Kritik Windows hizmetleri", CheckState.Unknown, "Hizmet durumları okunamadı.", r.Error, Nav.SystemTools, Nav.Services),
                new CheckResult("Güncelleme hizmetleri", CheckState.Unknown, "Hizmet durumları okunamadı.", r.Error, Nav.SystemTools, Nav.Services)
            ];
        var found = r.Rows.ToDictionary(x => x.Str("Name") ?? "", x => (State: x.Str("State"), Mode: x.Str("StartMode")), StringComparer.OrdinalIgnoreCase);

        var stopped = new List<string>();
        foreach (var (name, title) in MustRun)
        {
            if (!found.TryGetValue(name, out var s)) stopped.Add($"{title} ({name}) bulunamadı");
            else if (!string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase)) stopped.Add($"{title} ({name}): {ServiceText.State(s.State)}");
        }
        var disabled = MustNotBeDisabled
            .Where(x => found.TryGetValue(x.Name, out var s) && string.Equals(s.Mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Title} ({x.Name}) devre dışı").ToList();
        var missing = MustNotBeDisabled.Where(x => !found.ContainsKey(x.Name)).Select(x => $"{x.Title} ({x.Name}) bulunamadı").ToList();

        return
        [
            stopped.Count == 0
                ? new CheckResult("Kritik Windows hizmetleri", CheckState.Healthy, $"{MustRun.Length} çekirdek hizmetin tamamı çalışıyor", null, Nav.SystemTools, Nav.Services)
                : new CheckResult("Kritik Windows hizmetleri", CheckState.Warning, $"{stopped.Count} çekirdek hizmet çalışmıyor",
                    string.Join("\n", stopped), Nav.SystemTools, Nav.Services),
            disabled.Count + missing.Count == 0
                ? new CheckResult("Güncelleme hizmetleri", CheckState.Healthy, "Windows Update ve kurulum hizmetleri kullanılabilir", null, Nav.SystemTools, Nav.Services)
                : new CheckResult("Güncelleme hizmetleri", CheckState.Warning, "Güncelleme / kurulum hizmetlerinden biri kullanılamıyor",
                    string.Join("\n", disabled.Concat(missing)), Nav.SystemTools, Nav.Services)
        ];
    }
}

/// <summary>Win32_Service durum / başlangıç türü metinleri (Servisler ekranı ve sağlık satırları ortak kullanır).</summary>
public static class ServiceText
{
    public static string State(string? state) => state switch
    {
        "Running" => "Çalışıyor",
        "Stopped" => "Durduruldu",
        "Start Pending" => "Başlatılıyor",
        "Stop Pending" => "Durduruluyor",
        "Paused" => "Duraklatıldı",
        "Pause Pending" => "Duraklatılıyor",
        "Continue Pending" => "Sürdürülüyor",
        null => "Bilinmiyor",
        _ => state
    };

    public static string StartMode(string? mode, bool? delayed = null) => mode switch
    {
        "Auto" => delayed == true ? "Otomatik (gecikmeli)" : "Otomatik",
        "Manual" => "El ile",
        "Disabled" => "Devre dışı",
        "Boot" => "Önyükleme",
        "System" => "Sistem",
        null => "Bilinmiyor",
        _ => mode
    };
}

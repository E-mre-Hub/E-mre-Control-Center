using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Windows sürüm bilgisi (Win32_OperatingSystem + kayıt defteri CurrentVersion).</summary>
public sealed record WindowsVersionInfo(string Caption, string? DisplayVersion, string Build, int? BuildNumber, string? Architecture, DateTime? InstallDate)
{
    public string Text => L.T($"{Caption}{(DisplayVersion is null ? "" : " " + DisplayVersion)} · Derleme {Build}", $"{Caption}{(DisplayVersion is null ? "" : " " + DisplayVersion)} · Build {Build}");
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
        ("RpcSs", L.T("Uzak Yordam Çağrısı (RPC)", "Remote Procedure Call (RPC)")),
        ("EventLog", L.T("Windows Olay Günlüğü", "Windows Event Log")),
        ("Winmgmt", L.T("Windows Yönetim Araçları (WMI)", "Windows Management Instrumentation (WMI)")),
        ("Schedule", L.T("Görev Zamanlayıcı", "Task Scheduler")),
        ("CryptSvc", L.T("Şifreleme Hizmetleri", "Cryptographic Services")),
        ("Dnscache", L.T("DNS İstemcisi", "DNS Client")),
        ("Dhcp", L.T("DHCP İstemcisi", "DHCP Client")),
        ("nsi", L.T("Ağ Deposu Arabirimi", "Network Store Interface Service")),
        ("BFE", L.T("Temel Filtreleme Altyapısı", "Base Filtering Engine")),
        ("mpssvc", L.T("Windows Güvenlik Duvarı", "Windows Defender Firewall")),
        ("ProfSvc", L.T("Kullanıcı Profili Hizmeti", "User Profile Service")),
        ("Power", L.T("Güç", "Power")),
        ("SamSs", L.T("Güvenlik Hesapları Yöneticisi", "Security Accounts Manager")),
        ("wscsvc", L.T("Güvenlik Merkezi", "Security Center"))
    ];

    /// <summary>İsteğe bağlı başlayan ama devre dışı bırakılırsa güncelleme / kurulumu bozan hizmetler.</summary>
    internal static readonly (string Name, string Title)[] MustNotBeDisabled =
    [
        ("wuauserv", "Windows Update"),
        ("BITS", L.T("Arka Plan Akıllı Aktarım Hizmeti", "Background Intelligent Transfer Service")),
        ("UsoSvc", L.T("Güncelleme Düzenleyici Hizmeti", "Update Orchestrator Service")),
        ("TrustedInstaller", L.T("Windows Modül Yükleyici", "Windows Modules Installer")),
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
                ? new CheckResult(L.T("Windows sürümü", "Windows version"), CheckState.Unknown, L.T("Windows sürüm bilgisi okunamadı.", "Could not read Windows version information."))
                : new CheckResult(L.T("Windows sürümü", "Windows version"), version.BuildNumber >= SystemRequirementsChecker.Windows11MinBuild ? CheckState.Healthy : CheckState.Warning,
                    version.Text, version.Architecture is null ? null : L.T("Mimari: ", "Architecture: ") + version.Architecture)
        };
        ct.ThrowIfCancellationRequested();
        rows.Add(await Task.Run(CheckActivation, ct));
        rows.Add(await Task.Run(CheckPendingReboot, ct));
        ct.ThrowIfCancellationRequested();
        rows.AddRange(await Task.Run(CheckServices, ct));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        rows.Add(new CheckResult(L.T("Çalışma süresi", "Uptime"), CheckState.Info,
            L.T($"{(int)up.TotalDays} gün {up.Hours} sa {up.Minutes} dk (son başlatma {Formats.Date(DateTime.Now - up)})", $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min (last start {Formats.Date(DateTime.Now - up)})")));
        logger.Info(L.T("Windows sağlık satırları: ", "Windows health rows: ") + string.Join(" | ", rows.Select(r => $"{r.Title}: {CheckStates.Text(r.State)}")));
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
        var title = L.T("Windows etkinleştirme", "Windows activation");
        // Yalnızca durum okunur; ürün anahtarı okunmaz ve gösterilmez (PartialProductKey yalnızca filtrede).
        var r = Wmi.Query(@"\\.\root\cimv2",
            $"SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='{WindowsAppId}' AND PartialProductKey IS NOT NULL",
            TimeSpan.FromSeconds(45));
        if (!r.Ok) return new CheckResult(title, CheckState.Unknown, L.T("Etkinleştirme durumu okunamadı.", "Could not read the activation status."), r.Error);
        var rows = r.Rows.Select(x => (Name: x.Str("Name") ?? "Windows", Status: x.Long("LicenseStatus"))).ToList();
        if (rows.Count == 0) return new CheckResult(title, CheckState.Unknown, L.T("Windows lisans bilgisi bildirmedi.", "Windows reported no license information."));
        var best = rows.FirstOrDefault(x => x.Status == 1);
        if (best == default) best = rows[0];
        var (state, text) = best.Status switch
        {
            1 => (CheckState.Healthy, L.T("Windows etkin (lisanslı)", "Windows activated (licensed)")),
            0 => (CheckState.Error, L.T("Windows lisanssız", "Windows unlicensed")),
            2 => (CheckState.Warning, L.T("İlk kurulum ek süresinde (henüz etkinleştirilmedi)", "In the initial grace period (not activated yet)")),
            3 => (CheckState.Warning, L.T("Donanım değişikliği ek süresinde", "In the hardware change grace period")),
            4 => (CheckState.Warning, L.T("Orijinal olmayan lisans ek süresinde", "In the non-genuine grace period")),
            5 => (CheckState.Error, L.T("Windows etkinleştirilmemiş (bildirim modu)", "Windows not activated (notification mode)")),
            6 => (CheckState.Warning, L.T("Uzatılmış ek sürede", "In the extended grace period")),
            _ => (CheckState.Unknown, L.T($"Bilinmeyen lisans durumu ({best.Status?.ToString() ?? "—"})", $"Unknown license status ({best.Status?.ToString() ?? "—"})"))
        };
        return new CheckResult(title, state, text, L.T("Lisans: ", "License: ") + best.Name);
    }

    private static CheckResult CheckPendingReboot()
    {
        var title = L.T("Bekleyen yeniden başlatma", "Pending restart");
        var reasons = new List<string>();
        var fileRenames = false;
        try
        {
            using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) reasons.Add(L.T("Windows bileşen güncellemesi (CBS)", "Windows component update (CBS)"));
            using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (wu is not null) reasons.Add("Windows Update");
            using (var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                fileRenames = sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return new CheckResult(title, CheckState.Unknown, L.T("Yeniden başlatma kayıtları okunamadı.", "Could not read the restart records."), ex.Message);
        }
        if (reasons.Count > 0)
            return new CheckResult(title, CheckState.Warning, L.T("Yeniden başlatma bekleniyor: ", "Restart pending: ") + string.Join(", ", reasons),
                fileRenames ? L.T("Ayrıca yeniden başlatmada tamamlanacak dosya işlemleri var.", "There are also file operations to complete at restart.") : null);
        return fileRenames
            ? new CheckResult(title, CheckState.Info, L.T("Güncelleme için yeniden başlatma gerekmiyor", "No restart needed for updates"),
                L.T("Yeniden başlatmada tamamlanacak dosya işlemleri kayıtlı (kurulum / kaldırma programları bırakır).", "File operations to complete at restart are registered (left by setup / uninstall programs)."))
            : new CheckResult(title, CheckState.Healthy, L.T("Yeniden başlatma gerekmiyor", "No restart needed"));
    }

    private static IReadOnlyList<CheckResult> CheckServices()
    {
        var names = MustRun.Concat(MustNotBeDisabled).Select(s => $"Name='{s.Name}'");
        var r = Wmi.Query(@"\\.\root\cimv2", "SELECT Name, State, StartMode FROM Win32_Service WHERE " + string.Join(" OR ", names), Wmi.DefaultTimeout);
        if (!r.Ok)
            return
            [
                new CheckResult(L.T("Kritik Windows hizmetleri", "Critical Windows services"), CheckState.Unknown, L.T("Hizmet durumları okunamadı.", "Could not read service states."), r.Error, Nav.SystemTools, Nav.Services),
                new CheckResult(L.T("Güncelleme hizmetleri", "Update services"), CheckState.Unknown, L.T("Hizmet durumları okunamadı.", "Could not read service states."), r.Error, Nav.SystemTools, Nav.Services)
            ];
        var found = r.Rows.ToDictionary(x => x.Str("Name") ?? "", x => (State: x.Str("State"), Mode: x.Str("StartMode")), StringComparer.OrdinalIgnoreCase);

        var stopped = new List<string>();
        foreach (var (name, title) in MustRun)
        {
            if (!found.TryGetValue(name, out var s)) stopped.Add(L.T($"{title} ({name}) bulunamadı", $"{title} ({name}) not found"));
            else if (!string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase)) stopped.Add($"{title} ({name}): {ServiceText.State(s.State)}");
        }
        var disabled = MustNotBeDisabled
            .Where(x => found.TryGetValue(x.Name, out var s) && string.Equals(s.Mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            .Select(x => L.T($"{x.Title} ({x.Name}) devre dışı", $"{x.Title} ({x.Name}) disabled")).ToList();
        var missing = MustNotBeDisabled.Where(x => !found.ContainsKey(x.Name)).Select(x => L.T($"{x.Title} ({x.Name}) bulunamadı", $"{x.Title} ({x.Name}) not found")).ToList();

        return
        [
            stopped.Count == 0
                ? new CheckResult(L.T("Kritik Windows hizmetleri", "Critical Windows services"), CheckState.Healthy, L.T($"{MustRun.Length} çekirdek hizmetin tamamı çalışıyor", $"All {MustRun.Length} core services are running"), null, Nav.SystemTools, Nav.Services)
                : new CheckResult(L.T("Kritik Windows hizmetleri", "Critical Windows services"), CheckState.Warning, L.T($"{stopped.Count} çekirdek hizmet çalışmıyor", $"{stopped.Count} core service(s) not running"),
                    string.Join("\n", stopped), Nav.SystemTools, Nav.Services),
            disabled.Count + missing.Count == 0
                ? new CheckResult(L.T("Güncelleme hizmetleri", "Update services"), CheckState.Healthy, L.T("Windows Update ve kurulum hizmetleri kullanılabilir", "Windows Update and installation services are available"), null, Nav.SystemTools, Nav.Services)
                : new CheckResult(L.T("Güncelleme hizmetleri", "Update services"), CheckState.Warning, L.T("Güncelleme / kurulum hizmetlerinden biri kullanılamıyor", "One of the update / installation services is unavailable"),
                    string.Join("\n", disabled.Concat(missing)), Nav.SystemTools, Nav.Services)
        ];
    }
}

/// <summary>Win32_Service durum / başlangıç türü metinleri (Servisler ekranı ve sağlık satırları ortak kullanır).</summary>
public static class ServiceText
{
    public static string State(string? state) => state switch
    {
        "Running" => L.T("Çalışıyor", "Running"),
        "Stopped" => L.T("Durduruldu", "Stopped"),
        "Start Pending" => L.T("Başlatılıyor", "Starting"),
        "Stop Pending" => L.T("Durduruluyor", "Stopping"),
        "Paused" => L.T("Duraklatıldı", "Paused"),
        "Pause Pending" => L.T("Duraklatılıyor", "Pausing"),
        "Continue Pending" => L.T("Sürdürülüyor", "Resuming"),
        null => L.T("Bilinmiyor", "Unknown"),
        _ => state
    };

    public static string StartMode(string? mode, bool? delayed = null) => mode switch
    {
        "Auto" => delayed == true ? L.T("Otomatik (gecikmeli)", "Automatic (delayed)") : L.T("Otomatik", "Automatic"),
        "Manual" => L.T("El ile", "Manual"),
        "Disabled" => L.T("Devre dışı", "Disabled"),
        "Boot" => L.T("Önyükleme", "Boot"),
        "System" => L.T("Sistem", "System"),
        null => L.T("Bilinmiyor", "Unknown"),
        _ => mode
    };
}

using System.Diagnostics;
using System.IO;
using System.Management;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

public enum ServiceAction { Start, Stop, Restart }

/// <summary>Bir Windows hizmeti (Win32_Service – Hizmetler konsoluyla aynı kaynak).</summary>
public sealed record ServiceEntry(
    string Name,
    string DisplayName,
    string? State,
    string? StartMode,
    bool? DelayedStart,
    string? Description,
    string? PathName,
    string? Account,
    int? ProcessId,
    bool? AcceptStop,
    string? Publisher,
    string? ProtectedReason)
{
    public string StateText => ServiceText.State(State);
    public string StartModeText => ServiceText.StartMode(StartMode, DelayedStart);
    public string PathText => PathName ?? "—";
    public string PublisherText => Publisher ?? "—";
    public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? L.T("Açıklama yok", "No description") : Description!;
    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => string.Equals(StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool CanStart => !IsRunning && !IsDisabled;
    public bool CanStop => IsRunning && ProtectedReason is null && AcceptStop != false;
    public string StopBlockedText => ProtectedReason ?? (AcceptStop == false ? L.T("Hizmet durdurma isteğini kabul etmiyor", "The service does not accept stop requests") : "");
}

public sealed record ServiceScan(IReadOnlyList<ServiceEntry> Services, string? Error);

/// <summary>
/// Windows hizmetleri: listeleme (ad, görünen ad, durum, başlangıç türü, açıklama, çalıştırılabilir dosya, yayıncı) ve kullanıcı onayıyla
/// Başlat / Durdur / Yeniden başlat (Win32_Service.StartService / StopService; yönetici gerekir). Kritik hizmetler durdurulamaz
/// (ayrıca Windows'un "durdurma kabul etmiyor" bayrağına uyulur). Başlangıç türü DEĞİŞTİRİLMEZ. Sonuç, hizmetin gerçek son durumu
/// yeniden okunarak verilir.
/// </summary>
public sealed class WindowsServiceManager(Logger logger)
{
    /// <summary>Durdurulması / yeniden başlatılması Windows'u, oturumu, ağı veya güvenliği bozabilecek hizmetler.</summary>
    internal static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "EventLog", "Winmgmt", "Schedule", "CryptSvc", "Dnscache", "Dhcp", "nsi", "BFE",
        "mpssvc", "ProfSvc", "Power", "SamSs", "wscsvc", "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "BrokerInfrastructure",
        "SystemEventsBroker", "CoreMessagingRegistrar", "gpsvc", "UserManager", "StateRepository", "TimeBrokerSvc", "PlugPlay",
        "AudioEndpointBuilder", "KeyIso", "VaultSvc", "Appinfo", "NlaSvc", "netprofm", "LanmanWorkstation", "DispBrokerDesktopSvc",
        "WinHttpAutoProxySvc", "EFS", "Netlogon", "TrustedInstaller", "msiserver", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS",
        "camsvc", "tiledatamodelsvc", "WpnService", "Themes", "SENS", "ShellHWDetection", "Wcmsvc", "WlanSvc", "iphlpsvc", "mpsdrv"
    };

    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    public Task<ServiceScan> ListAsync(CancellationToken ct) => Task.Run(() =>
    {
        var r = Wmi.Query(@"\\.\root\cimv2", "SELECT * FROM Win32_Service", TimeSpan.FromSeconds(40));
        if (!r.Ok) return new ServiceScan([], L.T("Hizmetler okunamadı: ", "Could not read services: ") + r.Error);
        var publishers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var list = r.Rows.Select(row =>
        {
            var name = row.Str("Name") ?? "?";
            var path = row.Str("PathName");
            var exe = path is null ? null : StartupService.TargetFromCommand(path);
            string? publisher = null;
            if (exe is not null && !publishers.TryGetValue(exe, out publisher))
            {
                publisher = ReadPublisher(exe);
                publishers[exe] = publisher;
            }
            var pid = row.Long("ProcessId");
            return new ServiceEntry(name, row.Str("DisplayName") ?? name, row.Str("State"), row.Str("StartMode"), row.Bool("DelayedAutoStart"),
                row.Str("Description"), path, row.Str("StartName"), pid is > 0 ? (int)pid : null, row.Bool("AcceptStop"), publisher,
                ProtectedReason(name));
        }).OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        logger.Info(L.T($"Windows hizmetleri okundu: {list.Count} ({list.Count(s => s.IsRunning)} çalışıyor).", $"Windows services read: {list.Count} ({list.Count(s => s.IsRunning)} running)."));
        return new ServiceScan(list, null);
    }, ct);

    internal static string? ProtectedReason(string name) =>
        CriticalServices.Contains(name) ? L.T("Kritik Windows hizmeti – durdurulması engellendi", "Critical Windows service – stopping is blocked") : null;

    /// <summary>
    /// Kullanıcının onayladığı işlem. Durdurma / yeniden başlatma kritik hizmette reddedilir. Başarı yalnızca hizmet gerçekten istenen
    /// duruma geçtiyse (Running / Stopped yeniden okunarak) bildirilir.
    /// </summary>
    public async Task<(bool Success, string Message)> RunAsync(ServiceEntry service, ServiceAction action, CancellationToken ct)
    {
        if (!IsSafeName(service.Name)) return (false, L.T("Geçersiz hizmet adı.", "Invalid service name."));
        if (action != ServiceAction.Start && ProtectedReason(service.Name) is { } reason) return (false, reason + ".");
        if (!AdminPrivilegeManager.IsElevated) return (false, L.T("Hizmet başlatma / durdurma yönetici yetkisi gerektirir.", "Starting / stopping services requires administrator rights."));

        var label = $"{service.DisplayName} ({service.Name})";
        if (action is ServiceAction.Stop or ServiceAction.Restart)
        {
            var (ok, msg) = await InvokeAsync(service.Name, "StopService", "Stopped", ct);
            if (!ok)
            {
                logger.Warning(L.T($"Hizmet durdurulamadı: {label}: {msg}", $"The service could not be stopped: {label}: {msg}"));
                return (false, L.T("Durdurulamadı: ", "Could not stop: ") + msg);
            }
            if (action == ServiceAction.Stop)
            {
                logger.Info(L.T($"Hizmet durduruldu: {label}.", $"Service stopped: {label}."));
                return (true, L.T("Hizmet durduruldu.", "Service stopped."));
            }
        }
        var (started, startMsg) = await InvokeAsync(service.Name, "StartService", "Running", ct);
        if (!started)
        {
            logger.Warning(L.T($"Hizmet başlatılamadı: {label}: {startMsg}", $"The service could not be started: {label}: {startMsg}"));
            return (false, (action == ServiceAction.Restart ? L.T("Durduruldu ancak yeniden başlatılamadı: ", "Stopped but could not be restarted: ") : L.T("Başlatılamadı: ", "Could not start: ")) + startMsg);
        }
        logger.Info(L.T($"Hizmet {(action == ServiceAction.Restart ? "yeniden başlatıldı" : "başlatıldı")}: {label}.", $"Service {(action == ServiceAction.Restart ? "restarted" : "started")}: {label}."));
        return (true, action == ServiceAction.Restart ? L.T("Hizmet yeniden başlatıldı.", "Service restarted.") : L.T("Hizmet başlatıldı.", "Service started."));
    }

    private static Task<(bool, string)> InvokeAsync(string name, string method, string targetState, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            using var svc = new ManagementObject(new ManagementPath($@"\\.\root\cimv2:Win32_Service.Name='{name}'"));
            var code = Convert.ToUInt32(svc.InvokeMethod(method, null), System.Globalization.CultureInfo.InvariantCulture);
            if (code == 10 && targetState == "Running") return (true, L.T("zaten çalışıyor", "already running"));
            if (code != 0 && !(code == 6 && targetState == "Stopped")) return (false, ReturnText(code));
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < StateTimeout)
            {
                ct.ThrowIfCancellationRequested();
                svc.Get();
                var state = svc["State"] as string;
                if (string.Equals(state, targetState, StringComparison.OrdinalIgnoreCase)) return (true, ServiceText.State(state));
                await Task.Delay(500, ct);
            }
            svc.Get();
            return (false, L.T($"{StateTimeout.TotalSeconds:0} sn içinde istenen duruma geçmedi (şu an: {ServiceText.State(svc["State"] as string)}).", $"did not reach the requested state within {StateTimeout.TotalSeconds:0} sec (now: {ServiceText.State(svc["State"] as string)})."));
        }
        catch (ManagementException ex)
        {
            return (false, Wmi.Describe(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, Wmi.Describe(ex));
        }
    }, ct);

    internal static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 256 && name.All(c => !char.IsControl(c) && c is not ('\'' or '"' or '\\' or '/'));

    /// <summary>Win32_Service yöntem dönüş kodları (Microsoft belgesi).</summary>
    internal static string ReturnText(uint code) => code switch
    {
        1 => L.T("istek desteklenmiyor (1)", "request not supported (1)"),
        2 => L.T("erişim reddedildi – yönetici yetkisi gerekiyor (2)", "access denied – administrator rights required (2)"),
        3 => L.T("bu hizmete bağlı çalışan başka hizmetler var; önce onlar durdurulmalı (3)", "other running services depend on this service; stop them first (3)"),
        4 => L.T("geçersiz hizmet denetimi (4)", "invalid service control (4)"),
        5 => L.T("hizmet bu isteği şu anda kabul etmiyor (5)", "the service cannot accept this request right now (5)"),
        6 => L.T("hizmet çalışmıyor (6)", "the service is not running (6)"),
        7 => L.T("hizmet isteğe zamanında yanıt vermedi (7)", "the service did not respond to the request in time (7)"),
        8 => L.T("bilinmeyen hata (8)", "unknown error (8)"),
        9 => L.T("hizmet dosyası bulunamadı (9)", "service file not found (9)"),
        10 => L.T("hizmet zaten çalışıyor (10)", "the service is already running (10)"),
        11 => L.T("hizmet veritabanı kilitli (11)", "the service database is locked (11)"),
        12 => L.T("bağımlı olduğu hizmet silinmiş (12)", "a service it depends on has been deleted (12)"),
        13 => L.T("bağımlı olduğu hizmet başlatılamadı (13)", "a service it depends on could not be started (13)"),
        14 => L.T("hizmet devre dışı (14)", "the service is disabled (14)"),
        15 => L.T("hizmet hesabıyla oturum açılamadı (15)", "could not log on with the service account (15)"),
        16 => L.T("hizmet silinmek üzere işaretli (16)", "the service is marked for deletion (16)"),
        17 => L.T("hizmet iş parçacığı oluşturamadı (17)", "the service could not create a thread (17)"),
        18 => L.T("döngüsel bağımlılık (18)", "circular dependency (18)"),
        _ => L.T($"Windows hata kodu {code}", $"Windows error code {code}")
    };

    private static string? ReadPublisher(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return null;
            var c = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
            return string.IsNullOrEmpty(c) ? null : c;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

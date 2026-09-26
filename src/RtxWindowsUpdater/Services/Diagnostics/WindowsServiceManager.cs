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
    public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? "Açıklama yok" : Description!;
    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => string.Equals(StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool CanStart => !IsRunning && !IsDisabled;
    public bool CanStop => IsRunning && ProtectedReason is null && AcceptStop != false;
    public string StopBlockedText => ProtectedReason ?? (AcceptStop == false ? "Hizmet durdurma isteğini kabul etmiyor" : "");
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
        if (!r.Ok) return new ServiceScan([], "Hizmetler okunamadı: " + r.Error);
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
        logger.Info($"Windows hizmetleri okundu: {list.Count} ({list.Count(s => s.IsRunning)} çalışıyor).");
        return new ServiceScan(list, null);
    }, ct);

    internal static string? ProtectedReason(string name) =>
        CriticalServices.Contains(name) ? "Kritik Windows hizmeti – durdurulması engellendi" : null;

    /// <summary>
    /// Kullanıcının onayladığı işlem. Durdurma / yeniden başlatma kritik hizmette reddedilir. Başarı yalnızca hizmet gerçekten istenen
    /// duruma geçtiyse (Running / Stopped yeniden okunarak) bildirilir.
    /// </summary>
    public async Task<(bool Success, string Message)> RunAsync(ServiceEntry service, ServiceAction action, CancellationToken ct)
    {
        if (!IsSafeName(service.Name)) return (false, "Geçersiz hizmet adı.");
        if (action != ServiceAction.Start && ProtectedReason(service.Name) is { } reason) return (false, reason + ".");
        if (!AdminPrivilegeManager.IsElevated) return (false, "Hizmet başlatma / durdurma yönetici yetkisi gerektirir.");

        var label = $"{service.DisplayName} ({service.Name})";
        if (action is ServiceAction.Stop or ServiceAction.Restart)
        {
            var (ok, msg) = await InvokeAsync(service.Name, "StopService", "Stopped", ct);
            if (!ok)
            {
                logger.Warning($"Hizmet durdurulamadı: {label}: {msg}");
                return (false, "Durdurulamadı: " + msg);
            }
            if (action == ServiceAction.Stop)
            {
                logger.Info($"Hizmet durduruldu: {label}.");
                return (true, "Hizmet durduruldu.");
            }
        }
        var (started, startMsg) = await InvokeAsync(service.Name, "StartService", "Running", ct);
        if (!started)
        {
            logger.Warning($"Hizmet başlatılamadı: {label}: {startMsg}");
            return (false, (action == ServiceAction.Restart ? "Durduruldu ancak yeniden başlatılamadı: " : "Başlatılamadı: ") + startMsg);
        }
        logger.Info($"Hizmet {(action == ServiceAction.Restart ? "yeniden başlatıldı" : "başlatıldı")}: {label}.");
        return (true, action == ServiceAction.Restart ? "Hizmet yeniden başlatıldı." : "Hizmet başlatıldı.");
    }

    private static Task<(bool, string)> InvokeAsync(string name, string method, string targetState, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            using var svc = new ManagementObject(new ManagementPath($@"\\.\root\cimv2:Win32_Service.Name='{name}'"));
            var code = Convert.ToUInt32(svc.InvokeMethod(method, null), System.Globalization.CultureInfo.InvariantCulture);
            if (code == 10 && targetState == "Running") return (true, "zaten çalışıyor");
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
            return (false, $"{StateTimeout.TotalSeconds:0} sn içinde istenen duruma geçmedi (şu an: {ServiceText.State(svc["State"] as string)}).");
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
        1 => "istek desteklenmiyor (1)",
        2 => "erişim reddedildi – yönetici yetkisi gerekiyor (2)",
        3 => "bu hizmete bağlı çalışan başka hizmetler var; önce onlar durdurulmalı (3)",
        4 => "geçersiz hizmet denetimi (4)",
        5 => "hizmet bu isteği şu anda kabul etmiyor (5)",
        6 => "hizmet çalışmıyor (6)",
        7 => "hizmet isteğe zamanında yanıt vermedi (7)",
        8 => "bilinmeyen hata (8)",
        9 => "hizmet dosyası bulunamadı (9)",
        10 => "hizmet zaten çalışıyor (10)",
        11 => "hizmet veritabanı kilitli (11)",
        12 => "bağımlı olduğu hizmet silinmiş (12)",
        13 => "bağımlı olduğu hizmet başlatılamadı (13)",
        14 => "hizmet devre dışı (14)",
        15 => "hizmet hesabıyla oturum açılamadı (15)",
        16 => "hizmet silinmek üzere işaretli (16)",
        17 => "hizmet iş parçacığı oluşturamadı (17)",
        18 => "döngüsel bağımlılık (18)",
        _ => $"Windows hata kodu {code}"
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

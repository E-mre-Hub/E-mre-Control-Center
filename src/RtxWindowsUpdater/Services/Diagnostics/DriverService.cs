using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Bir aygıt sürücüsü (Win32_PnPSignedDriver + Win32_PnPEntity; Aygıt Yöneticisi ile aynı kaynak).</summary>
public sealed record DriverInfo(
    string DeviceName,
    string Manufacturer,
    string Provider,
    string Version,
    DateTime? Date,
    string DeviceClass,
    string Category,
    bool Important,
    bool? Signed,
    int? ProblemCode,
    string StatusText,
    CheckState State)
{
    public string DateText => Date is { } d ? d.ToString("dd.MM.yyyy") : "—";
    public string SignedText => Signed switch { true => L.T("İmzalı", "Signed"), false => L.T("İmzasız", "Unsigned"), _ => "—" };
}

public sealed record DriverScan(IReadOnlyList<DriverInfo> Drivers, string? Error)
{
    public int ProblemCount => Drivers.Count(d => d.State is CheckState.Error or CheckState.Warning);
}

/// <summary>Windows Update'te (resmî Microsoft kataloğu) bulunan, henüz kurulmamış sürücü güncellemesi.</summary>
public sealed record DriverUpdate(string Title, string Model, string Manufacturer, string DriverClass, DateTime? Date);

public sealed record DriverUpdateScan(IReadOnlyList<DriverUpdate> Updates, string? Error);

/// <summary>
/// Sürücü merkezi: sistemdeki gerçek aygıt sürücüleri ve Aygıt Yöneticisi hata kodları. Güncelleme denetimi yalnızca resmî kaynaktan
/// (Windows Update sürücü kataloğu – Microsoft Update Agent); rastgele sürücü siteleri kullanılmaz ve hiçbir sürücü kurulmaz.
/// NVIDIA ekran sürücüsü uygulamanın mevcut NVIDIA Driver kartıyla (NVIDIA'nın resmî servisi) denetlenir.
/// </summary>
public sealed class DriverService(Logger logger)
{
    private static readonly TimeSpan UpdateSearchTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Aygıt sınıfı → Türkçe kategori ve "önemli" (varsayılan listede gösterilir) bilgisi.</summary>
    private static (string Category, bool Important) Classify(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" => (L.T("Ekran kartı", "Graphics card"), true),
        "NET" => (L.T("Ağ", "Network"), true),
        "MEDIA" => (L.T("Ses", "Audio"), true),
        "BLUETOOTH" => ("Bluetooth", true),
        "HDC" or "SCSIADAPTER" => (L.T("Depolama denetleyicisi", "Storage controller"), true),
        "DISKDRIVE" => ("Disk", true),
        "USB" => ("USB", true),
        "SYSTEM" => (L.T("Yonga seti / sistem", "Chipset / system"), true),
        "CAMERA" or "IMAGE" => (L.T("Kamera", "Camera"), true),
        "BIOMETRIC" => (L.T("Biyometrik", "Biometric"), true),
        "SECURITYDEVICES" => (L.T("Güvenlik aygıtı (TPM)", "Security device (TPM)"), true),
        "FIRMWARE" => (L.T("Ürün yazılımı", "Firmware"), true),
        "MONITOR" => (L.T("Monitör", "Monitor"), false),
        "HIDCLASS" or "KEYBOARD" or "MOUSE" => (L.T("Giriş aygıtı", "Input device"), false),
        "AUDIOENDPOINT" => (L.T("Ses uç noktası", "Audio endpoint"), false),
        "PROCESSOR" => (L.T("İşlemci", "Processor"), false),
        "BATTERY" => (L.T("Pil", "Battery"), false),
        "PRINTER" or "PRINTQUEUE" => (L.T("Yazıcı", "Printer"), false),
        "SOFTWARECOMPONENT" or "SOFTWAREDEVICE" => (L.T("Yazılım bileşeni", "Software component"), false),
        _ => (string.IsNullOrEmpty(cls) ? L.T("Diğer", "Other") : cls, false)
    };

    /// <summary>Aygıt Yöneticisi hata kodları (Microsoft belgelerindeki anlamları).</summary>
    public static string DescribeProblem(int code) => code switch
    {
        0 => L.T("Çalışıyor", "Working"),
        1 => L.T("Kod 1: aygıt doğru yapılandırılmamış", "Code 1: the device is not configured correctly"),
        3 => L.T("Kod 3: sürücü bozuk olabilir veya bellek yetersiz", "Code 3: the driver may be corrupted or memory is low"),
        10 => L.T("Kod 10: aygıt başlatılamıyor", "Code 10: the device cannot start"),
        12 => L.T("Kod 12: yeterli boş kaynak bulunamadı", "Code 12: not enough free resources"),
        14 => L.T("Kod 14: bilgisayar yeniden başlatılmalı", "Code 14: the computer must be restarted"),
        18 => L.T("Kod 18: sürücüler yeniden yüklenmeli", "Code 18: the drivers must be reinstalled"),
        19 => L.T("Kod 19: kayıt defteri yapılandırma bilgisi bozuk", "Code 19: registry configuration information is corrupted"),
        21 => L.T("Kod 21: Windows aygıtı kaldırıyor", "Code 21: Windows is removing the device"),
        22 => L.T("Kod 22: aygıt devre dışı bırakılmış", "Code 22: the device is disabled"),
        24 => L.T("Kod 24: aygıt yok veya düzgün çalışmıyor", "Code 24: the device is not present or not working properly"),
        28 => L.T("Kod 28: sürücü yüklü değil", "Code 28: the driver is not installed"),
        29 => L.T("Kod 29: aygıt ürün yazılımınca devre dışı", "Code 29: the device is disabled by its firmware"),
        31 => L.T("Kod 31: sürücü yüklenemedi", "Code 31: the driver could not be loaded"),
        32 => L.T("Kod 32: sürücü hizmeti devre dışı", "Code 32: the driver service is disabled"),
        37 => L.T("Kod 37: sürücü başlatılamadı", "Code 37: the driver could not be initialized"),
        38 => L.T("Kod 38: sürücünün önceki örneği hâlâ bellekte", "Code 38: a previous instance of the driver is still in memory"),
        39 => L.T("Kod 39: sürücü bozuk veya eksik", "Code 39: the driver is corrupted or missing"),
        43 => L.T("Kod 43: aygıt sorun bildirdi, Windows durdurdu", "Code 43: the device reported a problem and Windows stopped it"),
        45 => L.T("Kod 45: aygıt şu anda bağlı değil", "Code 45: the device is not connected right now"),
        48 => L.T("Kod 48: sürücü uyumsuzluk nedeniyle engellendi", "Code 48: the driver was blocked due to incompatibility"),
        52 => L.T("Kod 52: sürücünün dijital imzası doğrulanamadı", "Code 52: the driver's digital signature could not be verified"),
        _ => L.T($"Kod {code}: aygıt sorunu (Aygıt Yöneticisi'nde ayrıntı)", $"Code {code}: device problem (details in Device Manager)")
    };

    public async Task<DriverScan> ScanAsync(CancellationToken ct = default)
    {
        var drivers = await Wmi.QueryAsync(@"root\cimv2",
            "SELECT DeviceID, DeviceName, Manufacturer, DriverProviderName, DriverVersion, DriverDate, DeviceClass, IsSigned FROM Win32_PnPSignedDriver",
            ct, TimeSpan.FromSeconds(60));
        if (!drivers.Ok) return new DriverScan([], L.T("Sürücü listesi okunamadı: ", "Could not read the driver list: ") + drivers.Error);

        var problems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entities = await Wmi.QueryAsync(@"root\cimv2", "SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity", ct,
            TimeSpan.FromSeconds(60));
        foreach (var e in entities.Rows)
            if (e.Str("DeviceID") is { } id && e.Long("ConfigManagerErrorCode") is { } code) problems[id] = (int)code;

        var list = new List<DriverInfo>();
        foreach (var d in drivers.Rows)
        {
            var name = d.Str("DeviceName");
            if (name is null) continue;
            var cls = d.Str("DeviceClass") ?? string.Empty;
            var (category, important) = Classify(cls);
            int? problem = d.Str("DeviceID") is { } id && problems.TryGetValue(id, out var p) ? p : null;
            var (state, text) = problem switch
            {
                null => (entities.Ok ? CheckState.Info : CheckState.Unknown,
                         entities.Ok ? L.T("Aygıt şu an listede yok (bağlı değil olabilir)", "The device is not in the list right now (it may be disconnected)") : L.T("Durum okunamadı: ", "Could not read the status: ") + entities.Error),
                0 => (CheckState.Healthy, L.T("Çalışıyor", "Working")),
                22 or 45 => (CheckState.Info, DescribeProblem(problem.Value)),
                14 => (CheckState.Warning, DescribeProblem(14)),
                _ => (CheckState.Error, DescribeProblem(problem.Value))
            };
            list.Add(new DriverInfo(name, d.Str("Manufacturer") ?? "—", d.Str("DriverProviderName") ?? "—", d.Str("DriverVersion") ?? "—",
                d.Date("DriverDate"), cls, category, important, d.Bool("IsSigned"), problem, text, state));
        }
        logger.Info(L.T($"Sürücüler okundu: {list.Count} aygıt, {list.Count(x => x.State == CheckState.Error)} sorunlu", $"Drivers read: {list.Count} devices, {list.Count(x => x.State == CheckState.Error)} with problems") +
                    (entities.Ok ? "." : L.T($" (aygıt durumu okunamadı: {entities.Error})", $" (could not read device status: {entities.Error})")));
        return new DriverScan(list.OrderBy(x => x.Important ? 0 : 1).ThenBy(x => x.Category).ThenBy(x => x.DeviceName).ToList(), null);
    }

    private const string DriverSearchScript = """
        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'E-mre Control Center'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        Write-Log '«Windows Update sürücü kataloğunda arama yapılıyor...|Searching the Windows Update driver catalog...»'
        $res = $searcher.Search("IsInstalled=0 and Type='Driver'")
        $list = New-Object System.Collections.ArrayList
        foreach ($u in $res.Updates) {
            $d = $null; if ($u.DriverVerDate) { $d = $u.DriverVerDate.ToString('yyyy-MM-dd') }
            [void]$list.Add(@{ title = [string]$u.Title; model = [string]$u.DriverModel; manufacturer = [string]$u.DriverManufacturer;
                               driverClass = [string]$u.DriverClass; date = $d })
        }
        Write-Result @{ updates = @($list) }
        """;

    /// <summary>
    /// Windows Update'in resmî sürücü kataloğunda bu bilgisayar için kurulmamış sürücü güncellemelerini arar (yalnızca arama; kurulum
    /// Windows Ayarlar → Windows Update → İsteğe bağlı güncellemeler'den kullanıcıya bırakılır). İnternet gerekir; birkaç dakika sürebilir.
    /// </summary>
    public async Task<DriverUpdateScan> SearchWindowsUpdateAsync(CancellationToken ct = default)
    {
        var ps = await PowerShellRunner.RunAsync(DriverSearchScript, UpdateSearchTimeout, ct, m => logger.Info("  " + m),
            traceName: "Windows Update Agent – IUpdateSearcher.Search(\"IsInstalled=0 and Type='Driver'\")");
        if (!ps.Ok) return new DriverUpdateScan([], L.T("Windows Update sürücü araması yapılamadı: ", "Windows Update driver search failed: ") + ps.DescribeFailure(L.T("Windows Update araması", "Windows Update search")));
        var list = new List<DriverUpdate>();
        foreach (var u in ps.Data!.Value.Arr("updates"))
            list.Add(new DriverUpdate(u.Str("title") ?? "—", u.Str("model") ?? "—", u.Str("manufacturer") ?? "—", u.Str("driverClass") ?? "—",
                DateTime.TryParse(u.Str("date"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : null));
        logger.Info(L.T($"Windows Update sürücü araması: {list.Count} kurulmamış sürücü güncellemesi.", $"Windows Update driver search: {list.Count} driver update(s) not installed."));
        return new DriverUpdateScan(list, null);
    }
}

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
    public string SignedText => Signed switch { true => "İmzalı", false => "İmzasız", _ => "—" };
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
        "DISPLAY" => ("Ekran kartı", true),
        "NET" => ("Ağ", true),
        "MEDIA" => ("Ses", true),
        "BLUETOOTH" => ("Bluetooth", true),
        "HDC" or "SCSIADAPTER" => ("Depolama denetleyicisi", true),
        "DISKDRIVE" => ("Disk", true),
        "USB" => ("USB", true),
        "SYSTEM" => ("Yonga seti / sistem", true),
        "CAMERA" or "IMAGE" => ("Kamera", true),
        "BIOMETRIC" => ("Biyometrik", true),
        "SECURITYDEVICES" => ("Güvenlik aygıtı (TPM)", true),
        "FIRMWARE" => ("Ürün yazılımı", true),
        "MONITOR" => ("Monitör", false),
        "HIDCLASS" or "KEYBOARD" or "MOUSE" => ("Giriş aygıtı", false),
        "AUDIOENDPOINT" => ("Ses uç noktası", false),
        "PROCESSOR" => ("İşlemci", false),
        "BATTERY" => ("Pil", false),
        "PRINTER" or "PRINTQUEUE" => ("Yazıcı", false),
        "SOFTWARECOMPONENT" or "SOFTWAREDEVICE" => ("Yazılım bileşeni", false),
        _ => (string.IsNullOrEmpty(cls) ? "Diğer" : cls, false)
    };

    /// <summary>Aygıt Yöneticisi hata kodları (Microsoft belgelerindeki anlamları).</summary>
    public static string DescribeProblem(int code) => code switch
    {
        0 => "Çalışıyor",
        1 => "Kod 1: aygıt doğru yapılandırılmamış",
        3 => "Kod 3: sürücü bozuk olabilir veya bellek yetersiz",
        10 => "Kod 10: aygıt başlatılamıyor",
        12 => "Kod 12: yeterli boş kaynak bulunamadı",
        14 => "Kod 14: bilgisayar yeniden başlatılmalı",
        18 => "Kod 18: sürücüler yeniden yüklenmeli",
        19 => "Kod 19: kayıt defteri yapılandırma bilgisi bozuk",
        21 => "Kod 21: Windows aygıtı kaldırıyor",
        22 => "Kod 22: aygıt devre dışı bırakılmış",
        24 => "Kod 24: aygıt yok veya düzgün çalışmıyor",
        28 => "Kod 28: sürücü yüklü değil",
        29 => "Kod 29: aygıt ürün yazılımınca devre dışı",
        31 => "Kod 31: sürücü yüklenemedi",
        32 => "Kod 32: sürücü hizmeti devre dışı",
        37 => "Kod 37: sürücü başlatılamadı",
        38 => "Kod 38: sürücünün önceki örneği hâlâ bellekte",
        39 => "Kod 39: sürücü bozuk veya eksik",
        43 => "Kod 43: aygıt sorun bildirdi, Windows durdurdu",
        45 => "Kod 45: aygıt şu anda bağlı değil",
        48 => "Kod 48: sürücü uyumsuzluk nedeniyle engellendi",
        52 => "Kod 52: sürücünün dijital imzası doğrulanamadı",
        _ => $"Kod {code}: aygıt sorunu (Aygıt Yöneticisi'nde ayrıntı)"
    };

    public async Task<DriverScan> ScanAsync(CancellationToken ct = default)
    {
        var drivers = await Wmi.QueryAsync(@"root\cimv2",
            "SELECT DeviceID, DeviceName, Manufacturer, DriverProviderName, DriverVersion, DriverDate, DeviceClass, IsSigned FROM Win32_PnPSignedDriver",
            ct, TimeSpan.FromSeconds(60));
        if (!drivers.Ok) return new DriverScan([], "Sürücü listesi okunamadı: " + drivers.Error);

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
                         entities.Ok ? "Aygıt şu an listede yok (bağlı değil olabilir)" : "Durum okunamadı: " + entities.Error),
                0 => (CheckState.Healthy, "Çalışıyor"),
                22 or 45 => (CheckState.Info, DescribeProblem(problem.Value)),
                14 => (CheckState.Warning, DescribeProblem(14)),
                _ => (CheckState.Error, DescribeProblem(problem.Value))
            };
            list.Add(new DriverInfo(name, d.Str("Manufacturer") ?? "—", d.Str("DriverProviderName") ?? "—", d.Str("DriverVersion") ?? "—",
                d.Date("DriverDate"), cls, category, important, d.Bool("IsSigned"), problem, text, state));
        }
        logger.Info($"Sürücüler okundu: {list.Count} aygıt, {list.Count(x => x.State == CheckState.Error)} sorunlu" +
                    (entities.Ok ? "." : $" (aygıt durumu okunamadı: {entities.Error})"));
        return new DriverScan(list.OrderBy(x => x.Important ? 0 : 1).ThenBy(x => x.Category).ThenBy(x => x.DeviceName).ToList(), null);
    }

    private const string DriverSearchScript = """
        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'E-mre Control Center'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        Write-Log 'Windows Update sürücü kataloğunda arama yapılıyor...'
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
        if (!ps.Ok) return new DriverUpdateScan([], "Windows Update sürücü araması yapılamadı: " + ps.DescribeFailure("Windows Update araması"));
        var list = new List<DriverUpdate>();
        foreach (var u in ps.Data!.Value.Arr("updates"))
            list.Add(new DriverUpdate(u.Str("title") ?? "—", u.Str("model") ?? "—", u.Str("manufacturer") ?? "—", u.Str("driverClass") ?? "—",
                DateTime.TryParse(u.Str("date"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : null));
        logger.Info($"Windows Update sürücü araması: {list.Count} kurulmamış sürücü güncellemesi.");
        return new DriverUpdateScan(list, null);
    }
}

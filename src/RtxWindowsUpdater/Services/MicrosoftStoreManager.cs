using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Microsoft Store uygulama güncellemeleri.
/// Kontrol : Microsoft Store uygulamasının varlığı + winget "msstore" kaynağı
///           (Store kataloğu ile eşleşen kurulu uygulamaların güncellemelerini gerçek olarak listeler).
/// Güncelle: winget ile bulunan her Store uygulaması tek tek güncellenir; ardından Windows'un
///           MDM arabirimi (MDM_EnterpriseModernAppManagement_AppManagement01.UpdateScanMethod)
///           ile Store'un kendi güncelleme taraması tetiklenir.
/// Not: Winget ile eşleşmeyen bazı yerleşik uygulamaları yalnızca Store'un kendi tarayıcısı görebilir.
/// </summary>
public sealed class MicrosoftStoreManager : IUpdateModule, IInUseRetryModule, IManualUpdateModule
{
    private readonly Logger _logger;
    private readonly WingetManager _winget;

    public MicrosoftStoreManager(Logger logger)
    {
        _logger = logger;
        _winget = new WingetManager(logger, "msstore", ComponentKeys.Store, "Microsoft Store");
    }

    public string Key => ComponentKeys.Store;
    public string DisplayName => "Microsoft Store";

    private const string StorePresenceScript = """
        $p = Get-AppxPackage -Name Microsoft.WindowsStore -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) { Write-Result @{ installed = $true; version = [string]$p.Version } } else { Write-Result @{ installed = $false } }
        """;

    private const string MdmScanScript = """
        $ns = 'root\cimv2\mdm\dmmap'
        $cls = 'MDM_EnterpriseModernAppManagement_AppManagement01'
        $obj = Get-CimInstance -Namespace $ns -ClassName $cls
        $r = $obj | Invoke-CimMethod -MethodName UpdateScanMethod
        Write-Result @{ returnValue = [int]$r.ReturnValue }
        """;

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        _logger.Info(L.T("Microsoft Store güncellemeleri kontrol ediliyor...", "Checking Microsoft Store updates..."));

        var presence = await PowerShellRunner.RunAsync(StorePresenceScript, TimeSpan.FromMinutes(1), ct,
            traceName: "Get-AppxPackage -Name Microsoft.WindowsStore");
        if (!presence.Ok)
        {
            var reason = L.T("Microsoft Store durumu okunamadı: ", "Could not read the Microsoft Store status: ") + presence.DescribeFailure("PowerShell");
            _logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
        if (presence.Data!.Value.Bool("installed") != true)
        {
            var reason = L.T("Microsoft Store bu kullanıcı hesabında kurulu değil.", "Microsoft Store is not installed for this user account.");
            _logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
        var storeVersion = presence.Data!.Value.Str("version");
        _logger.Info(L.T($"Microsoft Store kurulu (sürüm {storeVersion}).", $"Microsoft Store installed (version {storeVersion})."));

        var result = await _winget.CheckAsync(ct);
        if (result.Status == ComponentStatus.CheckFailed)
        {
            var reason = L.T("Microsoft Store kataloğuna erişilemedi. ", "Could not access the Microsoft Store catalog. ") + result.Reason;
            _logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason, L.T($"Store sürümü: {storeVersion}", $"Store version: {storeVersion}"));
        }

        return new ModuleResult
        {
            Key = Key,
            Status = result.Status,
            Summary = result.ActionableCount > 0 ? L.T($"Güncelleme mevcut: {result.ActionableCount}", $"Update available: {result.ActionableCount}") : L.T("Güncel", "Up to date"),
            Details = L.T($"Store sürümü: {storeVersion}\n{result.Details}\nKaynak: winget msstore kataloğu", $"Store version: {storeVersion}\n{result.Details}\nSource: winget msstore catalog"),
            Items = result.Items,
            ActionableCount = result.ActionableCount
        };
    }

    /// <summary>Çalışan uygulama nedeniyle güncellenemeyen Store paketlerini, onaylanan uygulamalar kapatıldıktan sonra yeniden dener.</summary>
    public Task<ModuleResult> RetryAfterClosingAsync(ModuleResult previous, IReadOnlyCollection<RunningProcessInfo> approved,
        CancellationToken ct) => _winget.RetryAfterClosingAsync(previous, approved, ct);

    /// <summary>Kullanıcının ayrıca seçtiği, otomatik uygulanmayan Store güncellemelerini uygular.</summary>
    public Task<ModuleResult> UpdateManualAsync(ModuleResult check, IReadOnlyCollection<string> ids, CancellationToken ct) =>
        _winget.UpdateManualAsync(check, ids, ct);

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var result = await _winget.UpdateAsync(check, ct);

        _logger.Info(L.T("Microsoft Store'un kendi güncelleme taraması tetikleniyor (MDM UpdateScanMethod)...", "Triggering Microsoft Store's own update scan (MDM UpdateScanMethod)..."));
        var scan = await PowerShellRunner.RunAsync(MdmScanScript, TimeSpan.FromMinutes(3), CancellationToken.None,
            traceName: "MDM_EnterpriseModernAppManagement_AppManagement01.UpdateScanMethod");
        if (scan.Ok && scan.Data!.Value.Long("returnValue") == 0)
            _logger.Success(L.T("Microsoft Store güncelleme taraması başlatıldı; kalan Store güncellemeleri arka planda Store tarafından kurulacak.", "The Microsoft Store update scan started; the remaining Store updates will be installed by Store in the background."));
        else
            _logger.Warning(L.T("Store güncelleme taraması tetiklenemedi: ", "Could not trigger the Store update scan: ") +
                            (scan.Ok ? L.T($"dönüş değeri {scan.Data!.Value.Long("returnValue")}", $"return value {scan.Data!.Value.Long("returnValue")}") : scan.DescribeFailure("MDM")));

        return new ModuleResult
        {
            Key = Key,
            Status = result.Status,
            Summary = result.Summary,
            Details = result.Details,
            Reason = result.Reason,
            Items = result.Items,
            RebootRequired = result.RebootRequired
        };
    }
}

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
        _logger.Info("Microsoft Store güncellemeleri kontrol ediliyor...");

        var presence = await PowerShellRunner.RunAsync(StorePresenceScript, TimeSpan.FromMinutes(1), ct,
            traceName: "Get-AppxPackage -Name Microsoft.WindowsStore");
        if (!presence.Ok)
        {
            var reason = "Microsoft Store durumu okunamadı: " + presence.DescribeFailure("PowerShell");
            _logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
        if (presence.Data!.Value.Bool("installed") != true)
        {
            const string reason = "Microsoft Store bu kullanıcı hesabında kurulu değil.";
            _logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
        var storeVersion = presence.Data!.Value.Str("version");
        _logger.Info($"Microsoft Store kurulu (sürüm {storeVersion}).");

        var result = await _winget.CheckAsync(ct);
        if (result.Status == ComponentStatus.CheckFailed)
        {
            var reason = "Microsoft Store kataloğuna erişilemedi. " + result.Reason;
            _logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason, $"Store sürümü: {storeVersion}");
        }

        return new ModuleResult
        {
            Key = Key,
            Status = result.Status,
            Summary = result.ActionableCount > 0 ? $"Güncelleme mevcut: {result.ActionableCount}" : "Güncel",
            Details = $"Store sürümü: {storeVersion}\n{result.Details}\nKaynak: winget msstore kataloğu",
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

        _logger.Info("Microsoft Store'un kendi güncelleme taraması tetikleniyor (MDM UpdateScanMethod)...");
        var scan = await PowerShellRunner.RunAsync(MdmScanScript, TimeSpan.FromMinutes(3), CancellationToken.None,
            traceName: "MDM_EnterpriseModernAppManagement_AppManagement01.UpdateScanMethod");
        if (scan.Ok && scan.Data!.Value.Long("returnValue") == 0)
            _logger.Success("Microsoft Store güncelleme taraması başlatıldı; kalan Store güncellemeleri arka planda Store tarafından kurulacak.");
        else
            _logger.Warning("Store güncelleme taraması tetiklenemedi: " +
                            (scan.Ok ? $"dönüş değeri {scan.Data!.Value.Long("returnValue")}" : scan.DescribeFailure("MDM")));

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

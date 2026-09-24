using System.Net.Http;
using System.Xml.Linq;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Microsoft Defender Antivirus entegrasyonu.
/// Kontrol : Get-MpComputerStatus (yerel durum) + Microsoft'un resmi Defender paket bilgi servisi
///           (microsoft.com/security/encyclopedia/adlpackages.aspx) ile yayımlanan en son sürüm karşılaştırması.
/// Güncelle: Update-MpSignature (resmi Defender cmdlet'i). Güncelleme sonrası sürüm yeniden okunarak doğrulanır.
/// Defender'ın hiçbir ayarı değiştirilmez / kapatılmaz.
/// </summary>
public sealed class DefenderManager(Logger logger, HttpClient http) : IUpdateModule
{
    public string Key => ComponentKeys.Defender;
    public string DisplayName => "Microsoft Defender";

    private const string LatestInfoUrl = "https://www.microsoft.com/security/encyclopedia/adlpackages.aspx?action=info&arch=x64";

    private const string StatusScript = """
        $s = Get-MpComputerStatus
        $ood = $null
        if ($s.PSObject.Properties.Name -contains 'DefenderSignaturesOutOfDate') { $ood = [bool]$s.DefenderSignaturesOutOfDate }
        $last = $null
        if ($s.AntivirusSignatureLastUpdated) { $last = $s.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm') }
        Write-Result @{
            serviceEnabled = [bool]$s.AMServiceEnabled
            antivirusEnabled = [bool]$s.AntivirusEnabled
            realTime = [bool]$s.RealTimeProtectionEnabled
            runningMode = [string]$s.AMRunningMode
            signatureVersion = [string]$s.AntivirusSignatureVersion
            signatureUpdated = $last
            signatureAge = [int]$s.AntivirusSignatureAge
            engineVersion = [string]$s.AMEngineVersion
            platformVersion = [string]$s.AMProductVersion
            outOfDate = $ood
        }
        """;

    private const string UpdateScript = """
        $before = (Get-MpComputerStatus).AntivirusSignatureVersion
        Write-Log ('Mevcut tanım sürümü: ' + $before)
        $done = $false; $lastErr = $null
        foreach ($src in @($null, 'MicrosoftUpdateServer', 'MMPC')) {
            try {
                if ($src) { Write-Log ('Alternatif kaynak deneniyor: ' + $src); Update-MpSignature -UpdateSource $src -ErrorAction Stop }
                else { Write-Log 'Update-MpSignature çalıştırılıyor...'; Update-MpSignature -ErrorAction Stop }
                $done = $true; break
            } catch { $lastErr = $_.Exception.Message; Write-Log ('Hata: ' + $lastErr) }
        }
        $after = (Get-MpComputerStatus).AntivirusSignatureVersion
        Write-Result @{ ok = $done; before = [string]$before; after = [string]$after; lastError = $lastErr }
        """;

    private sealed record LocalStatus(
        bool ServiceEnabled, bool AntivirusEnabled, bool RealTime, string RunningMode,
        string SignatureVersion, string? SignatureUpdated, long SignatureAge,
        string EngineVersion, string PlatformVersion, bool? OutOfDate);

    private sealed record LatestInfo(string Signatures, string Engine, string Platform, string? Date);

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info("Defender kontrol ediliyor...");
        var (local, error) = await ReadLocalAsync(ct);
        if (local is null)
        {
            var reason = "Microsoft Defender durumuna erişilemedi: " + error +
                         " (Başka bir antivirüs yazılımı Defender'ı devre dışı bırakmış olabilir.)";
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var active = local.ServiceEnabled && local.AntivirusEnabled && local.RealTime;
        var protection = active ? "Aktif" :
            !local.AntivirusEnabled ? $"Pasif ({local.RunningMode})" : "Gerçek zamanlı koruma kapalı";
        logger.Info($"Koruma durumu: {protection}; tanım sürümü {local.SignatureVersion} ({local.SignatureUpdated})");

        var latest = await FetchLatestAsync(ct);
        bool outOfDate;
        string comparison;
        if (latest is not null && Version.TryParse(latest.Signatures, out var lv) &&
            Version.TryParse(local.SignatureVersion, out var cv))
        {
            outOfDate = cv < lv;
            comparison = $"Microsoft'un yayımladığı son sürüm: {latest.Signatures}";
            logger.Info(comparison);
        }
        else if (local.OutOfDate is not null)
        {
            outOfDate = local.OutOfDate.Value;
            comparison = "Microsoft sunucusuyla karşılaştırılamadı; Defender'ın kendi 'güncel değil' bayrağı kullanıldı.";
            logger.Warning(comparison);
        }
        else
        {
            var reason = "Tanımların güncel olup olmadığı belirlenemedi (Microsoft sunucusuna ulaşılamadı ve Defender güncellik bilgisi vermedi).";
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason, $"Koruma durumu: {protection}\nTanım sürümü: {local.SignatureVersion}");
        }

        var details =
            $"Koruma durumu: {protection}\n" +
            $"Virüs ve tehdit tanımları: {(outOfDate ? "Güncel değil" : "Güncel")}\n" +
            $"Tanım sürümü: {local.SignatureVersion}\n" +
            $"Son güncelleme: {local.SignatureUpdated ?? "bilinmiyor"}";

        var items = new List<UpdateItem>
        {
            new()
            {
                Name = "Güvenlik zekası (virüs ve tehdit tanımları)",
                Id = "signatures",
                CurrentVersion = local.SignatureVersion,
                NewVersion = latest?.Signatures ?? "?",
                UpdateAvailable = outOfDate,
                StatusText = outOfDate ? "Güncelleme mevcut" : "Güncel"
            },
            new()
            {
                Name = "Kötü amaçlı yazılım koruma altyapısı (engine)",
                Id = "engine",
                CurrentVersion = local.EngineVersion,
                NewVersion = latest?.Engine ?? "?",
                UpdateAvailable = false,
                AutoUpdatable = false,
                StatusText = CompareText(local.EngineVersion, latest?.Engine)
            },
            new()
            {
                Name = "Defender platformu",
                Id = "platform",
                CurrentVersion = local.PlatformVersion,
                NewVersion = latest?.Platform ?? "?",
                UpdateAvailable = false,
                AutoUpdatable = false,
                StatusText = CompareText(local.PlatformVersion, latest?.Platform) + " (Windows Update ile güncellenir)"
            }
        };

        if (!active)
            logger.Warning("Defender koruması aktif değil. Uygulama güvenlik ayarlarını değiştirmez.");

        if (outOfDate) logger.Warning("Defender tanımları güncel değil.");
        else logger.Success("Defender güncel.");

        return new ModuleResult
        {
            Key = Key,
            Status = outOfDate ? ComponentStatus.UpdateAvailable : ComponentStatus.UpToDate,
            Summary = outOfDate ? "Tanım güncellemesi mevcut" : "Güncel",
            Details = details,
            Reason = active ? null : $"Koruma durumu: {protection}",
            Items = items,
            ActionableCount = outOfDate ? 1 : 0
        };
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        logger.Info("Microsoft Defender tanımları güncelleniyor...");
        var ps = await PowerShellRunner.RunAsync(UpdateScript, TimeSpan.FromMinutes(15), CancellationToken.None,
            m => logger.Info("  " + m), traceName: "Update-MpSignature");
        if (!ps.Ok)
        {
            var reason = ps.DescribeFailure("Update-MpSignature");
            logger.Error("Defender güncellemesi başarısız: " + reason);
            return ModuleResult.Failed(Key, reason, check.Details);
        }

        var d = ps.Data!.Value;
        var before = d.Str("before") ?? "?";
        var after = d.Str("after") ?? "?";
        if (d.Bool("ok") != true)
        {
            var reason = "Update-MpSignature başarısız: " + (d.Str("lastError") ?? "bilinmeyen hata");
            logger.Error(reason);
            return ModuleResult.Failed(Key, reason, check.Details);
        }

        // Sonucu gerçekten doğrula.
        var target = check.Items.FirstOrDefault(i => i.Id == "signatures")?.NewVersion;
        var reached = Version.TryParse(after, out var a) &&
                      (target is null || !Version.TryParse(target, out var t) || a >= t);
        var changed = before != after;

        if (reached || changed)
        {
            logger.Success($"Defender tanımları güncellendi: {before} → {after}");
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Updated,
                Summary = "Güncellendi",
                Details = $"Virüs ve tehdit tanımları: Güncel\nTanım sürümü: {after}",
                Items = check.Items
            };
        }

        var msg = $"Update-MpSignature tamamlandı ancak tanım sürümü değişmedi ({after}). Microsoft sunucularında yeni paket henüz dağıtılmamış olabilir.";
        logger.Warning(msg);
        return ModuleResult.Failed(Key, msg, check.Details);
    }

    private async Task<(LocalStatus? Status, string? Error)> ReadLocalAsync(CancellationToken ct)
    {
        var ps = await PowerShellRunner.RunAsync(StatusScript, TimeSpan.FromMinutes(2), ct, traceName: "Get-MpComputerStatus");
        if (!ps.Ok) return (null, ps.DescribeFailure("Get-MpComputerStatus"));
        var d = ps.Data!.Value;
        return (new LocalStatus(
            d.Bool("serviceEnabled") == true,
            d.Bool("antivirusEnabled") == true,
            d.Bool("realTime") == true,
            d.Str("runningMode") ?? "?",
            d.Str("signatureVersion") ?? "?",
            d.Str("signatureUpdated"),
            d.Long("signatureAge") ?? -1,
            d.Str("engineVersion") ?? "?",
            d.Str("platformVersion") ?? "?",
            d.Bool("outOfDate")), null);
    }

    private async Task<LatestInfo?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var xml = await http.GetStringAsync(LatestInfoUrl, cts.Token);
            var root = XDocument.Parse(xml).Root;
            var sig = root?.Element("signatures");
            if (sig is null)
            {
                ExecutionTrace.Note("Microsoft Defender sürüm servisi beklenen veriyi döndürmedi.");
                return null;
            }
            ExecutionTrace.Note($"Microsoft Defender sürüm servisi: tanım {sig.Value.Trim()}, motor {root!.Element("engine")?.Value.Trim() ?? "?"}, platform {root.Element("platform")?.Value.Trim() ?? "?"}");
            return new LatestInfo(
                sig.Value.Trim(),
                root!.Element("engine")?.Value.Trim() ?? "?",
                root.Element("platform")?.Value.Trim() ?? "?",
                sig.Attribute("date")?.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.Warning($"Microsoft Defender sürüm servisine ulaşılamadı: {ex.Message}");
            ExecutionTrace.Note($"Microsoft Defender sürüm servisine ulaşılamadı: {ex.Message}");
            return null;
        }
    }

    private static string CompareText(string local, string? latest)
    {
        if (latest is null || !Version.TryParse(local, out var l) || !Version.TryParse(latest, out var r))
            return "Karşılaştırılamadı";
        return l >= r ? "Güncel" : "Daha yeni sürüm yayımlandı";
    }
}

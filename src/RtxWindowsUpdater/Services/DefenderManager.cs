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

    /// <summary>
    /// Tanım güncellemesi. 2026-10-07 KULLANICI SORUNU: varsayılan kaynak sırası "MicrosoftUpdateServer|MMPC"; Windows Update hata
    /// vermeden "yeni tanım yok" dediği için Update-MpSignature başarıyla dönüyor ama sürüm değişmiyordu (cihaz 1.459.576.0, Microsoft'un
    /// yayımladığı 1.459.588.0) ve eski betik yalnızca HATA olunca sonraki kaynağa geçtiği için MMPC hiç denenmiyordu. Artık sürüm
    /// değişmezse de sıradaki resmi kaynak denenir: varsayılan sıra → (yalnızca varsayılan hata verdiyse) Microsoft Update → Microsoft'un
    /// tanım sunucusu (MMPC). Sürüm değişince durulur; hangi kaynağın getirdiği döner.
    /// </summary>
    private const string UpdateScript = """
        $before = [string](Get-MpComputerStatus).AntivirusSignatureVersion
        Write-Log ('«Mevcut tanım sürümü: |Current definition version:»' + $before)
        $ok = $false; $defaultOk = $false; $lastErr = $null; $source = $null
        foreach ($src in @($null, 'MicrosoftUpdateServer', 'MMPC')) {
            if ($src -eq 'MicrosoftUpdateServer' -and $defaultOk) { continue }
            try {
                if ($src -eq 'MMPC') { Write-Log '«Microsoft tanım sunucusundan (MMPC) doğrudan deneniyor...|Trying directly from Microsoft's definition server (MMPC)...»'; Update-MpSignature -UpdateSource MMPC -ErrorAction Stop }
                elseif ($src) { Write-Log ('«Kaynak deneniyor: |Trying source:»' + $src); Update-MpSignature -UpdateSource $src -ErrorAction Stop }
                else { Write-Log '«Update-MpSignature çalıştırılıyor (Windows varsayılan kaynak sırası)...|Running Update-MpSignature (Windows default source order)...»'; Update-MpSignature -ErrorAction Stop; $defaultOk = $true }
                $ok = $true
            } catch { $lastErr = $_.Exception.Message; «Write-Log ('Hata: ' + $lastErr)|Write-Log ('Error: ' + $lastErr)»; continue }
            $now = [string](Get-MpComputerStatus).AntivirusSignatureVersion
            if ($now -ne $before) { if ($src) { $source = $src } else { $source = 'default' }; break }
            Write-Log ('«Tanım sürümü değişmedi (|Definition version did not change (»' + $now + ').')
        }
        $after = [string](Get-MpComputerStatus).AntivirusSignatureVersion
        Write-Result @{ ok = $ok; before = $before; after = $after; source = $source; lastError = $lastErr }
        """;

    private sealed record LocalStatus(
        bool ServiceEnabled, bool AntivirusEnabled, bool RealTime, string RunningMode,
        string SignatureVersion, string? SignatureUpdated, long SignatureAge,
        string EngineVersion, string PlatformVersion, bool? OutOfDate);

    private sealed record LatestInfo(string Signatures, string Engine, string Platform, string? Date);

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info(L.T("Defender kontrol ediliyor...", "Checking Defender..."));
        var (local, error) = await ReadLocalAsync(ct);
        if (local is null)
        {
            var reason = L.T("Microsoft Defender durumuna erişilemedi: ", "Could not access the Microsoft Defender status: ") + error +
                         L.T(" (Başka bir antivirüs yazılımı Defender'ı devre dışı bırakmış olabilir.)", " (Another antivirus program may have disabled Defender.)");
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var active = local.ServiceEnabled && local.AntivirusEnabled && local.RealTime;
        var protection = active ? L.T("Aktif", "Active") :
            !local.AntivirusEnabled ? L.T($"Pasif ({local.RunningMode})", $"Passive ({local.RunningMode})") : L.T("Gerçek zamanlı koruma kapalı", "Real-time protection off");
        logger.Info(L.T($"Koruma durumu: {protection}; tanım sürümü {local.SignatureVersion} ({local.SignatureUpdated})", $"Protection status: {protection}; definition version {local.SignatureVersion} ({local.SignatureUpdated})"));

        var latest = await FetchLatestAsync(ct);
        bool outOfDate;
        string comparison;
        string? fallbackNote = null;
        if (latest is not null && Version.TryParse(latest.Signatures, out var lv) &&
            Version.TryParse(local.SignatureVersion, out var cv))
        {
            outOfDate = cv < lv;
            comparison = L.T($"Microsoft'un yayımladığı son sürüm: {latest.Signatures}", $"Latest version published by Microsoft: {latest.Signatures}");
            logger.Info(comparison);
        }
        else if (local.OutOfDate is not null)
        {
            outOfDate = local.OutOfDate.Value;
            comparison = L.T("Microsoft sunucusuyla karşılaştırılamadı; Defender'ın kendi 'güncel değil' bayrağı kullanıldı.", "Could not compare with the Microsoft server; Defender's own 'out of date' flag was used.");
            fallbackNote = comparison + L.T(" (İnternet bağlantısını kontrol edip yeniden kontrol edebilirsiniz.)", " (You can check the internet connection and check again.)");
            logger.Warning(comparison);
        }
        else
        {
            var reason = L.T("Tanımların güncel olup olmadığı belirlenemedi (Microsoft sunucusuna ulaşılamadı ve Defender güncellik bilgisi vermedi).", "Could not determine whether the definitions are up to date (the Microsoft server could not be reached and Defender gave no up-to-date information).");
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason, L.T($"Koruma durumu: {protection}\nTanım sürümü: {local.SignatureVersion}", $"Protection status: {protection}\nDefinition version: {local.SignatureVersion}"));
        }

        var details =
            L.T($"Koruma durumu: {protection}\n", $"Protection status: {protection}\n") +
            L.T($"Virüs ve tehdit tanımları: {(outOfDate ? "Güncel değil" : "Güncel")}\n", $"Virus and threat definitions: {(outOfDate ? "Out of date" : "Up to date")}\n") +
            L.T($"Tanım sürümü: {local.SignatureVersion}\n", $"Definition version: {local.SignatureVersion}\n") +
            L.T($"Son güncelleme: {local.SignatureUpdated ?? "bilinmiyor"}", $"Last update: {local.SignatureUpdated ?? "unknown"}");

        var items = new List<UpdateItem>
        {
            new()
            {
                Name = L.T("Güvenlik zekası (virüs ve tehdit tanımları)", "Security intelligence (virus and threat definitions)"),
                Id = "signatures",
                CurrentVersion = local.SignatureVersion,
                NewVersion = latest?.Signatures ?? "?",
                UpdateAvailable = outOfDate,
                StatusText = outOfDate ? L.T("Güncelleme mevcut", "Update available") : L.T("Güncel", "Up to date")
            },
            new()
            {
                Name = L.T("Kötü amaçlı yazılım koruma altyapısı (engine)", "Antimalware engine"),
                Id = "engine",
                CurrentVersion = local.EngineVersion,
                NewVersion = latest?.Engine ?? "?",
                UpdateAvailable = false,
                AutoUpdatable = false,
                StatusText = CompareText(local.EngineVersion, latest?.Engine)
            },
            new()
            {
                Name = L.T("Defender platformu", "Defender platform"),
                Id = "platform",
                CurrentVersion = local.PlatformVersion,
                NewVersion = latest?.Platform ?? "?",
                UpdateAvailable = false,
                AutoUpdatable = false,
                StatusText = CompareText(local.PlatformVersion, latest?.Platform) + L.T(" (Windows Update ile güncellenir)", " (updated with Windows Update)")
            }
        };

        if (!active)
            logger.Warning(L.T("Defender koruması aktif değil. Uygulama güvenlik ayarlarını değiştirmez.", "Defender protection is not active. The app does not change security settings."));

        if (outOfDate) logger.Warning(L.T("Defender tanımları güncel değil.", "Defender definitions are out of date."));
        else logger.Success(L.T("Defender güncel.", "Defender is up to date."));

        return new ModuleResult
        {
            Key = Key,
            Status = outOfDate ? ComponentStatus.UpdateAvailable : ComponentStatus.UpToDate,
            // Microsoft sunucusuyla karşılaştırılamadıysa sonuç Defender'ın kendi bilgisidir: kartta bu açıkça yazılır.
            Summary = (outOfDate ? L.T("Tanım güncellemesi mevcut", "Definition update available") : L.T("Güncel", "Up to date")) + (fallbackNote is null ? "" : L.T(" (Defender'ın kendi bilgisine göre)", " (according to Defender's own information)")),
            Details = details,
            Reason = string.Join("\n", new[] { active ? null : L.T($"Koruma durumu: {protection}", $"Protection status: {protection}"), fallbackNote }.Where(l => l is not null)) is { Length: > 0 } why
                ? why
                : null,
            Items = items,
            ActionableCount = outOfDate ? 1 : 0
        };
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        logger.Info(L.T("Microsoft Defender tanımları güncelleniyor...", "Updating Microsoft Defender definitions..."));
        var ps = await PowerShellRunner.RunAsync(UpdateScript, TimeSpan.FromMinutes(15), CancellationToken.None,
            m => logger.Info("  " + m), traceName: "Update-MpSignature");
        if (!ps.Ok)
        {
            var reason = ps.DescribeFailure("Update-MpSignature");
            logger.Error(L.T("Defender güncellemesi başarısız: ", "Defender update failed: ") + reason);
            return ModuleResult.Failed(Key, reason, check.Details);
        }

        var d = ps.Data!.Value;
        var before = d.Str("before") ?? "?";
        var after = d.Str("after") ?? "?";
        if (d.Bool("ok") != true)
        {
            var reason = L.T("Update-MpSignature başarısız: ", "Update-MpSignature failed: ") + (d.Str("lastError") ?? L.T("bilinmeyen hata", "unknown error"));
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
            var via = d.Str("source") switch
            {
                "MMPC" => L.T(" (Microsoft tanım sunucusu – MMPC)", " (Microsoft definition server – MMPC)"),
                "MicrosoftUpdateServer" => " (Microsoft Update)",
                _ => string.Empty
            };
            logger.Success(L.T($"Defender tanımları güncellendi: {before} → {after}{via}", $"Defender definitions updated: {before} → {after}{via}"));
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Updated,
                Summary = L.T("Güncellendi", "Updated"),
                Details = L.T($"Virüs ve tehdit tanımları: Güncel\nTanım sürümü: {after}", $"Virus and threat definitions: Up to date\nDefinition version: {after}"),
                Items = check.Items
            };
        }

        var msg = L.T($"Update-MpSignature tamamlandı ancak tanım sürümü değişmedi ({after}", $"Update-MpSignature finished but the definition version did not change ({after}") + (target is null ? ")" : L.T($"; Microsoft'un yayımladığı son sürüm {target})", $"; latest version published by Microsoft {target})")) +
                  L.T(". Windows'un varsayılan kaynağı ve Microsoft tanım sunucusu (MMPC) yeni paket vermedi; paket henüz dağıtılıyor olabilir. ", ". Windows' default source and the Microsoft definition server (MMPC) provided no new package; the package may still be rolling out. ") +
                  L.T("Bir süre sonra yeniden deneyin veya Windows Güvenliği → Virüs ve tehdit koruması → Koruma güncelleştirmeleri'nden denetleyin.", "Try again after a while or check in Windows Security → Virus & threat protection → Protection updates.");
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
                ExecutionTrace.Note(L.T("Microsoft Defender sürüm servisi beklenen veriyi döndürmedi.", "The Microsoft Defender version service did not return the expected data."));
                return null;
            }
            ExecutionTrace.Note(L.T($"Microsoft Defender sürüm servisi: tanım {sig.Value.Trim()}, motor {root!.Element("engine")?.Value.Trim() ?? "?"}, platform {root.Element("platform")?.Value.Trim() ?? "?"}", $"Microsoft Defender version service: definitions {sig.Value.Trim()}, engine {root!.Element("engine")?.Value.Trim() ?? "?"}, platform {root.Element("platform")?.Value.Trim() ?? "?"}"));
            return new LatestInfo(
                sig.Value.Trim(),
                root!.Element("engine")?.Value.Trim() ?? "?",
                root.Element("platform")?.Value.Trim() ?? "?",
                sig.Attribute("date")?.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.Warning(L.T($"Microsoft Defender sürüm servisine ulaşılamadı: {ex.Message}", $"Could not reach the Microsoft Defender version service: {ex.Message}"));
            ExecutionTrace.Note(L.T($"Microsoft Defender sürüm servisine ulaşılamadı: {ex.Message}", $"Could not reach the Microsoft Defender version service: {ex.Message}"));
            return null;
        }
    }

    private static string CompareText(string local, string? latest)
    {
        if (latest is null || !Version.TryParse(local, out var l) || !Version.TryParse(latest, out var r))
            return L.T("Karşılaştırılamadı", "Could not compare");
        return l >= r ? L.T("Güncel", "Up to date") : L.T("Daha yeni sürüm yayımlandı", "Newer version published");
    }
}

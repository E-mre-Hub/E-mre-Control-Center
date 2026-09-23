using System.Text.Json;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows Update entegrasyonu – Windows'un resmi Windows Update Agent (WUA) COM API'si
/// (Microsoft.Update.Session / UpdateSearcher / UpdateDownloader / UpdateInstaller) kullanılır.
/// COM çağrıları ayrı bir PowerShell sürecinde yürütülür; böylece takılırsa zaman aşımıyla sonlandırılabilir.
/// Servis ayarları DEĞİŞTİRİLMEZ; servis devre dışıysa yalnızca raporlanır.
/// Hiçbir zaman otomatik yeniden başlatma yapılmaz.
/// </summary>
public sealed class WindowsUpdateManager(Logger logger) : IUpdateModule
{
    public string Key => ComponentKeys.WindowsUpdate;
    public string DisplayName => "Windows Update";

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(3);

    // Defender tanım güncellemeleri (Definition Updates sınıfı) Defender modülünde işlenir.
    private const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0";
    private const string FallbackCriteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    private const string SearchFunction = """
        function Invoke-WuSearch($searcher) {
            try { return $searcher.Search("__CRITERIA__") }
            catch {
                $h = $_.Exception.HResult; if ($_.Exception.InnerException) { $h = $_.Exception.InnerException.HResult }
                if ($h -eq -2145124302) { Write-Log 'Arama ölçütü desteklenmedi, alternatif ölçütle yeniden deneniyor...'; return $searcher.Search("__FALLBACK__") }
                throw
            }
        }
        $defCat = 'e0789628-ce08-4437-be74-2495b842f43b'
        function Test-Definition($u) { foreach ($c in $u.Categories) { if ($c.CategoryID -eq $defCat) { return $true } }; return $false }

        """;

    private static string Prepare(string body) =>
        SearchFunction.Replace("__CRITERIA__", Criteria).Replace("__FALLBACK__", FallbackCriteria) + body;

    private const string CheckScript = """
        $svc = Get-Service -Name wuauserv -ErrorAction SilentlyContinue
        if (-not $svc) { Write-Result @{ error = 'Windows Update servisi (wuauserv) bu sistemde bulunamadı.' }; return }
        $mode = (Get-CimInstance Win32_Service -Filter "Name='wuauserv'").StartMode
        if ($mode -eq 'Disabled') { Write-Result @{ serviceDisabled = $true }; return }
        Write-Log ('Windows Update servisi: ' + $svc.Status + ' (başlangıç: ' + $mode + ')')

        $pending = $false
        try { $pending = [bool](New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired } catch { }

        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'RTX Windows Updater'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        Write-Log 'Microsoft Update sunucularında arama yapılıyor (birkaç dakika sürebilir)...'
        $res = Invoke-WuSearch $searcher

        $list = New-Object System.Collections.ArrayList
        foreach ($u in $res.Updates) {
            if (Test-Definition $u) { Write-Log ('Defender tanım güncellemesi (Defender bölümünde işlenecek): ' + $u.Title); continue }
            $cls = ''
            foreach ($c in $u.Categories) { if ($c.Type -eq 'UpdateClassification') { $cls = $c.Name } }
            $kbs = @(); foreach ($k in $u.KBArticleIDs) { $kbs += ('KB' + $k) }
            [void]$list.Add(@{
                id = [string]$u.Identity.UpdateID
                title = [string]$u.Title
                kb = ($kbs -join ', ')
                size = [long]$u.MaxDownloadSize
                classification = [string]$cls
                reboot = [int]$u.InstallationBehavior.RebootBehavior
            })
        }
        Write-Result @{ resultCode = [int]$res.ResultCode; rebootPending = $pending; updates = $list }
        """;

    private const string InstallScript = """
        $ids = @(); foreach ($x in (__IDS__ | ConvertFrom-Json)) { $ids += [string]$x }
        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'RTX Windows Updater'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        Write-Log 'Güncellemeler Microsoft Update üzerinde yeniden doğrulanıyor...'
        $res = Invoke-WuSearch $searcher

        $coll = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $res.Updates) {
            if ($ids -contains [string]$u.Identity.UpdateID) {
                if (-not $u.EulaAccepted) { $u.AcceptEula() }
                [void]$coll.Add($u)
            }
        }
        if ($coll.Count -eq 0) {
            $rb = $false; try { $rb = [bool](New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired } catch { }
            Write-Result @{ noneFound = $true; rebootRequired = $rb }; return
        }

        $installer = $session.CreateUpdateInstaller()
        if ($installer.IsBusy) { Write-Result @{ error = 'Windows Update şu anda başka bir kurulum yapıyor. Tamamlandıktan sonra tekrar deneyin.' }; return }

        $toDownload = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $coll) { if (-not $u.IsDownloaded) { [void]$toDownload.Add($u) } }
        if ($toDownload.Count -gt 0) {
            $mb = 0; foreach ($u in $toDownload) { $mb += $u.MaxDownloadSize }
            Write-Log ('{0} güncelleme indiriliyor (en fazla {1:N0} MB)...' -f $toDownload.Count, ($mb / 1MB))
            $dl = $session.CreateUpdateDownloader()
            $dl.Updates = $toDownload
            $dres = $dl.Download()
            Write-Log ('İndirme tamamlandı (sonuç kodu {0}, 0x{1:X8}).' -f [int]$dres.ResultCode, $dres.HResult)
        }

        $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
        $items = New-Object System.Collections.ArrayList
        foreach ($u in $coll) {
            if ($u.IsDownloaded) { [void]$toInstall.Add($u) }
            else {
                [void]$items.Add(@{ id = [string]$u.Identity.UpdateID; title = [string]$u.Title; resultCode = 4; hresult = ''; reboot = $false; note = 'indirilemedi' })
                Write-Log ('İndirilemedi: ' + $u.Title)
            }
        }
        if ($toInstall.Count -eq 0) { Write-Result @{ resultCode = 4; rebootRequired = $false; items = $items; hresult = '' }; return }

        Write-Log ('{0} güncelleme kuruluyor... (bilgisayarı kapatmayın)' -f $toInstall.Count)
        $installer.Updates = $toInstall
        $ires = $installer.Install()
        for ($i = 0; $i -lt $toInstall.Count; $i++) {
            $u = $toInstall.Item($i)
            $r = $ires.GetUpdateResult($i)
            [void]$items.Add(@{ id = [string]$u.Identity.UpdateID; title = [string]$u.Title; resultCode = [int]$r.ResultCode; hresult = ('0x{0:X8}' -f $r.HResult); reboot = [bool]$r.RebootRequired; note = '' })
            Write-Log ('{0} → sonuç kodu {1}' -f $u.Title, [int]$r.ResultCode)
        }
        Write-Result @{ resultCode = [int]$ires.ResultCode; rebootRequired = [bool]$ires.RebootRequired; items = $items; hresult = ('0x{0:X8}' -f $ires.HResult) }
        """;

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info("Windows Update kontrol ediliyor...");
        var ps = await PowerShellRunner.RunAsync(Prepare(CheckScript), SearchTimeout, ct, m => logger.Info("  " + m));

        if (!ps.Ok)
        {
            var reason = Explain(ps.DescribeFailure("Windows Update"));
            logger.Error("Windows Update kontrolü gerçekleştirilemedi: " + reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var data = ps.Data!.Value;
        if (data.Bool("serviceDisabled") == true)
        {
            const string reason = "Windows Update servisi (wuauserv) devre dışı bırakılmış. Uygulama sistem ayarlarını değiştirmez; servisi Hizmetler (services.msc) üzerinden siz etkinleştirmelisiniz.";
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var resultCode = data.Long("resultCode") ?? -1;
        if (resultCode is not (2 or 3))
        {
            var reason = $"Windows Update araması tamamlanamadı (sonuç kodu {resultCode}).";
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var pending = data.Bool("rebootPending") == true;
        var items = data.Arr("updates").Select(u =>
        {
            var size = u.Long("size") ?? 0;
            return new UpdateItem
            {
                Name = u.Str("title") ?? "(adsız güncelleme)",
                Id = u.Str("id") ?? string.Empty,
                CurrentVersion = u.Str("classification") ?? string.Empty,
                NewVersion = string.Join("  ", new[] { u.Str("kb"), size > 0 ? $"{size / 1048576.0:N0} MB" : null }
                    .Where(s => !string.IsNullOrEmpty(s))),
                UpdateAvailable = true,
                StatusText = "Güncelleme mevcut"
            };
        }).ToList();

        foreach (var i in items)
            logger.Info($"  Bulundu: {i.Name}");

        if (pending)
            logger.Warning("Önceki güncellemeler için yeniden başlatma bekleniyor.");

        if (items.Count == 0)
        {
            logger.Success("Windows Update: güncelleme bulunamadı.");
            return new ModuleResult
            {
                Key = Key,
                Status = pending ? ComponentStatus.RebootRequired : ComponentStatus.UpToDate,
                Summary = pending ? "Güncel – yeniden başlatma bekleniyor" : "Güncel",
                Details = pending ? "Daha önce kurulan güncellemeler yeniden başlatma bekliyor." : "Bekleyen Windows güncelleştirmesi yok.",
                Reason = pending ? "Yeniden başlatma gerekiyor." : null,
                RebootRequired = pending
            };
        }

        logger.Warning($"{items.Count} Windows güncellemesi bulundu.");
        return new ModuleResult
        {
            Key = Key,
            Status = ComponentStatus.UpdateAvailable,
            Summary = $"{items.Count} güncelleme mevcut",
            Details = "Windows güncelleştirmeleri mevcut" + (pending ? "\nAyrıca yeniden başlatma bekleniyor." : ""),
            Items = items,
            ActionableCount = items.Count,
            RebootRequired = pending
        };
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var ids = check.Items.Where(i => i.UpdateAvailable).Select(i => i.Id).ToArray();
        if (ids.Length == 0) return check;

        logger.Info($"Windows Update: {ids.Length} güncelleme indirilip kurulacak...");
        var script = Prepare(InstallScript.Replace("__IDS__", PowerShellRunner.ToPsLiteral(ids)));
        // Kurulum başladıktan sonra yarıda kesilmemesi için iptal belirteci iletilmez.
        var ps = await PowerShellRunner.RunAsync(script, InstallTimeout, CancellationToken.None, m => logger.Info("  " + m));

        if (!ps.Ok)
        {
            var reason = Explain(ps.DescribeFailure("Windows Update"));
            logger.Error("Windows Update kurulumu başarısız: " + reason);
            return ModuleResult.Failed(Key, reason);
        }

        var data = ps.Data!.Value;
        var reboot = data.Bool("rebootRequired") == true;

        if (data.Bool("noneFound") == true)
        {
            logger.Info("Seçilen güncellemeler artık listede değil (başka bir işlemle kurulmuş olabilir).");
            return new ModuleResult
            {
                Key = Key,
                Status = reboot ? ComponentStatus.RebootRequired : ComponentStatus.UpToDate,
                Summary = reboot ? "Yeniden başlatma gerekiyor" : "Güncel",
                Reason = reboot ? "Yeniden başlatma gerekiyor." : null,
                RebootRequired = reboot
            };
        }

        var results = data.Arr("items").ToList();
        var items = new List<UpdateItem>();
        var failures = new List<string>();
        var ok = 0;
        foreach (var r in results)
        {
            var code = r.Long("resultCode") ?? 4;
            var title = r.Str("title") ?? "(adsız)";
            var success = code is 2 or 3;
            if (success) ok++;
            else failures.Add($"{title}: {ResultCodeText(code)} {r.Str("note")} {r.Str("hresult")}".Trim());
            items.Add(new UpdateItem
            {
                Name = title,
                Id = r.Str("id") ?? string.Empty,
                UpdateAvailable = !success,
                StatusText = success
                    ? (r.Bool("reboot") == true ? "Kuruldu – yeniden başlatma gerekli" : "Kuruldu")
                    : "Başarısız: " + ResultCodeText(code)
            });
        }

        ComponentStatus status;
        string summary;
        if (failures.Count == 0)
        {
            status = reboot ? ComponentStatus.RebootRequired : ComponentStatus.Updated;
            summary = reboot ? $"{ok} güncelleme kuruldu – yeniden başlatma gerekli" : $"{ok} güncelleme kuruldu";
            logger.Success("Windows Update: " + summary);
        }
        else
        {
            status = ok > 0 ? ComponentStatus.PartiallyUpdated : ComponentStatus.Failed;
            summary = ok > 0 ? $"{ok}/{results.Count} kuruldu, {failures.Count} başarısız" : "Güncellemeler kurulamadı";
            logger.Error("Windows Update: " + summary);
        }
        if (reboot)
            logger.Warning("Windows güncelleştirmelerinin tamamlanması için yeniden başlatma gerekiyor. Bilgisayar sizin onayınız olmadan yeniden başlatılmayacak.");

        var reasons = new List<string>(failures);
        if (reboot) reasons.Add("Yeniden başlatma gerekiyor.");

        return new ModuleResult
        {
            Key = Key,
            Status = status,
            Summary = summary,
            Details = $"Kurulan: {ok}\nBaşarısız: {failures.Count}",
            Reason = reasons.Count > 0 ? string.Join("\n", reasons) : null,
            Items = items,
            RebootRequired = reboot
        };
    }

    private static string ResultCodeText(long code) => code switch
    {
        0 => "başlatılmadı",
        1 => "devam ediyor",
        2 => "başarılı",
        3 => "hatalarla tamamlandı",
        4 => "başarısız",
        5 => "iptal edildi",
        _ => $"bilinmeyen sonuç ({code})"
    };

    /// <summary>Sık görülen WUA HRESULT kodlarını anlaşılır açıklamaya çevirir.</summary>
    private static string Explain(string message)
    {
        var map = new (string Code, string Text)[]
        {
            ("0x8024402C", "Windows Update sunucusuna ulaşılamadı (internet bağlantısı yok veya DNS çözümlenemedi)."),
            ("0x80072EE7", "Sunucu adı çözümlenemedi – internet bağlantınızı kontrol edin."),
            ("0x80072EFD", "Windows Update sunucusuna bağlanılamadı – internet bağlantınızı kontrol edin."),
            ("0x80072EE2", "Windows Update sunucusu zaman aşımına uğradı."),
            ("0x8024401C", "Windows Update sunucusu zaman aşımına uğradı."),
            ("0x80070422", "Windows Update servisi devre dışı veya başlatılamıyor."),
            ("0x8024001E", "Windows Update servisi kapanıyor; daha sonra tekrar deneyin."),
            ("0x80240016", "Başka bir güncelleme kurulumu sürüyor."),
            ("0x80070005", "Erişim reddedildi – yönetici yetkisi gerekli."),
            ("0x8024002E", "Windows Update erişimi grup ilkesiyle engellenmiş (WSUS/ilke).")
        };
        foreach (var (code, text) in map)
            if (message.Contains(code, StringComparison.OrdinalIgnoreCase))
                return $"{text} ({code})";
        return message;
    }
}

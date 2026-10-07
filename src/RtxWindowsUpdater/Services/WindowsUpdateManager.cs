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
                if ($h -eq -2145124302) { Write-Log '«Arama ölçütü desteklenmedi, alternatif ölçütle yeniden deneniyor...|Search criteria not supported, retrying with alternative criteria...»'; return $searcher.Search("__FALLBACK__") }
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
        if (-not $svc) { Write-Result @{ error = '«Windows Update servisi (wuauserv) bu sistemde bulunamadı.|The Windows Update service (wuauserv) was not found on this system.»' }; return }
        $mode = (Get-CimInstance Win32_Service -Filter "Name='wuauserv'").StartMode
        if ($mode -eq 'Disabled') { Write-Result @{ serviceDisabled = $true }; return }
        Write-Log ('«Windows Update servisi: |Windows Update service:»' + $svc.Status + '« (başlangıç: | (startup:»' + $mode + ')')

        $pending = $false
        try { $pending = [bool](New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired } catch { }

        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'E-mre Control Center'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        Write-Log '«Microsoft Update sunucularında arama yapılıyor (birkaç dakika sürebilir)...|Searching the Microsoft Update servers (this can take a few minutes)...»'
        $res = Invoke-WuSearch $searcher

        $list = New-Object System.Collections.ArrayList
        foreach ($u in $res.Updates) {
            if (Test-Definition $u) { Write-Log ('«Defender tanım güncellemesi (Defender bölümünde işlenecek): |Defender definition update (handled in the Defender section):»' + $u.Title); continue }
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
        $session.ClientApplicationID = 'E-mre Control Center'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        Write-Log '«Güncellemeler Microsoft Update üzerinde yeniden doğrulanıyor...|Verifying the updates again on Microsoft Update...»'
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
        if ($installer.IsBusy) { Write-Result @{ error = '«Windows Update şu anda başka bir kurulum yapıyor. Tamamlandıktan sonra tekrar deneyin.|Windows Update is currently running another installation. Try again after it finishes.»' }; return }

        $toDownload = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $coll) { if (-not $u.IsDownloaded) { [void]$toDownload.Add($u) } }
        if ($toDownload.Count -gt 0) {
            $mb = 0; foreach ($u in $toDownload) { $mb += $u.MaxDownloadSize }
            Write-Log ('«{0} güncelleme indiriliyor (en fazla {1:N0} MB)...|Downloading {0} update(s) (up to {1:N0} MB)...»' -f $toDownload.Count, ($mb / 1MB))
            $dl = $session.CreateUpdateDownloader()
            $dl.Updates = $toDownload
            $dres = $dl.Download()
            Write-Log ('«İndirme tamamlandı (sonuç kodu {0}, 0x{1:X8}).|Download completed (result code {0}, 0x{1:X8}).»' -f [int]$dres.ResultCode, $dres.HResult)
        }

        $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
        $items = New-Object System.Collections.ArrayList
        foreach ($u in $coll) {
            if ($u.IsDownloaded) { [void]$toInstall.Add($u) }
            else {
                [void]$items.Add(@{ id = [string]$u.Identity.UpdateID; title = [string]$u.Title; resultCode = 4; hresult = ''; reboot = $false; «note = 'indirilemedi'|note = 'could not be downloaded'» })
                Write-Log ('«İndirilemedi: |Could not download:»' + $u.Title)
            }
        }
        if ($toInstall.Count -eq 0) { Write-Result @{ resultCode = 4; rebootRequired = $false; items = $items; hresult = '' }; return }

        Write-Log ('«{0} güncelleme kuruluyor... (bilgisayarı kapatmayın)|Installing {0} update(s)... (do not turn off the computer)»' -f $toInstall.Count)
        $installer.Updates = $toInstall
        $ires = $installer.Install()
        for ($i = 0; $i -lt $toInstall.Count; $i++) {
            $u = $toInstall.Item($i)
            $r = $ires.GetUpdateResult($i)
            [void]$items.Add(@{ id = [string]$u.Identity.UpdateID; title = [string]$u.Title; resultCode = [int]$r.ResultCode; hresult = ('0x{0:X8}' -f $r.HResult); reboot = [bool]$r.RebootRequired; note = '' })
            Write-Log ('«{0} → sonuç kodu {1}|{0} → result code {1}»' -f $u.Title, [int]$r.ResultCode)
        }
        Write-Result @{ resultCode = [int]$ires.ResultCode; rebootRequired = [bool]$ires.RebootRequired; items = $items; hresult = ('0x{0:X8}' -f $ires.HResult) }
        """;

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info(L.T("Windows Update kontrol ediliyor...", "Checking Windows Update..."));
        var ps = await PowerShellRunner.RunAsync(Prepare(CheckScript), SearchTimeout, ct, m => logger.Info("  " + m),
            traceName: "Windows Update Agent – IUpdateSearcher.Search(\"" + Criteria + "\")");

        if (!ps.Ok)
        {
            var reason = Explain(ps.DescribeFailure("Windows Update"));
            logger.Error(L.T("Windows Update kontrolü gerçekleştirilemedi: ", "The Windows Update check could not be performed: ") + reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var data = ps.Data!.Value;
        if (data.Bool("serviceDisabled") == true)
        {
            var reason = L.T("Windows Update servisi (wuauserv) devre dışı bırakılmış. Uygulama sistem ayarlarını değiştirmez; servisi Hizmetler (services.msc) üzerinden siz etkinleştirmelisiniz.", "The Windows Update service (wuauserv) has been disabled. The app does not change system settings; you must enable the service yourself in Services (services.msc).");
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var resultCode = data.Long("resultCode") ?? -1;
        if (resultCode is not (2 or 3))
        {
            var reason = L.T($"Windows Update araması tamamlanamadı (sonuç kodu {resultCode}).", $"The Windows Update search could not be completed (result code {resultCode}).");
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var pending = data.Bool("rebootPending") == true;
        ExecutionTrace.Note(L.T($"WUA arama sonuç kodu: {resultCode} ({(resultCode == 2 ? "başarılı" : "hatalarla tamamlandı")}); bekleyen yeniden başlatma: {(pending ? "evet" : "hayır")}", $"WUA search result code: {resultCode} ({(resultCode == 2 ? "succeeded" : "completed with errors")}); pending restart: {(pending ? "yes" : "no")}"));

        // Windows güncellemelerinin "mevcut sürümü" yoktur; KB numarası "yeni" sütununda, sınıf ve boyut durum metninde gösterilir.
        var items = data.Arr("updates").Select(u =>
        {
            var size = u.Long("size") ?? 0;
            var extra = string.Join(" · ", new[] { u.Str("classification"), size > 0 ? $"{size / 1048576.0:N0} MB" : null }
                .Where(s => !string.IsNullOrEmpty(s)));
            return new UpdateItem
            {
                Name = u.Str("title") ?? L.T("(adsız güncelleme)", "(unnamed update)"),
                Id = u.Str("id") ?? string.Empty,
                CurrentVersion = string.Empty,
                NewVersion = u.Str("kb") ?? string.Empty,
                UpdateAvailable = true,
                StatusText = extra.Length > 0 ? L.T($"Güncelleme mevcut · {extra}", $"Update available · {extra}") : L.T("Güncelleme mevcut", "Update available")
            };
        }).ToList();

        foreach (var i in items)
            logger.Info(L.T($"  Bulundu: {i.Name}", $"  Found: {i.Name}"));

        if (pending)
            logger.Warning(L.T("Önceki güncellemeler için yeniden başlatma bekleniyor.", "A restart is pending for previous updates."));

        if (items.Count == 0)
        {
            logger.Success(L.T("Windows Update: güncelleme bulunamadı.", "Windows Update: no updates found."));
            return new ModuleResult
            {
                Key = Key,
                Status = pending ? ComponentStatus.RebootRequired : ComponentStatus.UpToDate,
                Summary = pending ? L.T("Güncel – yeniden başlatma bekleniyor", "Up to date – restart pending") : L.T("Güncel", "Up to date"),
                Details = pending ? L.T("Daha önce kurulan güncellemeler yeniden başlatma bekliyor.", "Previously installed updates are waiting for a restart.") : L.T("Bekleyen Windows güncelleştirmesi yok.", "No pending Windows updates."),
                Reason = pending ? L.T("Yeniden başlatma gerekiyor.", "A restart is required.") : null,
                RebootRequired = pending
            };
        }

        logger.Warning(L.T($"{items.Count} Windows güncellemesi bulundu.", $"{items.Count} Windows update(s) found."));
        return new ModuleResult
        {
            Key = Key,
            Status = ComponentStatus.UpdateAvailable,
            Summary = L.T($"{items.Count} güncelleme mevcut", $"{items.Count} update(s) available"),
            Details = L.T("Windows güncelleştirmeleri mevcut", "Windows updates available") + (pending ? L.T("\nAyrıca yeniden başlatma bekleniyor.", "\nA restart is also pending.") : ""),
            Items = items,
            ActionableCount = items.Count,
            RebootRequired = pending
        };
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var ids = check.Items.Where(i => i.UpdateAvailable).Select(i => i.Id).ToArray();
        if (ids.Length == 0) return check;

        logger.Info(L.T($"Windows Update: {ids.Length} güncelleme indirilip kurulacak...", $"Windows Update: {ids.Length} update(s) will be downloaded and installed..."));
        var script = Prepare(InstallScript.Replace("__IDS__", PowerShellRunner.ToPsLiteral(ids)));
        // Kurulum başladıktan sonra yarıda kesilmemesi için iptal belirteci iletilmez.
        var ps = await PowerShellRunner.RunAsync(script, InstallTimeout, CancellationToken.None, m => logger.Info("  " + m),
            traceName: "Windows Update Agent – UpdateDownloader.Download + UpdateInstaller.Install");

        if (!ps.Ok)
        {
            var reason = Explain(ps.DescribeFailure("Windows Update"));
            logger.Error(L.T("Windows Update kurulumu başarısız: ", "Windows Update installation failed: ") + reason);
            return ModuleResult.Failed(Key, reason);
        }

        var data = ps.Data!.Value;
        var reboot = data.Bool("rebootRequired") == true;

        if (data.Bool("noneFound") == true)
        {
            logger.Info(L.T("Seçilen güncellemeler artık listede değil (başka bir işlemle kurulmuş olabilir).", "The selected updates are no longer in the list (they may have been installed by another process)."));
            return new ModuleResult
            {
                Key = Key,
                Status = reboot ? ComponentStatus.RebootRequired : ComponentStatus.UpToDate,
                Summary = reboot ? L.T("Yeniden başlatma gerekiyor", "Restart required") : L.T("Güncel", "Up to date"),
                Reason = reboot ? L.T("Yeniden başlatma gerekiyor.", "A restart is required.") : null,
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
            var title = r.Str("title") ?? L.T("(adsız)", "(unnamed)");
            var success = code is 2 or 3;
            if (success) ok++;
            else failures.Add($"{title}: {ResultCodeText(code)} {r.Str("note")} {r.Str("hresult")}".Trim());
            items.Add(new UpdateItem
            {
                Name = title,
                Id = r.Str("id") ?? string.Empty,
                UpdateAvailable = !success,
                StatusText = success
                    ? (r.Bool("reboot") == true ? L.T("Kuruldu – yeniden başlatma gerekli", "Installed – restart required") : L.T("Kuruldu", "Installed"))
                    : L.T("Başarısız: ", "Failed: ") + ResultCodeText(code)
            });
        }

        ComponentStatus status;
        string summary;
        if (failures.Count == 0)
        {
            status = reboot ? ComponentStatus.RebootRequired : ComponentStatus.Updated;
            summary = reboot ? L.T($"{ok} güncelleme kuruldu – yeniden başlatma gerekli", $"{ok} update(s) installed – restart required") : L.T($"{ok} güncelleme kuruldu", $"{ok} update(s) installed");
            logger.Success("Windows Update: " + summary);
        }
        else
        {
            status = ok > 0 ? ComponentStatus.PartiallyUpdated : ComponentStatus.Failed;
            summary = ok > 0 ? L.T($"{ok}/{results.Count} kuruldu, {failures.Count} başarısız", $"{ok}/{results.Count} installed, {failures.Count} failed") : L.T("Güncellemeler kurulamadı", "Updates could not be installed");
            logger.Error("Windows Update: " + summary);
        }
        if (reboot)
            logger.Warning(L.T("Windows güncelleştirmelerinin tamamlanması için yeniden başlatma gerekiyor. Bilgisayar sizin onayınız olmadan yeniden başlatılmayacak.", "A restart is required to complete the Windows updates. The computer will not restart without your approval."));

        var reasons = new List<string>(failures);
        if (reboot) reasons.Add(L.T("Yeniden başlatma gerekiyor.", "A restart is required."));

        return new ModuleResult
        {
            Key = Key,
            Status = status,
            Summary = summary,
            Details = L.T($"Kurulan: {ok}\nBaşarısız: {failures.Count}", $"Installed: {ok}\nFailed: {failures.Count}"),
            Reason = reasons.Count > 0 ? string.Join("\n", reasons) : null,
            Items = items,
            RebootRequired = reboot
        };
    }

    private static string ResultCodeText(long code) => code switch
    {
        0 => L.T("başlatılmadı", "not started"),
        1 => L.T("devam ediyor", "in progress"),
        2 => L.T("başarılı", "succeeded"),
        3 => L.T("hatalarla tamamlandı", "completed with errors"),
        4 => L.T("başarısız", "failed"),
        5 => L.T("iptal edildi", "cancelled"),
        _ => L.T($"bilinmeyen sonuç ({code})", $"unknown result ({code})")
    };

    /// <summary>Sık görülen WUA HRESULT kodlarını anlaşılır açıklamaya çevirir.</summary>
    private static string Explain(string message)
    {
        var map = new (string Code, string Text)[]
        {
            ("0x8024402C", L.T("Windows Update sunucusuna ulaşılamadı (internet bağlantısı yok veya DNS çözümlenemedi).", "Could not reach the Windows Update server (no internet connection or DNS could not resolve).")),
            ("0x80072EE7", L.T("Sunucu adı çözümlenemedi – internet bağlantınızı kontrol edin.", "The server name could not be resolved – check your internet connection.")),
            ("0x80072EFD", L.T("Windows Update sunucusuna bağlanılamadı – internet bağlantınızı kontrol edin.", "Could not connect to the Windows Update server – check your internet connection.")),
            ("0x80072EE2", L.T("Windows Update sunucusu zaman aşımına uğradı.", "The Windows Update server timed out.")),
            ("0x8024401C", L.T("Windows Update sunucusu zaman aşımına uğradı.", "The Windows Update server timed out.")),
            ("0x80070422", L.T("Windows Update servisi devre dışı veya başlatılamıyor.", "The Windows Update service is disabled or cannot start.")),
            ("0x8024001E", L.T("Windows Update servisi kapanıyor; daha sonra tekrar deneyin.", "The Windows Update service is shutting down; try again later.")),
            ("0x80240016", L.T("Başka bir güncelleme kurulumu sürüyor.", "Another update installation is in progress.")),
            ("0x80070005", L.T("Erişim reddedildi – yönetici yetkisi gerekli.", "Access denied – administrator rights required.")),
            ("0x8024002E", L.T("Windows Update erişimi grup ilkesiyle engellenmiş (WSUS/ilke).", "Windows Update access is blocked by group policy (WSUS/policy)."))
        };
        foreach (var (code, text) in map)
            if (message.Contains(code, StringComparison.OrdinalIgnoreCase))
                return $"{text} ({code})";
        return message;
    }
}

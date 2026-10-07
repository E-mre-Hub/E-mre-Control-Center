using System.IO;
using System.IO.Compression;
using System.Text;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Destek paketine girebilecek bir içerik (kullanıcı oluşturmadan önce görür ve seçer).</summary>
public sealed record SupportItem(string Key, string Title, string Description);

public sealed record SupportPackageResult(bool Success, string Message, string? Path, IReadOnlyList<string> Entries, long Size);

/// <summary>
/// Destek paketi: seçilen tanılama verilerini tek ZIP dosyasında toplar (sistem raporu, uygulama günlükleri, sürücü listesi, önemli olay
/// kayıtları, ağ tanılaması, güncelleme geçmişi, hata bilgileri). Tüm metinler <see cref="PersonalDataMask"/> ile maskelenir; belge,
/// parola, tarayıcı verisi, seri numarası eklenmez. Paket yalnızca kullanıcının seçtiği konuma yazılır ve yeniden açılarak doğrulanır.
/// Hiçbir yere gönderilmez.
/// </summary>
public sealed class SupportPackageService(Logger logger, SystemReportService reports)
{
    public static IReadOnlyList<SupportItem> Items { get; } =
    [
        new("report", L.T("Sistem raporu", "System report"), L.T("Windows, donanım, depolama, ağ, sürücü, güvenlik, hizmet, başlangıç, olay ve çökme özeti (TXT + HTML + JSON).", "Summary of Windows, hardware, storage, network, drivers, security, services, startup, events and crashes (TXT + HTML + JSON).")),
        new("logs", L.T("Uygulama günlükleri", "App logs"), L.T("E-mre Control Center'ın son 5 oturum günlüğü (bu uygulamanın yaptığı işlemler ve gerçek sonuçları).", "The last 5 session logs of E-mre Control Center (the operations this app performed and their real results).")),
        new("drivers", L.T("Sürücü listesi", "Driver list"), L.T("Tüm aygıt sürücüleri: aygıt, üretici, sürüm, tarih, imza ve Aygıt Yöneticisi durumu.", "All device drivers: device, manufacturer, version, date, signature and Device Manager status.")),
        new("events", L.T("Önemli olay kayıtları", "Important event records"), L.T("Sistem ve Uygulama günlüklerindeki son 7 günün kritik ve hata kayıtları (Windows'un kendi iletileri).", "Critical and error records of the last 7 days in the System and Application logs (Windows' own messages).")),
        new("network", L.T("Ağ tanılaması", "Network diagnostics"), L.T("Bağdaştırıcı yapılandırması ve paket oluşturulurken çalıştırılan gerçek testler (ping, DNS, HTTPS, DNS sunucuları).", "Adapter configuration and the real tests run while creating the package (ping, DNS, HTTPS, DNS servers).")),
        new("updates", L.T("Güncelleme geçmişi", "Update history"), L.T("Windows Update geçmişi (son 50) ve bu uygulamanın işlem geçmişi.", "Windows Update history (last 50) and this app's operation history.")),
        new("errors", L.T("Hata bilgileri", "Error information"), L.T("Günlüklerdeki uyarı / hata satırları ve son 30 günün çökme kayıtları.", "Warning / error lines in the logs and the crash records of the last 30 days."))
    ];

    public async Task<SupportPackageResult> CreateAsync(string zipPath, IReadOnlyCollection<string> keys, IReadOnlyList<OperationRecord> history,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (keys.Count == 0) return new SupportPackageResult(false, L.T("Pakete eklenecek içerik seçilmedi.", "No content was selected for the package."), null, [], 0);
        var mask = new PersonalDataMask();
        var files = new List<(string Name, string Content)>();
        var notes = new List<string>();

        async Task Step(string key, string text, Func<Task> body)
        {
            if (!keys.Contains(key)) return;
            ct.ThrowIfCancellationRequested();
            progress?.Report(text);
            try { await body(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                notes.Add(L.T($"{text.TrimEnd('…')}: okunamadı – {ex.Message}", $"{text.TrimEnd('…')}: unreadable – {ex.Message}"));
                logger.Warning(L.T($"Destek paketi – {text} başarısız: {ex.Message}", $"Support package – {text} failed: {ex.Message}"));
            }
        }

        await Step("report", L.T("Sistem raporu hazırlanıyor…", "Preparing the system report…"), async () =>
        {
            var doc = await reports.CollectAsync(ReportParts.All, progress, ct);
            files.Add((L.T("sistem-raporu.txt", "system-report.txt"), SystemReportService.ToText(doc)));
            files.Add((L.T("sistem-raporu.html", "system-report.html"), SystemReportService.ToHtml(doc)));
            files.Add((L.T("sistem-raporu.json", "system-report.json"), SystemReportService.ToJson(doc)));
        });
        await Step("logs", L.T("Uygulama günlükleri ekleniyor…", "Adding app logs…"), () =>
        {
            foreach (var f in RecentLogs(5))
                files.Add(("gunlukler/" + Path.GetFileName(f), mask.Apply(ReadShared(f, 2 * 1024 * 1024))));
            return Task.CompletedTask;
        });
        await Step("drivers", L.T("Sürücü listesi okunuyor…", "Reading the driver list…"), async () =>
        {
            var d = await new DriverService(logger).ScanAsync(ct);
            var sb = new StringBuilder(L.T("Aygıt\tKategori\tÜretici\tSağlayıcı\tSürüm\tTarih\tİmza\tDurum\r\n", "Device\tCategory\tManufacturer\tProvider\tVersion\tDate\tSignature\tStatus\r\n"));
            foreach (var x in d.Drivers)
                sb.Append($"{x.DeviceName}\t{x.Category}\t{x.Manufacturer}\t{x.Provider}\t{x.Version}\t{x.DateText}\t{x.SignedText}\t{x.StatusText}\r\n");
            if (d.Error is not null) sb.Append(L.T("Not: ", "Note: ")).Append(d.Error).Append("\r\n");
            files.Add(("suruculer.tsv", mask.Apply(sb.ToString())));
        });
        await Step("events", L.T("Olay kayıtları okunuyor…", "Reading event records…"), async () =>
        {
            var q = await new EventLogService(logger).QueryAsync(["System", "Application"], [1, 2], TimeSpan.FromDays(7), ct);
            var sb = new StringBuilder();
            foreach (var e in q.Entries)
                sb.Append($"{e.TimeText} | {e.LogText} | {e.LevelText} | {e.Provider} | {e.Id}\r\n{e.Message}\r\n\r\n");
            foreach (var err in q.Errors) sb.Append(L.T("Not: ", "Note: ")).Append(err).Append("\r\n");
            if (q.Truncated) sb.Append(L.T($"Not: en yeni {EventLogService.MaxEntries} kayıt alındı.\r\n", $"Note: the newest {EventLogService.MaxEntries} records were taken.\r\n"));
            files.Add(("olay-kayitlari.txt", mask.Apply(sb.Length == 0 ? L.T("Son 7 günde kritik / hata kaydı yok.", "No critical / error records in the last 7 days.") : sb.ToString())));
        });
        await Step("network", L.T("Ağ testleri çalıştırılıyor…", "Running network tests…"), async () =>
        {
            var net = new NetworkDiagnosticsService(logger);
            var snap = await net.ReadAsync(ct);
            var tests = await net.RunTestsAsync(snap, progress, ct);
            var dns = await new DnsDiagnosticsService(logger).RunAsync(snap.Adapters, progress, ct);
            var sb = new StringBuilder();
            sb.Append(L.T("Windows bağlantı durumu: ", "Windows connection status: ")).Append(snap.ConnectivityText ?? "—").Append("\r\n\r\n");
            foreach (var a in snap.Adapters)
                sb.Append(L.T($"{a.Name} ({a.TypeText}, {a.Description}) – {a.StatusText}, hız {a.SpeedText}\r\n  IPv4: {a.IPv4Text}\r\n  IPv6: {a.IPv6Text}\r\n", $"{a.Name} ({a.TypeText}, {a.Description}) – {a.StatusText}, speed {a.SpeedText}\r\n  IPv4: {a.IPv4Text}\r\n  IPv6: {a.IPv6Text}\r\n") +
                          L.T($"  Ağ geçidi: {a.GatewayText}\r\n  DNS: {a.DnsText}\r\n  DHCP: {a.DhcpText}\r\n  MAC: {a.MacText}\r\n", $"  Gateway: {a.GatewayText}\r\n  DNS: {a.DnsText}\r\n  DHCP: {a.DhcpText}\r\n  MAC: {a.MacText}\r\n"));
            sb.Append(L.T("\r\nTESTLER\r\n", "\r\nTESTS\r\n"));
            foreach (var t in tests) sb.Append($"{t.Title}: {t.StateText} – {t.Summary}\r\n  {t.Detail?.ReplaceLineEndings("\r\n  ")}\r\n");
            sb.Append(L.T("\r\nDNS SUNUCULARI\r\n", "\r\nDNS SERVERS\r\n"));
            if (dns.Error is not null) sb.Append(dns.Error).Append("\r\n");
            foreach (var s in dns.Servers)
                sb.Append($"{s.ServerText} ({s.Family}, {s.Adapter}): {s.ResultText}, {s.TimeText}, DNSSEC: {s.DnssecText} {s.FailuresText}\r\n");
            files.Add(("ag-tanilama.txt", mask.Apply(sb.ToString())));
        });
        await Step("updates", L.T("Güncelleme geçmişi okunuyor…", "Reading the update history…"), async () =>
        {
            var sb = new StringBuilder(L.T("WINDOWS UPDATE GEÇMİŞİ (son 50)\r\n", "WINDOWS UPDATE HISTORY (last 50)\r\n"));
            var ps = await PowerShellRunner.RunAsync(WuHistoryScript, TimeSpan.FromSeconds(90), ct, traceName: L.T("Windows Update geçmişi", "Windows Update history"));
            if (ps.Ok)
                foreach (var h in ps.Data!.Value.Arr("items"))
                    sb.Append($"{h.Str("date")} | {WuResult(h.Long("result"))} | {h.Str("title")}" + (h.Str("hresult") is { } hr && hr != "0x00000000" ? $" | {hr}" : "") + "\r\n");
            else sb.Append(L.T("Okunamadı: ", "Unreadable: ")).Append(ps.DescribeFailure(L.T("Windows Update geçmişi", "Windows Update history"))).Append("\r\n");
            sb.Append(L.T("\r\nE-MRE CONTROL CENTER İŞLEM GEÇMİŞİ\r\n", "\r\nE-MRE CONTROL CENTER OPERATION HISTORY\r\n"));
            foreach (var r in history)
                sb.Append(L.T($"{r.TimeText} | {r.Title} | {r.SummaryText} | süre {r.DurationText}", $"{r.TimeText} | {r.Title} | {r.SummaryText} | duration {r.DurationText}") + (string.IsNullOrEmpty(r.ErrorText) ? "" : L.T($" | hata: {r.ErrorText}", $" | error: {r.ErrorText}")) + "\r\n");
            files.Add(("guncelleme-gecmisi.txt", mask.Apply(sb.ToString())));
        });
        await Step("errors", L.T("Hata bilgileri toplanıyor…", "Collecting error information…"), async () =>
        {
            var sb = new StringBuilder(L.T("GÜNLÜKLERDEKİ UYARI / HATA SATIRLARI\r\n", "WARNING / ERROR LINES IN THE LOGS\r\n"));
            foreach (var f in RecentLogs(5))
                foreach (var line in ReadShared(f, 2 * 1024 * 1024).Split('\n'))
                    if (line.Contains("[ERROR]", StringComparison.Ordinal) || line.Contains("[WARNING]", StringComparison.Ordinal))
                        sb.Append(Path.GetFileName(f)).Append(": ").Append(line.TrimEnd('\r')).Append("\r\n");
            var c = await new CrashAnalysisService(logger).AnalyzeAsync(TimeSpan.FromDays(30), AdminPrivilegeManager.IsElevated, ct);
            sb.Append(L.T($"\r\nÇÖKME KAYITLARI (30 gün): {c.BugChecks} BugCheck, {c.Unexpected} beklenmedik kapanma, {c.DisplayResets} ekran sürücüsü sıfırlama, {c.Hardware} WHEA\r\n", $"\r\nCRASH RECORDS (30 days): {c.BugChecks} BugCheck, {c.Unexpected} unexpected shutdown(s), {c.DisplayResets} display driver reset(s), {c.Hardware} WHEA\r\n"));
            foreach (var e in c.Events)
                sb.Append($"{e.TimeText} | {e.KindText} | {e.CodeText} | {e.ModuleText} | {e.RelationText}\r\n");
            if (c.DumpNote is not null) sb.Append(L.T("Not: ", "Note: ")).Append(c.DumpNote).Append("\r\n");
            files.Add((L.T("hata-bilgileri.txt", "error-info.txt"), mask.Apply(sb.ToString())));
        });

        var readme = new StringBuilder(L.T("E-mre Control Center – Destek Paketi\r\n", "E-mre Control Center – Support Package\r\n"))
            .Append(L.T($"Oluşturulma: {DateTime.Now:dd.MM.yyyy HH:mm:ss} · sürüm {AppInfo.Version}\r\n", $"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss} · version {AppInfo.Version}\r\n"))
            .Append(L.T("İçerik: ", "Contents: ")).Append(string.Join(", ", Items.Where(i => keys.Contains(i.Key)).Select(i => i.Title))).Append("\r\n")
            .Append(L.T("Gizlenen bilgiler: ", "Masked information: ")).Append(PersonalDataMask.DescriptionText).Append("\r\n")
            .Append(L.T("Bu paket hiçbir yere otomatik gönderilmez; yalnızca sizin paylaştığınız kişiye ulaşır.\r\n", "This package is never sent anywhere automatically; it reaches only the person you share it with.\r\n"));
        if (notes.Count > 0) readme.Append(L.T("\r\nEKSİK KALAN BÖLÜMLER\r\n", "\r\nMISSING SECTIONS\r\n")).Append(string.Join("\r\n", notes)).Append("\r\n");
        files.Insert(0, (L.T("BENIOKU.txt", "README.txt"), mask.Apply(readme.ToString())));

        progress?.Report(L.T("ZIP dosyası yazılıyor…", "Writing the ZIP file…"));
        var temp = zipPath + ".yaziliyor";
        try
        {
            await Task.Run(() =>
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var (name, content) in files)
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(true));
                        w.Write(content);
                    }
                }
                File.Move(temp, zipPath, overwrite: true);
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (ex is OperationCanceledException) throw;
            logger.Error(L.T("Destek paketi yazılamadı: ", "Could not write the support package: ") + ex.Message);
            return new SupportPackageResult(false, L.T("Paket yazılamadı: ", "Could not write the package: ") + ex.Message, null, [], 0);
        }

        // Doğrulama: ZIP yeniden açılır, girdiler okunur.
        try
        {
            using var check = ZipFile.OpenRead(zipPath);
            var entries = check.Entries.Select(e => e.FullName).ToList();
            var size = new FileInfo(zipPath).Length;
            if (entries.Count != files.Count) throw new InvalidDataException(L.T($"{files.Count} dosya yazıldı, {entries.Count} okundu", $"{files.Count} files written, {entries.Count} read"));
            logger.Info(L.T($"Destek paketi oluşturuldu: {zipPath} ({entries.Count} dosya, {Formats.Bytes(size)})", $"Support package created: {zipPath} ({entries.Count} files, {Formats.Bytes(size)})") + (notes.Count > 0 ? L.T("; eksik: ", "; missing: ") + string.Join("; ", notes) : "."));
            return new SupportPackageResult(true, notes.Count == 0 ? L.T("Destek paketi oluşturuldu ve doğrulandı.", "Support package created and verified.") : L.T($"Paket oluşturuldu; {notes.Count} bölüm okunamadı (BENIOKU.txt'de yazılı).", $"Package created; {notes.Count} section(s) could not be read (listed in README.txt)."),
                zipPath, entries, size);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.Error(L.T("Destek paketi doğrulanamadı: ", "Could not verify the support package: ") + ex.Message);
            return new SupportPackageResult(false, L.T("Paket yazıldı ancak doğrulanamadı: ", "The package was written but could not be verified: ") + ex.Message, zipPath, [], 0);
        }
    }

    private const string WuHistoryScript = """
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        $count = $searcher.GetTotalHistoryCount()
        $list = New-Object System.Collections.ArrayList
        if ($count -gt 0) {
            foreach ($h in $searcher.QueryHistory(0, [Math]::Min($count, 50))) {
                [void]$list.Add(@{ title = [string]$h.Title; date = $h.Date.ToLocalTime().ToString('yyyy-MM-dd HH:mm'); result = [int]$h.ResultCode; hresult = ('0x{0:X8}' -f $h.HResult) })
            }
        }
        Write-Result @{ items = @($list); total = $count }
        """;

    private static string WuResult(long? code) => code switch
    {
        2 => L.T("Başarılı", "Succeeded"), 3 => L.T("Hatalarla başarılı", "Succeeded with errors"), 4 => L.T("Başarısız", "Failed"), 5 => L.T("İptal edildi", "Cancelled"), 1 => L.T("Sürüyor", "In progress"), 0 => L.T("Başlamadı", "Not started"), _ => "—"
    };

    private IEnumerable<string> RecentLogs(int count)
    {
        try
        {
            return new DirectoryInfo(logger.LogDirectory).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTime).Take(count).Select(f => f.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Açık (yazılmakta olan) günlük dosyasını paylaşımlı okur; çok büyükse son kısmı alınır.</summary>
    private static string ReadShared(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

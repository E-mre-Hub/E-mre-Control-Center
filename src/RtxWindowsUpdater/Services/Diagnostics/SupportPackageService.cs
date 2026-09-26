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
        new("report", "Sistem raporu", "Windows, donanım, depolama, ağ, sürücü, güvenlik, hizmet, başlangıç, olay ve çökme özeti (TXT + HTML + JSON)."),
        new("logs", "Uygulama günlükleri", "E-mre Control Center'ın son 5 oturum günlüğü (bu uygulamanın yaptığı işlemler ve gerçek sonuçları)."),
        new("drivers", "Sürücü listesi", "Tüm aygıt sürücüleri: aygıt, üretici, sürüm, tarih, imza ve Aygıt Yöneticisi durumu."),
        new("events", "Önemli olay kayıtları", "Sistem ve Uygulama günlüklerindeki son 7 günün kritik ve hata kayıtları (Windows'un kendi iletileri)."),
        new("network", "Ağ tanılaması", "Bağdaştırıcı yapılandırması ve paket oluşturulurken çalıştırılan gerçek testler (ping, DNS, HTTPS, DNS sunucuları)."),
        new("updates", "Güncelleme geçmişi", "Windows Update geçmişi (son 50) ve bu uygulamanın işlem geçmişi."),
        new("errors", "Hata bilgileri", "Günlüklerdeki uyarı / hata satırları ve son 30 günün çökme kayıtları.")
    ];

    public async Task<SupportPackageResult> CreateAsync(string zipPath, IReadOnlyCollection<string> keys, IReadOnlyList<OperationRecord> history,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (keys.Count == 0) return new SupportPackageResult(false, "Pakete eklenecek içerik seçilmedi.", null, [], 0);
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
                notes.Add($"{text.TrimEnd('…')}: okunamadı – {ex.Message}");
                logger.Warning($"Destek paketi – {text} başarısız: {ex.Message}");
            }
        }

        await Step("report", "Sistem raporu hazırlanıyor…", async () =>
        {
            var doc = await reports.CollectAsync(ReportParts.All, progress, ct);
            files.Add(("sistem-raporu.txt", SystemReportService.ToText(doc)));
            files.Add(("sistem-raporu.html", SystemReportService.ToHtml(doc)));
            files.Add(("sistem-raporu.json", SystemReportService.ToJson(doc)));
        });
        await Step("logs", "Uygulama günlükleri ekleniyor…", () =>
        {
            foreach (var f in RecentLogs(5))
                files.Add(("gunlukler/" + Path.GetFileName(f), mask.Apply(ReadShared(f, 2 * 1024 * 1024))));
            return Task.CompletedTask;
        });
        await Step("drivers", "Sürücü listesi okunuyor…", async () =>
        {
            var d = await new DriverService(logger).ScanAsync(ct);
            var sb = new StringBuilder("Aygıt\tKategori\tÜretici\tSağlayıcı\tSürüm\tTarih\tİmza\tDurum\r\n");
            foreach (var x in d.Drivers)
                sb.Append($"{x.DeviceName}\t{x.Category}\t{x.Manufacturer}\t{x.Provider}\t{x.Version}\t{x.DateText}\t{x.SignedText}\t{x.StatusText}\r\n");
            if (d.Error is not null) sb.Append("Not: ").Append(d.Error).Append("\r\n");
            files.Add(("suruculer.tsv", mask.Apply(sb.ToString())));
        });
        await Step("events", "Olay kayıtları okunuyor…", async () =>
        {
            var q = await new EventLogService(logger).QueryAsync(["System", "Application"], [1, 2], TimeSpan.FromDays(7), ct);
            var sb = new StringBuilder();
            foreach (var e in q.Entries)
                sb.Append($"{e.TimeText} | {e.LogText} | {e.LevelText} | {e.Provider} | {e.Id}\r\n{e.Message}\r\n\r\n");
            foreach (var err in q.Errors) sb.Append("Not: ").Append(err).Append("\r\n");
            if (q.Truncated) sb.Append($"Not: en yeni {EventLogService.MaxEntries} kayıt alındı.\r\n");
            files.Add(("olay-kayitlari.txt", mask.Apply(sb.Length == 0 ? "Son 7 günde kritik / hata kaydı yok." : sb.ToString())));
        });
        await Step("network", "Ağ testleri çalıştırılıyor…", async () =>
        {
            var net = new NetworkDiagnosticsService(logger);
            var snap = await net.ReadAsync(ct);
            var tests = await net.RunTestsAsync(snap, progress, ct);
            var dns = await new DnsDiagnosticsService(logger).RunAsync(snap.Adapters, progress, ct);
            var sb = new StringBuilder();
            sb.Append("Windows bağlantı durumu: ").Append(snap.ConnectivityText ?? "—").Append("\r\n\r\n");
            foreach (var a in snap.Adapters)
                sb.Append($"{a.Name} ({a.TypeText}, {a.Description}) – {a.StatusText}, hız {a.SpeedText}\r\n  IPv4: {a.IPv4Text}\r\n  IPv6: {a.IPv6Text}\r\n" +
                          $"  Ağ geçidi: {a.GatewayText}\r\n  DNS: {a.DnsText}\r\n  DHCP: {a.DhcpText}\r\n  MAC: {a.MacText}\r\n");
            sb.Append("\r\nTESTLER\r\n");
            foreach (var t in tests) sb.Append($"{t.Title}: {t.StateText} – {t.Summary}\r\n  {t.Detail?.ReplaceLineEndings("\r\n  ")}\r\n");
            sb.Append("\r\nDNS SUNUCULARI\r\n");
            if (dns.Error is not null) sb.Append(dns.Error).Append("\r\n");
            foreach (var s in dns.Servers)
                sb.Append($"{s.ServerText} ({s.Family}, {s.Adapter}): {s.ResultText}, {s.TimeText}, DNSSEC: {s.DnssecText} {s.FailuresText}\r\n");
            files.Add(("ag-tanilama.txt", mask.Apply(sb.ToString())));
        });
        await Step("updates", "Güncelleme geçmişi okunuyor…", async () =>
        {
            var sb = new StringBuilder("WINDOWS UPDATE GEÇMİŞİ (son 50)\r\n");
            var ps = await PowerShellRunner.RunAsync(WuHistoryScript, TimeSpan.FromSeconds(90), ct, traceName: "Windows Update geçmişi");
            if (ps.Ok)
                foreach (var h in ps.Data!.Value.Arr("items"))
                    sb.Append($"{h.Str("date")} | {WuResult(h.Long("result"))} | {h.Str("title")}" + (h.Str("hresult") is { } hr && hr != "0x00000000" ? $" | {hr}" : "") + "\r\n");
            else sb.Append("Okunamadı: ").Append(ps.DescribeFailure("Windows Update geçmişi")).Append("\r\n");
            sb.Append("\r\nE-MRE CONTROL CENTER İŞLEM GEÇMİŞİ\r\n");
            foreach (var r in history)
                sb.Append($"{r.TimeText} | {r.Title} | {r.SummaryText} | süre {r.DurationText}" + (string.IsNullOrEmpty(r.ErrorText) ? "" : $" | hata: {r.ErrorText}") + "\r\n");
            files.Add(("guncelleme-gecmisi.txt", mask.Apply(sb.ToString())));
        });
        await Step("errors", "Hata bilgileri toplanıyor…", async () =>
        {
            var sb = new StringBuilder("GÜNLÜKLERDEKİ UYARI / HATA SATIRLARI\r\n");
            foreach (var f in RecentLogs(5))
                foreach (var line in ReadShared(f, 2 * 1024 * 1024).Split('\n'))
                    if (line.Contains("[ERROR]", StringComparison.Ordinal) || line.Contains("[WARNING]", StringComparison.Ordinal))
                        sb.Append(Path.GetFileName(f)).Append(": ").Append(line.TrimEnd('\r')).Append("\r\n");
            var c = await new CrashAnalysisService(logger).AnalyzeAsync(TimeSpan.FromDays(30), AdminPrivilegeManager.IsElevated, ct);
            sb.Append($"\r\nÇÖKME KAYITLARI (30 gün): {c.BugChecks} BugCheck, {c.Unexpected} beklenmedik kapanma, {c.DisplayResets} ekran sürücüsü sıfırlama, {c.Hardware} WHEA\r\n");
            foreach (var e in c.Events)
                sb.Append($"{e.TimeText} | {e.KindText} | {e.CodeText} | {e.ModuleText} | {e.RelationText}\r\n");
            if (c.DumpNote is not null) sb.Append("Not: ").Append(c.DumpNote).Append("\r\n");
            files.Add(("hata-bilgileri.txt", mask.Apply(sb.ToString())));
        });

        var readme = new StringBuilder("E-mre Control Center – Destek Paketi\r\n")
            .Append($"Oluşturulma: {DateTime.Now:dd.MM.yyyy HH:mm:ss} · sürüm {AppInfo.Version}\r\n")
            .Append("İçerik: ").Append(string.Join(", ", Items.Where(i => keys.Contains(i.Key)).Select(i => i.Title))).Append("\r\n")
            .Append("Gizlenen bilgiler: ").Append(PersonalDataMask.DescriptionText).Append("\r\n")
            .Append("Bu paket hiçbir yere otomatik gönderilmez; yalnızca sizin paylaştığınız kişiye ulaşır.\r\n");
        if (notes.Count > 0) readme.Append("\r\nEKSİK KALAN BÖLÜMLER\r\n").Append(string.Join("\r\n", notes)).Append("\r\n");
        files.Insert(0, ("BENIOKU.txt", mask.Apply(readme.ToString())));

        progress?.Report("ZIP dosyası yazılıyor…");
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
            logger.Error("Destek paketi yazılamadı: " + ex.Message);
            return new SupportPackageResult(false, "Paket yazılamadı: " + ex.Message, null, [], 0);
        }

        // Doğrulama: ZIP yeniden açılır, girdiler okunur.
        try
        {
            using var check = ZipFile.OpenRead(zipPath);
            var entries = check.Entries.Select(e => e.FullName).ToList();
            var size = new FileInfo(zipPath).Length;
            if (entries.Count != files.Count) throw new InvalidDataException($"{files.Count} dosya yazıldı, {entries.Count} okundu");
            logger.Info($"Destek paketi oluşturuldu: {zipPath} ({entries.Count} dosya, {Formats.Bytes(size)})" + (notes.Count > 0 ? "; eksik: " + string.Join("; ", notes) : "."));
            return new SupportPackageResult(true, notes.Count == 0 ? "Destek paketi oluşturuldu ve doğrulandı." : $"Paket oluşturuldu; {notes.Count} bölüm okunamadı (BENIOKU.txt'de yazılı).",
                zipPath, entries, size);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.Error("Destek paketi doğrulanamadı: " + ex.Message);
            return new SupportPackageResult(false, "Paket yazıldı ancak doğrulanamadı: " + ex.Message, zipPath, [], 0);
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
        2 => "Başarılı", 3 => "Hatalarla başarılı", 4 => "Başarısız", 5 => "İptal edildi", 1 => "Sürüyor", 0 => "Başlamadı", _ => "—"
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

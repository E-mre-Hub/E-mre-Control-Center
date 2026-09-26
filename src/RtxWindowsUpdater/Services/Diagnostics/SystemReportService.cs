using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Rapor bölümündeki tablo (başlıklar + satırlar).</summary>
public sealed record ReportTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Rapor bölümü: ad–değer satırları ve isteğe bağlı tablo. Note: bölüm okunamadıysa / eksikse gerçek neden.</summary>
public sealed record ReportSection(string Title, IReadOnlyList<KeyValuePair<string, string>> Items, ReportTable? Table = null, string? Note = null);

public sealed record ReportDocument(string Title, DateTime CreatedAt, string AppVersion, IReadOnlyList<ReportSection> Sections, IReadOnlyList<string> Masked);

/// <summary>Raporun içereceği bölümler (kullanıcı seçer).</summary>
[Flags]
public enum ReportParts
{
    None = 0,
    Windows = 1,
    Hardware = 2,
    Storage = 4,
    Network = 8,
    Drivers = 16,
    Security = 32,
    Services = 64,
    Startup = 128,
    Events = 256,
    Crashes = 512,
    Battery = 1024,
    All = Windows | Hardware | Storage | Network | Drivers | Security | Services | Startup | Events | Crashes | Battery
}

/// <summary>
/// Rapora girmeden önce kişisel verileri gizler: bilgisayar adı, kullanıcı adı, kullanıcı klasörü yolu, bu bilgisayarın IP adresleri ve
/// MAC adresleri. Ağ geçidi / DNS sunucusu (ağ tanısı için gerekli, kişisel değil) korunur.
/// </summary>
public sealed class PersonalDataMask
{
    private static readonly Regex MacPattern = new(@"\b[0-9A-Fa-f]{2}([-:])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);
    private readonly List<(string Value, string Replacement)> _replacements = [];

    /// <summary>Gizlenen / rapora hiç alınmayan bilgiler (ekranda, raporda ve BENIOKU'da aynı liste).</summary>
    public static IReadOnlyList<string> Descriptions { get; } =
    [
        "Bilgisayar adı", "Kullanıcı adı ve kullanıcı klasörü yolu", "Bu bilgisayarın IP adresleri", "Ağ kartı MAC adresleri",
        "Wi-Fi ağ adı, seri numaraları ve ürün anahtarları rapora hiç alınmaz"
    ];

    public static string DescriptionText => string.Join(", ", Descriptions);

    public PersonalDataMask()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) _replacements.Add((profile, "%USERPROFILE%"));
        foreach (var ip in OwnAddresses()) _replacements.Add((ip, "<bu bilgisayarın IP adresi>"));
        if (Environment.MachineName.Length > 1) _replacements.Add((Environment.MachineName, "<BİLGİSAYAR-ADI>"));
        if (Environment.UserName.Length > 1) _replacements.Add((Environment.UserName, "<KULLANICI>"));
        // Uzun değer önce: yol içindeki kullanıcı adı önce yol olarak değiştirilir.
        _replacements.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));
    }

    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (value, replacement) in _replacements)
            text = text.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);
        return MacPattern.Replace(text, "<MAC>");
    }

    private static IEnumerable<string> OwnAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (!IPAddress.IsLoopback(u.Address)) list.Add(u.Address.ToString().Split('%')[0]);
            }
        }
        catch (NetworkInformationException)
        {
            // adresler okunamadı: MAC deseni ve ad maskeleri yine uygulanır
        }
        return list.Where(a => a.Length >= 7).Distinct();
    }
}

/// <summary>
/// Sistem raporu: seçilen bölümler için mevcut tanılama servislerini çalıştırır (hiçbiri sistemi değiştirmez) ve sonuçları TXT / JSON /
/// HTML olarak üretir. Kişisel veriler <see cref="PersonalDataMask"/> ile gizlenir; okunamayan bölüm "okunamadı + neden" olarak yazılır.
/// </summary>
public sealed class SystemReportService(Logger logger, SystemInfoService systemInfo)
{
    public async Task<ReportDocument> CollectAsync(ReportParts parts, IProgress<string>? progress, CancellationToken ct,
        IReadOnlyList<NetTestResult>? lastNetworkTests = null, DnsReport? lastDns = null)
    {
        var mask = new PersonalDataMask();
        var sections = new List<ReportSection>();

        async Task Add(ReportParts part, string step, Func<Task<ReportSection>> build)
        {
            if (!parts.HasFlag(part)) return;
            ct.ThrowIfCancellationRequested();
            progress?.Report(step);
            try
            {
                sections.Add(await build());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sections.Add(new ReportSection(step.TrimEnd('…', '.'), [], null, "Bu bölüm okunamadı: " + ex.Message));
                logger.Warning($"Sistem raporu – {step} başarısız: {ex.Message}");
            }
        }

        await Add(ReportParts.Windows, "Windows bilgileri okunuyor…", async () =>
        {
            var rows = await new WindowsHealthService(logger).CheckAsync(ct);
            return new ReportSection("Windows", rows.Select(r => Kv(r.Title, $"{CheckStates.Text(r.State)} – {r.Summary}" + (r.Detail is null ? "" : $" ({r.Detail.ReplaceLineEndings("; ")})"))).ToList());
        });
        await Add(ReportParts.Hardware, "Donanım bilgileri okunuyor…", async () =>
        {
            var snap = await systemInfo.CollectAsync(ct);
            return new ReportSection("Donanım (CPU / GPU / RAM / Disk)", snap.Fields
                .Where(f => !f.Label.Contains("Bilgisayar adı", StringComparison.OrdinalIgnoreCase))
                .Select(f => Kv(f.Label, f.Value)).ToList());
        });
        await Add(ReportParts.Storage, "Depolama sağlığı okunuyor…", async () =>
        {
            var s = await new StorageHealthService(logger).ScanAsync(ct);
            var table = new ReportTable(["Disk", "Tür", "Boyut", "Durum", "Sıcaklık", "Aşınma", "Çalışma", "Bulgular"],
                s.Disks.Select(d => (IReadOnlyList<string>)[d.Name, d.TypeText, d.SizeText, d.StateText, d.TemperatureText, d.WearText, d.PowerOnText, d.FindingsText]).ToList());
            return new ReportSection("Depolama", s.Volumes.Select(v => Kv(v.Letter + " " + v.FileSystem, v.UsageText)).ToList(), table,
                s.Error ?? s.ReliabilityNote);
        });
        await Add(ReportParts.Network, "Ağ bilgileri okunuyor…", async () =>
        {
            var n = await new NetworkDiagnosticsService(logger).ReadAsync(ct);
            var table = new ReportTable(["Bağdaştırıcı", "Tür", "Durum", "Hız", "Ağ geçidi", "DNS", "DHCP"],
                n.Adapters.Select(a => (IReadOnlyList<string>)[a.Name + (a.IsPrimary ? " (birincil)" : ""), a.TypeText, a.StatusText, a.SpeedText, a.GatewayText, a.DnsText, a.DhcpText]).ToList());
            var items = new List<KeyValuePair<string, string>> { Kv("Windows bağlantı durumu", n.ConnectivityText ?? "—") };
            if (n.Wifi is { } w) items.Add(Kv("Wi-Fi", w.Error ?? $"sinyal {w.SignalText}, {w.PhyType ?? "—"}"));
            if (lastNetworkTests is { Count: > 0 })
                items.AddRange(lastNetworkTests.Select(t => Kv("Test: " + t.Title, $"{t.StateText} – {t.Summary}")));
            else items.Add(Kv("Bağlantı testleri", "Bu oturumda çalıştırılmadı"));
            if (lastDns is { Servers.Count: > 0 })
                items.AddRange(lastDns.Servers.Where(s => !s.Placeholder).Select(s => Kv("DNS " + s.ServerText, $"{s.ResultText}, {s.TimeText}, DNSSEC: {s.DnssecText}")));
            return new ReportSection("Ağ", items, table, n.Error);
        });
        await Add(ReportParts.Drivers, "Sürücüler okunuyor…", async () =>
        {
            var d = await new DriverService(logger).ScanAsync(ct);
            var shown = d.Drivers.Where(x => x.Important || x.State is CheckState.Error or CheckState.Warning).ToList();
            var table = new ReportTable(["Aygıt", "Kategori", "Sağlayıcı", "Sürüm", "Tarih", "Durum"],
                shown.Select(x => (IReadOnlyList<string>)[x.DeviceName, x.Category, x.Provider, x.Version, x.DateText, x.StatusText]).ToList());
            return new ReportSection("Sürücüler", [Kv("Toplam sürücü", d.Drivers.Count.ToString()), Kv("Sorunlu aygıt", d.ProblemCount.ToString())], table, d.Error);
        });
        await Add(ReportParts.Security, "Güvenlik durumu okunuyor…", async () =>
        {
            var s = await new SecurityStatusService(logger).ReadAsync(ct);
            return new ReportSection("Güvenlik", s.Checks.Select(c => Kv(c.Title, $"{CheckStates.Text(c.State)} – {c.Summary}")).ToList(),
                new ReportTable(["Ürün", "Tür", "Durum"], s.Products.Select(p => (IReadOnlyList<string>)[p.Name, p.Kind, p.StateText]).ToList()));
        });
        await Add(ReportParts.Services, "Windows hizmetleri okunuyor…", async () =>
        {
            var s = await new WindowsServiceManager(logger).ListAsync(ct);
            // Tüm hizmetler yerine: otomatik başlaması gerekip çalışmayanlar + Microsoft dışı çalışanlar (tanı için anlamlı olanlar).
            var autoStopped = s.Services.Where(x => x.StartMode == "Auto" && !x.IsRunning && x.DelayedStart != true).ToList();
            var thirdParty = s.Services.Where(x => x.IsRunning && x.Publisher is { } p && !p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)).ToList();
            var rows = autoStopped.Select(x => (IReadOnlyList<string>)[x.DisplayName, x.Name, x.StateText, x.StartModeText, "Otomatik ama çalışmıyor"])
                .Concat(thirdParty.Select(x => (IReadOnlyList<string>)[x.DisplayName, x.Name, x.StateText, x.StartModeText, x.PublisherText])).ToList();
            return new ReportSection("Hizmetler",
                [Kv("Toplam", s.Services.Count.ToString()), Kv("Çalışan", s.Services.Count(x => x.IsRunning).ToString()),
                 Kv("Otomatik ama çalışmayan (gecikmeli hariç)", autoStopped.Count.ToString())],
                new ReportTable(["Hizmet", "Ad", "Durum", "Başlangıç", "Not / yayıncı"], rows), s.Error);
        });
        await Add(ReportParts.Startup, "Başlangıç uygulamaları okunuyor…", async () =>
        {
            var s = await new StartupService(logger).ScanAsync(ct);
            return new ReportSection("Başlangıç uygulamaları", [Kv("Toplam", s.Entries.Count.ToString()), Kv("Etkin", s.Entries.Count(e => e.Enabled).ToString())],
                new ReportTable(["Ad", "Kaynak", "Durum", "Yayıncı", "Konum"],
                    s.Entries.Select(e => (IReadOnlyList<string>)[e.Name, e.SourceText, e.StateText, e.PublisherText, e.LocationText]).ToList()),
                s.Errors.Count > 0 ? string.Join(" ", s.Errors) : null);
        });
        await Add(ReportParts.Events, "Olay günlüğü okunuyor…", async () =>
        {
            var events = new EventLogService(logger);
            var sys = await events.CountAsync("System", TimeSpan.FromDays(7), ct);
            var app = await events.CountAsync("Application", TimeSpan.FromDays(7), ct);
            var last = await events.QueryAsync(["System"], [1, 2], TimeSpan.FromDays(7), ct);
            return new ReportSection("Olay günlüğü (son 7 gün)",
                [Kv("Sistem", sys.Ok ? $"{sys.Critical} kritik, {sys.Errors} hata, {sys.Warnings} uyarı" : "okunamadı: " + sys.Failure),
                 Kv("Uygulama", app.Ok ? $"{app.Critical} kritik, {app.Errors} hata, {app.Warnings} uyarı" : "okunamadı: " + app.Failure)],
                new ReportTable(["Tarih", "Düzey", "Kaynak", "Olay", "İleti"],
                    last.Entries.Take(30).Select(e => (IReadOnlyList<string>)[e.TimeText, e.LevelText, e.Provider, e.Id.ToString(), e.ShortMessage]).ToList()),
                last.Errors.Count > 0 ? string.Join(" ", last.Errors) : null);
        });
        await Add(ReportParts.Crashes, "Çökme kayıtları okunuyor…", async () =>
        {
            var c = await new CrashAnalysisService(logger).AnalyzeAsync(TimeSpan.FromDays(30), AdminPrivilegeManager.IsElevated, ct);
            return new ReportSection("Çökme / beklenmedik kapanma (son 30 gün)",
                [Kv("Mavi ekran (BugCheck)", c.BugChecks.ToString()), Kv("Beklenmedik kapanma kaydı", c.Unexpected.ToString()),
                 Kv("Ekran sürücüsü sıfırlama", c.DisplayResets.ToString()), Kv("Donanım hatası (WHEA)", c.Hardware.ToString()),
                 Kv("Minidump", c.Dumps.Count > 0 ? c.Dumps.Count.ToString() : c.DumpNote ?? "yok")],
                new ReportTable(["Tarih", "Tür", "Kod", "Modül", "İlişkili olabilir"],
                    c.Events.Take(30).Select(e => (IReadOnlyList<string>)[e.TimeText, e.KindText, e.CodeText, e.ModuleText, e.RelationText]).ToList()),
                c.EventError);
        });
        await Add(ReportParts.Battery, "Batarya bilgisi okunuyor…", async () =>
        {
            var b = await new BatteryService(logger).ReadAsync(ct);
            if (!b.HasBattery) return new ReportSection("Batarya", [Kv("Durum", b.Error ?? BatteryService.NoBatteryText)]);
            return new ReportSection("Batarya", b.Batteries.SelectMany(x => new[]
            {
                Kv(x.Name + " – şarj", $"{x.ChargeText}, {x.StatusText}"), Kv(x.Name + " – sağlık", x.HealthText),
                Kv(x.Name + " – kapasite", $"tam {x.FullText} / tasarım {x.DesignText}"), Kv(x.Name + " – döngü", x.CycleText)
            }).Prepend(Kv("Güç", b.AcText ?? "—")).ToList(), null, b.Note);
        });

        // Tüm metinler maskeden geçer (kişisel veri rapora yazılmaz).
        var masked = sections.Select(s => new ReportSection(mask.Apply(s.Title),
            s.Items.Select(i => Kv(mask.Apply(i.Key), mask.Apply(i.Value))).ToList(),
            s.Table is null ? null : new ReportTable(s.Table.Headers, s.Table.Rows.Select(r => (IReadOnlyList<string>)r.Select(mask.Apply).ToList()).ToList()),
            s.Note is null ? null : mask.Apply(s.Note))).ToList();
        logger.Info($"Sistem raporu hazırlandı: {masked.Count} bölüm ({string.Join(", ", masked.Select(s => s.Title))}).");
        return new ReportDocument("E-mre Control Center – Sistem Raporu", DateTime.Now, AppInfo.Version, masked, PersonalDataMask.Descriptions);
    }

    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    // ------------------------------------------------------------------ biçimler

    public static string ToText(ReportDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(doc.Title);
        sb.AppendLine($"Oluşturulma: {doc.CreatedAt:dd.MM.yyyy HH:mm:ss} · Uygulama sürümü {doc.AppVersion}");
        sb.AppendLine("Gizlenen bilgiler: " + string.Join(", ", doc.Masked));
        foreach (var s in doc.Sections)
        {
            sb.AppendLine().AppendLine(new string('=', 72)).AppendLine(s.Title.ToUpper(new System.Globalization.CultureInfo("tr-TR"))).AppendLine(new string('=', 72));
            if (s.Note is not null) sb.AppendLine("Not: " + s.Note);
            var width = s.Items.Count == 0 ? 0 : Math.Min(40, s.Items.Max(i => i.Key.Length));
            foreach (var i in s.Items) sb.AppendLine($"{i.Key.PadRight(width)} : {i.Value}");
            if (s.Table is { Rows.Count: > 0 } t)
            {
                sb.AppendLine();
                sb.AppendLine(string.Join(" | ", t.Headers));
                foreach (var r in t.Rows) sb.AppendLine(string.Join(" | ", r.Select(c => c.ReplaceLineEndings(" "))));
            }
        }
        return sb.ToString();
    }

    public static string ToJson(ReportDocument doc) => JsonSerializer.Serialize(new
    {
        title = doc.Title,
        createdAt = doc.CreatedAt.ToString("s"),
        appVersion = doc.AppVersion,
        masked = doc.Masked,
        sections = doc.Sections.Select(s => new
        {
            title = s.Title,
            note = s.Note,
            items = s.Items.Select(i => new { name = i.Key, value = i.Value }),
            table = s.Table is null ? null : new { headers = s.Table.Headers, rows = s.Table.Rows }
        })
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static string ToHtml(ReportDocument doc)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\"><title>").Append(E(doc.Title)).Append("</title><style>")
          .Append("body{background:#020307;color:#E6EDF7;font-family:'Segoe UI',sans-serif;margin:24px;max-width:1200px}")
          .Append("h1{color:#4CCBFF;font-weight:600}h2{color:#4CCBFF;font-size:18px;border-bottom:1px solid #1B2A44;padding-bottom:6px;margin-top:28px}")
          .Append("table{border-collapse:collapse;width:100%;margin-top:8px;font-size:13px}td,th{border:1px solid #1B2A44;padding:6px 8px;text-align:left;vertical-align:top;overflow-wrap:anywhere}")
          .Append("th{background:#0A1426;color:#A7B5CF}.kv td:first-child{color:#A7B5CF;width:32%}.note{color:#F2B84B}.meta{color:#7887A5}")
          .Append("</style></head><body>");
        sb.Append("<h1>").Append(E(doc.Title)).Append("</h1><p class=\"meta\">Oluşturulma: ").Append(E(doc.CreatedAt.ToString("dd.MM.yyyy HH:mm:ss")))
          .Append(" · Uygulama sürümü ").Append(E(doc.AppVersion)).Append("<br>Gizlenen bilgiler: ").Append(E(string.Join(", ", doc.Masked))).Append("</p>");
        foreach (var s in doc.Sections)
        {
            sb.Append("<h2>").Append(E(s.Title)).Append("</h2>");
            if (s.Note is not null) sb.Append("<p class=\"note\">").Append(E(s.Note)).Append("</p>");
            if (s.Items.Count > 0)
            {
                sb.Append("<table class=\"kv\">");
                foreach (var i in s.Items) sb.Append("<tr><td>").Append(E(i.Key)).Append("</td><td>").Append(E(i.Value)).Append("</td></tr>");
                sb.Append("</table>");
            }
            if (s.Table is { Rows.Count: > 0 } t)
            {
                sb.Append("<table><tr>");
                foreach (var h in t.Headers) sb.Append("<th>").Append(E(h)).Append("</th>");
                sb.Append("</tr>");
                foreach (var r in t.Rows)
                {
                    sb.Append("<tr>");
                    foreach (var c in r) sb.Append("<td>").Append(E(c)).Append("</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
            }
        }
        return sb.Append("</body></html>").ToString();
    }
}

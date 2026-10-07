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
        L.T("Bilgisayar adı", "Computer name"), L.T("Kullanıcı adı ve kullanıcı klasörü yolu", "User name and user folder path"), L.T("Bu bilgisayarın IP adresleri", "This computer's IP addresses"), L.T("Ağ kartı MAC adresleri", "Network adapter MAC addresses"),
        L.T("Wi-Fi ağ adı, seri numaraları ve ürün anahtarları rapora hiç alınmaz", "Wi-Fi network name, serial numbers and product keys are never included in the report")
    ];

    public static string DescriptionText => string.Join(", ", Descriptions);

    public PersonalDataMask()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) _replacements.Add((profile, "%USERPROFILE%"));
        foreach (var ip in OwnAddresses()) _replacements.Add((ip, L.T("<bu bilgisayarın IP adresi>", "<this computer's IP address>")));
        if (Environment.MachineName.Length > 1) _replacements.Add((Environment.MachineName, L.T("<BİLGİSAYAR-ADI>", "<COMPUTER-NAME>")));
        if (Environment.UserName.Length > 1) _replacements.Add((Environment.UserName, L.T("<KULLANICI>", "<USER>")));
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
                sections.Add(new ReportSection(step.TrimEnd('…', '.'), [], null, L.T("Bu bölüm okunamadı: ", "This section could not be read: ") + ex.Message));
                logger.Warning(L.T($"Sistem raporu – {step} başarısız: {ex.Message}", $"System report – {step} failed: {ex.Message}"));
            }
        }

        await Add(ReportParts.Windows, L.T("Windows bilgileri okunuyor…", "Reading Windows information…"), async () =>
        {
            var rows = await new WindowsHealthService(logger).CheckAsync(ct);
            return new ReportSection("Windows", rows.Select(r => Kv(r.Title, $"{CheckStates.Text(r.State)} – {r.Summary}" + (r.Detail is null ? "" : $" ({r.Detail.ReplaceLineEndings("; ")})"))).ToList());
        });
        await Add(ReportParts.Hardware, L.T("Donanım bilgileri okunuyor…", "Reading hardware information…"), async () =>
        {
            var snap = await systemInfo.CollectAsync(ct);
            return new ReportSection(L.T("Donanım (CPU / GPU / RAM / Disk)", "Hardware (CPU / GPU / RAM / Disk)"), snap.Fields
                .Where(f => !f.Label.Contains(L.T("Bilgisayar adı", "Computer name"), StringComparison.OrdinalIgnoreCase))
                .Select(f => Kv(f.Label, f.Value)).ToList());
        });
        await Add(ReportParts.Storage, L.T("Depolama sağlığı okunuyor…", "Reading storage health…"), async () =>
        {
            var s = await new StorageHealthService(logger).ScanAsync(ct);
            var table = new ReportTable(["Disk", L.T("Tür", "Type"), L.T("Boyut", "Size"), L.T("Durum", "Status"), L.T("Sıcaklık", "Temperature"), L.T("Aşınma", "Wear"), L.T("Çalışma", "Power-on"), L.T("Bulgular", "Findings")],
                s.Disks.Select(d => (IReadOnlyList<string>)[d.Name, d.TypeText, d.SizeText, d.StateText, d.TemperatureText, d.WearText, d.PowerOnText, d.FindingsText]).ToList());
            return new ReportSection(L.T("Depolama", "Storage"), s.Volumes.Select(v => Kv(v.Letter + " " + v.FileSystem, v.UsageText)).ToList(), table,
                s.Error ?? s.ReliabilityNote);
        });
        await Add(ReportParts.Network, L.T("Ağ bilgileri okunuyor…", "Reading network information…"), async () =>
        {
            var n = await new NetworkDiagnosticsService(logger).ReadAsync(ct);
            var table = new ReportTable([L.T("Bağdaştırıcı", "Adapter"), L.T("Tür", "Type"), L.T("Durum", "Status"), L.T("Hız", "Speed"), L.T("Ağ geçidi", "Gateway"), "DNS", "DHCP"],
                n.Adapters.Select(a => (IReadOnlyList<string>)[a.Name + (a.IsPrimary ? L.T(" (birincil)", " (primary)") : ""), a.TypeText, a.StatusText, a.SpeedText, a.GatewayText, a.DnsText, a.DhcpText]).ToList());
            var items = new List<KeyValuePair<string, string>> { Kv(L.T("Windows bağlantı durumu", "Windows connection status"), n.ConnectivityText ?? "—") };
            if (n.Wifi is { } w) items.Add(Kv("Wi-Fi", w.Error ?? L.T($"sinyal {w.SignalText}, {w.PhyType ?? "—"}", $"signal {w.SignalText}, {w.PhyType ?? "—"}")));
            if (lastNetworkTests is { Count: > 0 })
                items.AddRange(lastNetworkTests.Select(t => Kv(L.T("Test: ", "Test: ") + t.Title, $"{t.StateText} – {t.Summary}")));
            else items.Add(Kv(L.T("Bağlantı testleri", "Connection tests"), L.T("Bu oturumda çalıştırılmadı", "Not run in this session")));
            if (lastDns is { Servers.Count: > 0 })
                items.AddRange(lastDns.Servers.Where(s => !s.Placeholder).Select(s => Kv("DNS " + s.ServerText, L.T($"{s.ResultText}, {s.TimeText}, DNSSEC: {s.DnssecText}", $"{s.ResultText}, {s.TimeText}, DNSSEC: {s.DnssecText}"))));
            return new ReportSection(L.T("Ağ", "Network"), items, table, n.Error);
        });
        await Add(ReportParts.Drivers, L.T("Sürücüler okunuyor…", "Reading drivers…"), async () =>
        {
            var d = await new DriverService(logger).ScanAsync(ct);
            var shown = d.Drivers.Where(x => x.Important || x.State is CheckState.Error or CheckState.Warning).ToList();
            var table = new ReportTable([L.T("Aygıt", "Device"), L.T("Kategori", "Category"), L.T("Sağlayıcı", "Provider"), L.T("Sürüm", "Version"), L.T("Tarih", "Date"), L.T("Durum", "Status")],
                shown.Select(x => (IReadOnlyList<string>)[x.DeviceName, x.Category, x.Provider, x.Version, x.DateText, x.StatusText]).ToList());
            return new ReportSection(L.T("Sürücüler", "Drivers"), [Kv(L.T("Toplam sürücü", "Total drivers"), d.Drivers.Count.ToString()), Kv(L.T("Sorunlu aygıt", "Devices with problems"), d.ProblemCount.ToString())], table, d.Error);
        });
        await Add(ReportParts.Security, L.T("Güvenlik durumu okunuyor…", "Reading security status…"), async () =>
        {
            var s = await new SecurityStatusService(logger).ReadAsync(ct);
            return new ReportSection(L.T("Güvenlik", "Security"), s.Checks.Select(c => Kv(c.Title, $"{CheckStates.Text(c.State)} – {c.Summary}")).ToList(),
                new ReportTable([L.T("Ürün", "Product"), L.T("Tür", "Type"), L.T("Durum", "Status")], s.Products.Select(p => (IReadOnlyList<string>)[p.Name, p.Kind, p.StateText]).ToList()));
        });
        await Add(ReportParts.Services, L.T("Windows hizmetleri okunuyor…", "Reading Windows services…"), async () =>
        {
            var s = await new WindowsServiceManager(logger).ListAsync(ct);
            // Tüm hizmetler yerine: otomatik başlaması gerekip çalışmayanlar + Microsoft dışı çalışanlar (tanı için anlamlı olanlar).
            var autoStopped = s.Services.Where(x => x.StartMode == "Auto" && !x.IsRunning && x.DelayedStart != true).ToList();
            var thirdParty = s.Services.Where(x => x.IsRunning && x.Publisher is { } p && !p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)).ToList();
            var rows = autoStopped.Select(x => (IReadOnlyList<string>)[x.DisplayName, x.Name, x.StateText, x.StartModeText, L.T("Otomatik ama çalışmıyor", "Automatic but not running")])
                .Concat(thirdParty.Select(x => (IReadOnlyList<string>)[x.DisplayName, x.Name, x.StateText, x.StartModeText, x.PublisherText])).ToList();
            return new ReportSection(L.T("Hizmetler", "Services"),
                [Kv(L.T("Toplam", "Total"), s.Services.Count.ToString()), Kv(L.T("Çalışan", "Running"), s.Services.Count(x => x.IsRunning).ToString()),
                 Kv(L.T("Otomatik ama çalışmayan (gecikmeli hariç)", "Automatic but not running (excluding delayed)"), autoStopped.Count.ToString())],
                new ReportTable([L.T("Hizmet", "Service"), "Ad", L.T("Durum", "Status"), L.T("Başlangıç", "Startup"), L.T("Not / yayıncı", "Note / publisher")], rows), s.Error);
        });
        await Add(ReportParts.Startup, L.T("Başlangıç uygulamaları okunuyor…", "Reading startup apps…"), async () =>
        {
            var s = await new StartupService(logger).ScanAsync(ct);
            return new ReportSection(L.T("Başlangıç uygulamaları", "Startup apps"), [Kv(L.T("Toplam", "Total"), s.Entries.Count.ToString()), Kv(L.T("Etkin", "Enabled"), s.Entries.Count(e => e.Enabled).ToString())],
                new ReportTable(["Ad", L.T("Kaynak", "Source"), L.T("Durum", "Status"), L.T("Yayıncı", "Publisher"), L.T("Konum", "Location")],
                    s.Entries.Select(e => (IReadOnlyList<string>)[e.Name, e.SourceText, e.StateText, e.PublisherText, e.LocationText]).ToList()),
                s.Errors.Count > 0 ? string.Join(" ", s.Errors) : null);
        });
        await Add(ReportParts.Events, L.T("Olay günlüğü okunuyor…", "Reading the event log…"), async () =>
        {
            var events = new EventLogService(logger);
            var sys = await events.CountAsync("System", TimeSpan.FromDays(7), ct);
            var app = await events.CountAsync("Application", TimeSpan.FromDays(7), ct);
            var last = await events.QueryAsync(["System"], [1, 2], TimeSpan.FromDays(7), ct);
            return new ReportSection(L.T("Olay günlüğü (son 7 gün)", "Event log (last 7 days)"),
                [Kv(L.T("Sistem", "System"), sys.Ok ? L.T($"{sys.Critical} kritik, {sys.Errors} hata, {sys.Warnings} uyarı", $"{sys.Critical} critical, {sys.Errors} error(s), {sys.Warnings} warning(s)") : L.T("okunamadı: ", "unreadable: ") + sys.Failure),
                 Kv(L.T("Uygulama", "Application"), app.Ok ? L.T($"{app.Critical} kritik, {app.Errors} hata, {app.Warnings} uyarı", $"{app.Critical} critical, {app.Errors} error(s), {app.Warnings} warning(s)") : L.T("okunamadı: ", "unreadable: ") + app.Failure)],
                new ReportTable([L.T("Tarih", "Date"), L.T("Düzey", "Level"), L.T("Kaynak", "Source"), L.T("Olay", "Event"), L.T("İleti", "Message")],
                    last.Entries.Take(30).Select(e => (IReadOnlyList<string>)[e.TimeText, e.LevelText, e.Provider, e.Id.ToString(), e.ShortMessage]).ToList()),
                last.Errors.Count > 0 ? string.Join(" ", last.Errors) : null);
        });
        await Add(ReportParts.Crashes, L.T("Çökme kayıtları okunuyor…", "Reading crash records…"), async () =>
        {
            var c = await new CrashAnalysisService(logger).AnalyzeAsync(TimeSpan.FromDays(30), AdminPrivilegeManager.IsElevated, ct);
            return new ReportSection(L.T("Çökme / beklenmedik kapanma (son 30 gün)", "Crashes / unexpected shutdowns (last 30 days)"),
                [Kv(L.T("Mavi ekran (BugCheck)", "Blue screen (BugCheck)"), c.BugChecks.ToString()), Kv(L.T("Beklenmedik kapanma kaydı", "Unexpected shutdown record"), c.Unexpected.ToString()),
                 Kv(L.T("Ekran sürücüsü sıfırlama", "Display driver reset"), c.DisplayResets.ToString()), Kv(L.T("Donanım hatası (WHEA)", "Hardware error (WHEA)"), c.Hardware.ToString()),
                 Kv("Minidump", c.Dumps.Count > 0 ? c.Dumps.Count.ToString() : c.DumpNote ?? L.T("yok", "none"))],
                new ReportTable([L.T("Tarih", "Date"), L.T("Tür", "Type"), L.T("Kod", "Code"), L.T("Modül", "Module"), L.T("İlişkili olabilir", "May be related")],
                    c.Events.Take(30).Select(e => (IReadOnlyList<string>)[e.TimeText, e.KindText, e.CodeText, e.ModuleText, e.RelationText]).ToList()),
                c.EventError);
        });
        await Add(ReportParts.Battery, L.T("Batarya bilgisi okunuyor…", "Reading battery information…"), async () =>
        {
            var b = await new BatteryService(logger).ReadAsync(ct);
            if (!b.HasBattery) return new ReportSection(L.T("Batarya", "Battery"), [Kv(L.T("Durum", "Status"), b.Error ?? BatteryService.NoBatteryText)]);
            return new ReportSection(L.T("Batarya", "Battery"), b.Batteries.SelectMany(x => new[]
            {
                Kv(x.Name + L.T(" – şarj", " – charge"), $"{x.ChargeText}, {x.StatusText}"), Kv(x.Name + L.T(" – sağlık", " – health"), x.HealthText),
                Kv(x.Name + L.T(" – kapasite", " – capacity"), L.T($"tam {x.FullText} / tasarım {x.DesignText}", $"full {x.FullText} / design {x.DesignText}")), Kv(x.Name + L.T(" – döngü", " – cycles"), x.CycleText)
            }).Prepend(Kv(L.T("Güç", "Power"), b.AcText ?? "—")).ToList(), null, b.Note);
        });

        // Tüm metinler maskeden geçer (kişisel veri rapora yazılmaz).
        var masked = sections.Select(s => new ReportSection(mask.Apply(s.Title),
            s.Items.Select(i => Kv(mask.Apply(i.Key), mask.Apply(i.Value))).ToList(),
            s.Table is null ? null : new ReportTable(s.Table.Headers, s.Table.Rows.Select(r => (IReadOnlyList<string>)r.Select(mask.Apply).ToList()).ToList()),
            s.Note is null ? null : mask.Apply(s.Note))).ToList();
        logger.Info(L.T($"Sistem raporu hazırlandı: {masked.Count} bölüm ({string.Join(", ", masked.Select(s => s.Title))}).", $"System report prepared: {masked.Count} sections ({string.Join(", ", masked.Select(s => s.Title))})."));
        return new ReportDocument(L.T("E-mre Control Center – Sistem Raporu", "E-mre Control Center – System Report"), DateTime.Now, AppInfo.Version, masked, PersonalDataMask.Descriptions);
    }

    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    // ------------------------------------------------------------------ biçimler

    public static string ToText(ReportDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(doc.Title);
        sb.AppendLine(L.T($"Oluşturulma: {doc.CreatedAt:dd.MM.yyyy HH:mm:ss} · Uygulama sürümü {doc.AppVersion}", $"Created: {doc.CreatedAt:yyyy-MM-dd HH:mm:ss} · App version {doc.AppVersion}"));
        sb.AppendLine(L.T("Gizlenen bilgiler: ", "Masked information: ") + string.Join(", ", doc.Masked));
        foreach (var s in doc.Sections)
        {
            sb.AppendLine().AppendLine(new string('=', 72)).AppendLine(L.Upper(s.Title)).AppendLine(new string('=', 72));
            if (s.Note is not null) sb.AppendLine(L.T("Not: ", "Note: ") + s.Note);
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
        sb.Append(L.T("<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\"><title>", "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")).Append(E(doc.Title)).Append("</title><style>")
          .Append("body{background:#020307;color:#E6EDF7;font-family:'Segoe UI',sans-serif;margin:24px;max-width:1200px}")
          .Append("h1{color:#4CCBFF;font-weight:600}h2{color:#4CCBFF;font-size:18px;border-bottom:1px solid #1B2A44;padding-bottom:6px;margin-top:28px}")
          .Append("table{border-collapse:collapse;width:100%;margin-top:8px;font-size:13px}td,th{border:1px solid #1B2A44;padding:6px 8px;text-align:left;vertical-align:top;overflow-wrap:anywhere}")
          .Append("th{background:#0A1426;color:#A7B5CF}.kv td:first-child{color:#A7B5CF;width:32%}.note{color:#F2B84B}.meta{color:#7887A5}")
          .Append("</style></head><body>");
        sb.Append("<h1>").Append(E(doc.Title)).Append(L.T("</h1><p class=\"meta\">Oluşturulma: ", "</h1><p class=\"meta\">Created: ")).Append(E(doc.CreatedAt.ToString(L.T("dd.MM.yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss"))))
          .Append(L.T(" · Uygulama sürümü ", " · App version ")).Append(E(doc.AppVersion)).Append(L.T("<br>Gizlenen bilgiler: ", "<br>Masked information: ")).Append(E(string.Join(", ", doc.Masked))).Append("</p>");
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

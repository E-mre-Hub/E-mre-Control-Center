using System.Diagnostics;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Tek Tıkla Tanıla'nın bir adımı; sonuç gerçek kontrolün kendisidir (yüzde yok, yalnızca durum).</summary>
public sealed record DiagnosticStep(int Index, string Key, string Title, string RunningText, string TargetCategory, string TargetSection);

public sealed record DiagnosticStepResult(DiagnosticStep Step, CheckResult Result, TimeSpan Duration);

/// <summary>
/// Tanılama orkestratörü: Tek Tıkla Tanıla (10 adım), Sistem Sağlığı taraması ve "Tümünü Kontrol Et"in güvenli tanılama kısmı. Adımlar
/// sırayla çalışır ve yalnızca OKUR: dosya silme, sürücü kurma, uygulama kaldırma, hizmet durdurma, kayıt değişikliği, DNS değişikliği
/// yapılmaz. Bir adım hata verirse sonraki adımlar sürer; hata "Kontrol edilemedi" + gerçek neden olarak döner.
/// </summary>
public sealed class DiagnosticOrchestrator(Logger logger)
{
    public static IReadOnlyList<DiagnosticStep> Steps { get; } =
    [
        new(1, "requirements", L.T("Sistem gereksinimleri", "System requirements"), L.T("Sistem gereksinimleri kontrol ediliyor…", "Checking system requirements…"), Nav.Settings, Nav.Requirements),
        new(2, "windows", L.T("Windows sağlığı", "Windows health"), L.T("Windows sistem durumu kontrol ediliyor…", "Checking Windows system status…"), Nav.Health, Nav.SystemHealth),
        new(3, "drivers", L.T("Sürücü durumu", "Driver status"), L.T("Sürücüler okunuyor…", "Reading drivers…"), Nav.Update, Nav.Drivers),
        new(4, "network", L.T("Ağ bağlantısı", "Network connection"), L.T("Ağ bağlantısı test ediliyor…", "Testing the network connection…"), Nav.SpeedTest, Nav.Network),
        new(5, "dns", "DNS", L.T("DNS sunucuları sorgulanıyor…", "Querying DNS servers…"), Nav.SpeedTest, Nav.Dns),
        new(6, "storage", L.T("Depolama sağlığı", "Storage health"), L.T("Depolama sağlık bilgileri okunuyor…", "Reading storage health information…"), Nav.Health, Nav.StorageHealth),
        new(7, "security", L.T("Güvenlik durumu", "Security status"), L.T("Güvenlik durumu okunuyor…", "Reading security status…"), Nav.SystemTools, Nav.Security),
        new(8, "events", L.T("Olay günlüğü", "Event log"), L.T("Olay günlüğü taranıyor…", "Scanning the event log…"), Nav.Health, Nav.EventLog),
        new(9, "crash", L.T("Çökme / mavi ekran geçmişi", "Crash / blue screen history"), L.T("Çökme kayıtları taranıyor…", "Scanning crash records…"), Nav.Health, Nav.Crash),
        new(10, "performance", L.T("Performans anlık görüntüsü", "Performance snapshot"), L.T("Performans ölçülüyor…", "Measuring performance…"), Nav.Device, Nav.Performance)
    ];

    /// <summary>"Tümünü Kontrol Et"e eklenen hızlı ve güvenli tanılama adımları (DISM / SFC zaten kart kontrollerinde).</summary>
    public static IReadOnlyList<string> QuickKeys { get; } = ["windows", "drivers", "network", "dns", "storage", "security"];

    /// <summary>Son çalıştırmanın ayrıntı verileri (ilgili ekranlar yeniden okumadan gösterebilsin diye).</summary>
    public NetworkSnapshot? LastNetwork { get; private set; }
    public IReadOnlyList<NetTestResult>? LastNetworkTests { get; private set; }
    public DnsReport? LastDns { get; private set; }

    /// <summary>
    /// Adımları sırayla çalıştırır. <paramref name="onStep"/>: adım başlarken (Checking) ve bitince (gerçek sonuç) çağrılır.
    /// İptal edilirse kalan adımlar çalıştırılmaz (sonuçları NotChecked kalır).
    /// </summary>
    public async Task<IReadOnlyList<DiagnosticStepResult>> RunAsync(IReadOnlyCollection<string>? keys, Action<DiagnosticStep, CheckResult>? onStep,
        CancellationToken ct)
    {
        var results = new List<DiagnosticStepResult>();
        foreach (var step in Steps.Where(s => keys is null || keys.Contains(s.Key)))
        {
            ct.ThrowIfCancellationRequested();
            onStep?.Invoke(step, new CheckResult(step.Title, CheckState.Checking, step.RunningText));
            var sw = Stopwatch.StartNew();
            CheckResult result;
            try
            {
                result = await RunStepAsync(step, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new CheckResult(step.Title, CheckState.Unknown, L.T("Kontrol edilemedi.", "Could not be checked."), ex.Message);
                logger.Warning(L.T($"Tanılama adımı başarısız ({step.Title}): {ex.Message}", $"Diagnosis step failed ({step.Title}): {ex.Message}"));
            }
            result = result with { Title = step.Title, TargetCategory = step.TargetCategory, TargetSection = step.TargetSection };
            results.Add(new DiagnosticStepResult(step, result, sw.Elapsed));
            onStep?.Invoke(step, result);
            logger.Info(L.T($"Tanılama {step.Index}/{Steps.Count} – {step.Title}: {CheckStates.Text(result.State)} – {result.Summary} ({sw.Elapsed.TotalSeconds:0.0} sn)", $"Diagnosis {step.Index}/{Steps.Count} – {step.Title}: {CheckStates.Text(result.State)} – {result.Summary} ({sw.Elapsed.TotalSeconds:0.0} sec)"));
        }
        return results;
    }

    private async Task<CheckResult> RunStepAsync(DiagnosticStep step, CancellationToken ct)
    {
        switch (step.Key)
        {
            case "requirements":
            {
                var r = await new SystemRequirementsChecker(logger).CheckAsync();
                if (r.Error is not null) return new CheckResult("", CheckState.Unknown, L.T("Gereksinimler okunamadı.", "Requirements could not be read."), r.Error);
                var parts = new List<string>
                {
                    r.IsWindows11 ? "Windows 11" : L.T("Windows 11 değil", "not Windows 11"),
                    r.HasRtxGpu ? L.T("NVIDIA RTX var", "NVIDIA RTX present") : L.T("NVIDIA RTX yok (kartsız mod)", "no NVIDIA RTX (without-RTX mode)"),
                    r.IsAdministrator ? L.T("yönetici", "administrator") : L.T("yönetici değil", "not administrator")
                };
                var state = !r.IsWindows11 ? CheckState.Error : !r.IsAdministrator ? CheckState.Warning : CheckState.Healthy;
                return new CheckResult("", state, string.Join(" · ", parts),
                    $"{r.OsDescription}\n{r.GpuDescription}" + (r.IsAdministrator ? "" : L.T("\nYönetici yetkisi olmadan bazı kontroller (sıcaklık, minidump, DISM) yapılamaz.", "\nWithout administrator rights some checks (temperature, minidump, DISM) cannot be run.")));
            }
            case "windows":
            {
                var rows = await new WindowsHealthService(logger).CheckAsync(ct);
                return Summarize(rows, L.T("Windows sistem durumu sağlıklı", "Windows system status is healthy"));
            }
            case "drivers":
            {
                var d = await new DriverService(logger).ScanAsync(ct);
                if (d.Error is not null && d.Drivers.Count == 0) return new CheckResult("", CheckState.Unknown, L.T("Sürücü bilgisi alınamadı.", "Driver information unavailable."), d.Error);
                var problems = d.Drivers.Where(x => x.State is CheckState.Error or CheckState.Warning).ToList();
                return problems.Count == 0
                    ? new CheckResult("", CheckState.Healthy, L.T($"{d.Drivers.Count} sürücü · sorunlu aygıt yok", $"{d.Drivers.Count} drivers · no devices with problems"))
                    : new CheckResult("", problems.Any(p => p.State == CheckState.Error) ? CheckState.Error : CheckState.Warning,
                        L.T($"{problems.Count} aygıtta sorun bildirildi", $"Problems reported on {problems.Count} device(s)"), string.Join("\n", problems.Take(10).Select(p => $"{p.DeviceName}: {p.StatusText}")));
            }
            case "network":
            {
                var net = new NetworkDiagnosticsService(logger);
                var snap = await net.ReadAsync(ct);
                LastNetwork = snap;
                if (snap.Error is not null) return new CheckResult("", CheckState.Unknown, L.T("Ağ bilgisi alınamadı.", "Network information unavailable."), snap.Error);
                var tests = await net.RunTestsAsync(snap, null, ct);
                LastNetworkTests = tests;
                var worst = CheckStates.Worst(tests.Select(t => t.State));
                var inet = tests.FirstOrDefault(t => t.LatencyMs is not null && t.Title.StartsWith(L.T("İnternet", "Internet"), StringComparison.Ordinal));
                return new CheckResult("", worst,
                    snap.Primary is null ? L.T("Etkin ağ bağlantısı yok", "No active network connection")
                    : worst == CheckState.Healthy ? L.T($"Bağlı ({snap.Primary.TypeText})", $"Connected ({snap.Primary.TypeText})") + (inet is null ? "" : $" · {inet.Summary}")
                    : string.Join(" · ", tests.Where(t => t.State is CheckState.Warning or CheckState.Error).Select(t => $"{t.Title}: {t.Summary}")),
                    string.Join("\n", tests.Select(t => $"{t.Title}: {t.StateText} – {t.Summary}")));
            }
            case "dns":
            {
                var adapters = LastNetwork?.Adapters ?? (await new NetworkDiagnosticsService(logger).ReadAsync(ct)).Adapters;
                var dns = await new DnsDiagnosticsService(logger).RunAsync(adapters, null, ct);
                LastDns = dns;
                if (dns.Error is not null) return new CheckResult("", CheckState.Skipped, dns.Error);
                var tested = dns.Servers.Where(s => !s.Placeholder).ToList();
                return new CheckResult("", dns.Overall,
                    string.Join(" · ", tested.Select(s => $"{s.ServerText}: {s.ResultText}, {s.TimeText}")),
                    string.Join("\n", tested.Select(s => $"{s.ServerText} DNSSEC: {s.DnssecText} {s.FailuresText}")));
            }
            case "storage":
            {
                var s = await new StorageHealthService(logger).ScanAsync(ct);
                if (s.Error is not null) return new CheckResult("", CheckState.Unknown, L.T("Depolama bilgisi alınamadı.", "Storage information unavailable."), s.Error);
                var findings = s.Disks.SelectMany(d => d.Findings.Select(f => $"{d.Name}: {f}")).ToList();
                return new CheckResult("", s.Overall,
                    findings.Count == 0 ? L.T($"{s.Disks.Count} disk · ", $"{s.Disks.Count} disk(s) · ") + string.Join(", ", s.Disks.Select(d => $"{d.Name}: {d.StateText}")) : string.Join(" · ", findings),
                    s.ReliabilityNote);
            }
            case "security":
            {
                var s = await new SecurityStatusService(logger).ReadAsync(ct);
                var issues = s.Checks.Where(c => c.State is CheckState.Warning or CheckState.Error).ToList();
                return new CheckResult("", s.Overall,
                    issues.Count == 0 ? L.T("Virüsten koruma ve güvenlik duvarı etkin", "Antivirus and firewall are on") : string.Join(" · ", issues.Select(i => $"{i.Title}: {i.Summary}")),
                    string.Join("\n", s.Checks.Select(c => $"{c.Title}: {CheckStates.Text(c.State)} – {c.Summary}")));
            }
            case "events":
            {
                var c = await new EventLogService(logger).CountAsync("System", TimeSpan.FromHours(24), ct);
                if (!c.Ok) return new CheckResult("", CheckState.Unknown, L.T("Olay günlüğü okunamadı.", "Could not read the event log."), c.Failure);
                return new CheckResult("", c.Critical > 0 ? CheckState.Warning : CheckState.Healthy,
                    L.T($"Son 24 saat (Sistem): {c.Critical} kritik, {c.Errors} hata, {c.Warnings} uyarı", $"Last 24 hours (System): {c.Critical} critical, {c.Errors} error(s), {c.Warnings} warning(s)") + (c.Capped ? L.T(" (sınır)", " (limit)") : ""),
                    c.Critical > 0 ? L.T("Kritik olaylar Olay Günlüğü ekranında listelenir (ör. Kernel-Power 41 = beklenmedik kapanma).", "Critical events are listed on the Event Log screen (e.g. Kernel-Power 41 = unexpected shutdown).") : null);
            }
            case "crash":
            {
                var r = await new CrashAnalysisService(logger).AnalyzeAsync(TimeSpan.FromDays(30), AdminPrivilegeManager.IsElevated, ct);
                if (r.EventError is not null) return new CheckResult("", CheckState.Unknown, L.T("Çökme kayıtları okunamadı.", "Could not read crash records."), r.EventError);
                var parts = new List<string>();
                if (r.BugChecks > 0) parts.Add(L.T($"{r.BugChecks} mavi ekran", $"{r.BugChecks} blue screen(s)"));
                if (r.Unexpected > 0) parts.Add(L.T($"{r.Unexpected} beklenmedik kapanma kaydı", $"{r.Unexpected} unexpected shutdown record(s)"));
                if (r.DisplayResets > 0) parts.Add(L.T($"{r.DisplayResets} ekran sürücüsü sıfırlama", $"{r.DisplayResets} display driver reset(s)"));
                if (r.Hardware > 0) parts.Add(L.T($"{r.Hardware} donanım hatası (WHEA)", $"{r.Hardware} hardware error(s) (WHEA)"));
                return parts.Count == 0
                    ? new CheckResult("", CheckState.Healthy, L.T("Son 30 günde çökme / beklenmedik kapanma kaydı yok", "No crash / unexpected shutdown records in the last 30 days"))
                    : new CheckResult("", r.BugChecks > 0 || r.Hardware > 0 ? CheckState.Error : CheckState.Warning, L.T("Son 30 gün: ", "Last 30 days: ") + string.Join(", ", parts),
                        string.Join("\n", r.Events.Take(5).Select(e => $"{e.TimeText} {e.KindText} {e.CodeText}")));
            }
            case "performance":
            {
                using var monitor = new DeviceMonitorService(logger);
                var s = await Task.Run(monitor.Sample, ct);
                var parts = new List<string>();
                var warn = new List<string>();
                if (s.CpuUsage.Value is { } cpu) { parts.Add(L.T($"CPU %{cpu:0}", $"CPU {cpu:0}%")); if (cpu >= 90) warn.Add(L.T("işlemci yükü yüksek", "high processor load")); }
                if (s.MemoryUsage.Value is { } ram) { parts.Add(L.T($"RAM %{ram:0}", $"RAM {ram:0}%")); if (ram >= 90) warn.Add(L.T("bellek neredeyse dolu", "memory almost full")); }
                foreach (var g in s.Gpus)
                {
                    if (g.Usage.Value is { } gu) parts.Add(L.T($"GPU %{gu:0}", $"GPU {gu:0}%"));
                    if (g.Temperature.Value is { } gt) { parts.Add($"GPU {gt:0} °C"); if (gt >= 90) warn.Add(L.T("ekran kartı sıcak", "graphics card is hot")); }
                }
                if (parts.Count == 0) return new CheckResult("", CheckState.Unknown, L.T("Performans ölçülemedi.", "Performance could not be measured."), s.CpuUsage.Note ?? s.MemoryUsage.Note);
                return new CheckResult("", warn.Count > 0 ? CheckState.Warning : CheckState.Healthy, string.Join(" · ", parts),
                    warn.Count > 0 ? L.T("Dikkat: ", "Attention: ") + string.Join(", ", warn) : s.GpuNote);
            }
            default:
                return new CheckResult("", CheckState.Skipped, L.T("Bilinmeyen adım", "Unknown step"));
        }
    }

    private static CheckResult Summarize(IReadOnlyList<CheckResult> rows, string healthyText)
    {
        var worst = CheckStates.Worst(rows.Select(r => r.State));
        var issues = rows.Where(r => r.State is CheckState.Warning or CheckState.Error or CheckState.Unknown).ToList();
        return new CheckResult("", worst, issues.Count == 0 ? healthyText : string.Join(" · ", issues.Select(i => $"{i.Title}: {i.Summary}")),
            string.Join("\n", rows.Select(r => $"{r.Title}: {CheckStates.Text(r.State)} – {r.Summary}")));
    }

    // ------------------------------------------------------------------ Sistem Sağlığı ekranı

    /// <summary>Kartın son GERÇEK sonucunu tanılama durumuna çevirir (kontrol yapılmadıysa NotChecked).</summary>
    public static CheckState FromModule(ModuleResult? r) => r?.Status switch
    {
        null or ComponentStatus.NotChecked => CheckState.NotChecked,
        ComponentStatus.UpToDate or ComponentStatus.Updated => CheckState.Healthy,
        ComponentStatus.UpdateAvailable or ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired or ComponentStatus.Attention => CheckState.Warning,
        ComponentStatus.CheckFailed or ComponentStatus.Failed => CheckState.Error,
        ComponentStatus.AdminRequired or ComponentStatus.Skipped or ComponentStatus.Unavailable => CheckState.Skipped,
        _ => CheckState.Unknown
    };

    /// <summary>
    /// Sistem Sağlığı satırları: Windows durum satırları + SFC / DISM / Windows Update (kartların son gerçek sonucu; DISM sonucu yoksa ve
    /// yönetici ise salt okunur /CheckHealth çalıştırılır) + depolama + sürücüler + kritik olaylar + son çökmeler.
    /// </summary>
    public async Task<IReadOnlyList<CheckResult>> RunSystemHealthAsync(ModuleResult? sfc, ModuleResult? dism, ModuleResult? windowsUpdate,
        Func<CancellationToken, Task<ModuleResult>>? runDismCheck, Action<string>? progress, CancellationToken ct)
    {
        var rows = new List<CheckResult>();
        progress?.Invoke(L.T("Windows sistem durumu kontrol ediliyor…", "Checking Windows system status…"));
        rows.AddRange(await new WindowsHealthService(logger).CheckAsync(ct)); // kendi bölmesine bağlantı verilmez (yalnızca Servisler gibi başka ekrana)

        rows.Add(sfc is null
            ? new CheckResult(L.T("Sistem dosyaları (SFC)", "System files (SFC)"), CheckState.NotChecked, L.T("Sağlık Araçları'ndaki SFC kartından (salt doğrulama) çalıştırılır; 10-15 dk sürebilir.", "Run from the SFC card in Health Tools (verification only); it can take 10-15 min."), null, Nav.Health, Nav.Cards)
            : new CheckResult(L.T("Sistem dosyaları (SFC)", "System files (SFC)"), FromModule(sfc), sfc.Summary, sfc.Reason, Nav.Health, Nav.Cards));

        if (dism is null && runDismCheck is not null && AdminPrivilegeManager.IsElevated)
        {
            progress?.Invoke(L.T("Windows görüntü sağlığı (DISM /CheckHealth) okunuyor…", "Reading Windows image health (DISM /CheckHealth)…"));
            dism = await runDismCheck(ct);
        }
        rows.Add(dism is null
            ? new CheckResult(L.T("Windows görüntüsü (DISM)", "Windows image (DISM)"), AdminPrivilegeManager.IsElevated ? CheckState.NotChecked : CheckState.Skipped,
                AdminPrivilegeManager.IsElevated ? L.T("DISM kartından kontrol edilir.", "Checked from the DISM card.") : L.T("Yönetici yetkisi gerekiyor", "Administrator rights required"), null, Nav.Health, Nav.Cards)
            : new CheckResult(L.T("Windows görüntüsü (DISM)", "Windows image (DISM)"), FromModule(dism), dism.Summary, dism.Reason, Nav.Health, Nav.Cards));

        rows.Add(windowsUpdate is null
            ? new CheckResult("Windows Update", CheckState.NotChecked, L.T("Güncelleme kategorisindeki Windows Update kartından kontrol edilir.", "Checked from the Windows Update card in the Updates category."), null, Nav.Update, Nav.Cards)
            : new CheckResult("Windows Update", FromModule(windowsUpdate), windowsUpdate.Summary, windowsUpdate.Reason, Nav.Update, Nav.Cards));

        foreach (var key in new[] { "storage", "drivers", "events", "crash" })
        {
            ct.ThrowIfCancellationRequested();
            var step = Steps.First(s => s.Key == key);
            progress?.Invoke(step.RunningText);
            CheckResult r;
            try { r = await RunStepAsync(step, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r = new CheckResult("", CheckState.Unknown, L.T("Kontrol edilemedi.", "Could not be checked."), ex.Message); }
            rows.Add(r with
            {
                Title = key switch { "storage" => L.T("Disk sağlığı ve güvenilirlik", "Disk health and reliability"), "drivers" => L.T("Sistem sürücüleri", "System drivers"), "events" => L.T("Kritik sistem olayları", "Critical system events"), _ => L.T("Son sistem hataları (çökme)", "Recent system errors (crashes)") },
                TargetCategory = step.TargetCategory,
                TargetSection = step.TargetSection
            });
        }
        logger.Info(L.T("Sistem Sağlığı taraması: ", "System Health scan: ") + string.Join(" | ", rows.Select(r => $"{r.Title}: {CheckStates.Text(r.State)}")));
        return rows;
    }
}

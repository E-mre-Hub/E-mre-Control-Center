using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services.Diagnostics;

namespace RtxWindowsUpdater.ViewModels.Tools;

/// <summary>Kart sonuçlarına erişim (Sistem Sağlığı SFC / DISM / Windows Update satırları, Uygulamalar winget denetimi).</summary>
public interface ICardResultSource
{
    ModuleResult? LastCardResult(string key);

    /// <summary>Kartı mevcut akışla (ilerleme, günlük, işlem geçmişi) salt okunur kontrol eder; yönetici değilse / meşgulse null.</summary>
    Task<ModuleResult?> CheckCardForToolAsync(string key);
}

// ====================================================================== Sistem Sağlığı

public sealed class SystemHealthViewModel(IToolHost host, ICardResultSource cards, DiagnosticOrchestrator orchestrator) : ToolViewModel(host)
{
    public ObservableCollection<CheckRowViewModel> Rows { get; } = [];
    public string OverallText { get => _overall; private set => Set(ref _overall, value); }
    public CheckState Overall { get => _state; private set => Set(ref _state, value); }
    private string _overall = L.T("Henüz kontrol edilmedi", "Not checked yet");
    private CheckState _state = CheckState.NotChecked;

    /// <summary>Yönetici ve DISM sonucu yoksa DISM /CheckHealth da çalıştırır (kart akışıyla; yalnızca "Tara" butonunda).</summary>
    public ICommand ScanWithDismCommand => _scanWithDism ??= new AsyncCommand(() => RunAsync(ct => ScanAsync(true, ct), true), () => !IsBusy && Host.CanStartTool);
    private ICommand? _scanWithDism;

    protected override Task LoadAsync(CancellationToken ct) => ScanAsync(false, ct);

    private async Task ScanAsync(bool includeDism, CancellationToken ct)
    {
        var sw = Start();
        StatusText = L.T("Windows sistem durumu kontrol ediliyor…", "Checking Windows system status…");
        if (includeDism && Host.IsAdmin && cards.LastCardResult(ComponentKeys.Dism) is null && Host.CanStartTool)
        {
            StatusText = L.T("Windows görüntü sağlığı (DISM /CheckHealth) kontrol ediliyor…", "Checking Windows image health (DISM /CheckHealth)…");
            await cards.CheckCardForToolAsync(ComponentKeys.Dism);
        }
        var sfc = cards.LastCardResult(ComponentKeys.Sfc);
        var dism = cards.LastCardResult(ComponentKeys.Dism);
        var wu = cards.LastCardResult(ComponentKeys.WindowsUpdate);
        var rows = await Task.Run(() => orchestrator.RunSystemHealthAsync(sfc, dism, wu, null, t => StatusText = t, ct), ct);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(new CheckRowViewModel(r, Host.OpenSection));
        Overall = CheckStates.Worst(rows.Select(r => r.State));
        var warn = rows.Count(r => r.State == CheckState.Warning);
        var err = rows.Count(r => r.State == CheckState.Error);
        var notChecked = rows.Count(r => r.State is CheckState.NotChecked or CheckState.Skipped or CheckState.Unknown);
        OverallText = Overall switch
        {
            CheckState.Healthy => L.T("Kontrol edilen tüm bileşenler sağlıklı", "All checked components are healthy"),
            CheckState.Warning => L.T($"{warn} bileşen dikkat gerektiriyor", $"{warn} component(s) need attention"),
            CheckState.Error => L.T($"{err} bileşende hata var", $"{err} component(s) have errors"),
            _ => CheckStates.Text(Overall)
        } + (notChecked > 0 ? L.T($" · {notChecked} kontrol yapılmadı / yapılamadı", $" · {notChecked} check(s) not run / could not run") : "");
        StatusText = L.T($"{rows.Count} kontrol · {sw.Elapsed.TotalSeconds:0.0} sn", $"{rows.Count} checks · {sw.Elapsed.TotalSeconds:0.0} sec");
        var summary = new CheckResult(L.T("Sistem Sağlığı", "System Health"), Overall, OverallText, null, Nav.Health, Nav.SystemHealth);
        Host.ReportDiagnostic("windows", summary);
        Host.RecordToolOperation(L.T("Windows sağlık taraması", "Windows health scan"), Overall, OverallText, sw.Elapsed,
            err > 0 ? string.Join("; ", rows.Where(r => r.State == CheckState.Error).Select(r => $"{r.Title}: {r.Summary}")) : null);
    }
}

// ====================================================================== Depolama Sağlığı

public sealed class StorageHealthViewModel(IToolHost host) : ToolViewModel(host)
{
    private readonly StorageHealthService _service = new(host.Logger);
    private StorageScan? _scan;

    public ObservableCollection<DiskHealth> Disks { get; } = [];
    public ObservableCollection<VolumeInfo> Volumes { get; } = [];
    public string? Note => _scan?.ReliabilityNote;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Depolama sağlık bilgileri okunuyor…", "Reading storage health information…");
        var scan = await Task.Run(() => _service.ScanAsync(ct), ct);
        _scan = scan;
        Disks.Clear();
        foreach (var d in scan.Disks) Disks.Add(d);
        Volumes.Clear();
        foreach (var v in scan.Volumes) Volumes.Add(v);
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        if (scan.Error is not null) ErrorText = scan.Error;
        StatusText = scan.Error is null ? L.T($"{scan.Disks.Count} fiziksel disk, {scan.Volumes.Count} bölüm", $"{scan.Disks.Count} physical disk(s), {scan.Volumes.Count} partition(s)") : L.T("Okunamadı", "Unreadable");
        var findings = scan.Disks.SelectMany(d => d.Findings).ToList();
        Host.ReportDiagnostic("storage", new CheckResult(L.T("Depolama", "Storage"), scan.Overall,
            scan.Error ?? (findings.Count == 0 ? L.T($"{scan.Disks.Count} disk sağlıklı", $"{scan.Disks.Count} disk(s) healthy") : string.Join(" · ", findings)), null, Nav.Health, Nav.StorageHealth));
    }
}

// ====================================================================== Olay Günlüğü

public sealed class EventLogViewModel : ToolViewModel
{
    private readonly EventLogService _service;
    private string _period = "24h";
    private string _log = "both";
    private bool _critical = true, _error = true, _warning = true;
    private string _search = string.Empty;
    private EventEntry? _selected;

    public EventLogViewModel(IToolHost host) : base(host)
    {
        _service = new EventLogService(host.Logger);
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => o is EventEntry e && (_search.Length == 0 ||
            TextSearch.Contains(e.Provider, _search) || TextSearch.Contains(e.Message, _search) || e.Id.ToString() == _search);
    }

    public ObservableCollection<EventEntry> Entries { get; } = [];
    public ICollectionView EntriesView { get; }

    /// <summary>1h / 24h / 7d / 30d.</summary>
    public string Period { get => _period; set { if (Set(ref _period, value)) _ = RefreshAsync(); } }
    /// <summary>system / application / both.</summary>
    public string Log { get => _log; set { if (Set(ref _log, value)) _ = RefreshAsync(); } }
    public bool IncludeCritical { get => _critical; set { if (Set(ref _critical, value)) _ = RefreshAsync(); } }
    public bool IncludeError { get => _error; set { if (Set(ref _error, value)) _ = RefreshAsync(); } }
    public bool IncludeWarning { get => _warning; set { if (Set(ref _warning, value)) _ = RefreshAsync(); } }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, (value ?? string.Empty).Trim())) EntriesView.Refresh();
        }
    }

    public EventEntry? Selected { get => _selected; set { Set(ref _selected, value); OnPropertyChanged(nameof(HasSelected)); } }
    public bool HasSelected => _selected is not null;

    private TimeSpan PeriodSpan => _period switch
    {
        "1h" => TimeSpan.FromHours(1),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(24)
    };

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var levels = new List<int>();
        if (_critical) levels.Add(1);
        if (_error) levels.Add(2);
        if (_warning) levels.Add(3);
        string[] logs = _log switch { "system" => ["System"], "application" => ["Application"], _ => ["System", "Application"] };
        StatusText = L.T("Olay günlüğü okunuyor…", "Reading the event log…");
        var r = await Task.Run(() => _service.QueryAsync(logs, levels, PeriodSpan, ct), ct);
        Entries.Clear();
        foreach (var e in r.Entries) Entries.Add(e);
        Selected = null;
        if (r.Errors.Count > 0) ErrorText = string.Join(" ", r.Errors);
        StatusText = levels.Count == 0 ? L.T("Düzey seçilmedi", "No level selected")
            : L.T($"{r.Entries.Count(e => e.Level == 1)} kritik · {r.Entries.Count(e => e.Level == 2)} hata · {r.Entries.Count(e => e.Level == 3)} uyarı", $"{r.Entries.Count(e => e.Level == 1)} critical · {r.Entries.Count(e => e.Level == 2)} error(s) · {r.Entries.Count(e => e.Level == 3)} warning(s)") +
              (r.Truncated ? L.T($" (en yeni {EventLogService.MaxEntries} kayıt)", $" (newest {EventLogService.MaxEntries} records)") : "");
        if (_log == "both" && _critical && _error && _period == "24h")
        {
            var crit = r.Entries.Count(e => e.Level == 1 && e.Log == "System");
            Host.ReportDiagnostic("events", new CheckResult(L.T("Olay Günlüğü", "Event Log"), r.Errors.Count > 0 ? CheckState.Unknown : crit > 0 ? CheckState.Warning : CheckState.Healthy,
                r.Errors.Count > 0 ? r.Errors[0] : L.T($"Son 24 saat: {crit} kritik sistem olayı", $"Last 24 hours: {crit} critical system event(s)"), null, Nav.Health, Nav.EventLog));
        }
    }
}

// ====================================================================== Çökme Analizi

public sealed class CrashViewModel(IToolHost host) : ToolViewModel(host)
{
    private readonly CrashAnalysisService _service = new(host.Logger);
    private string _days = "90";
    private CrashReport? _report;

    public ObservableCollection<CrashEvent> Events { get; } = [];
    public ObservableCollection<DumpFileInfo> Dumps { get; } = [];
    /// <summary>"30" / "90" / "365" gün (çip seçimi).</summary>
    public string Days { get => _days; set { if (Set(ref _days, value)) _ = RefreshAsync(); } }
    private int DayCount => int.TryParse(_days, out var d) ? d : 90;
    public string CountsText => _report is null ? "—"
        : L.T($"{_report.BugChecks} mavi ekran · {_report.Unexpected} beklenmedik kapanma kaydı · {_report.DisplayResets} ekran sürücüsü sıfırlama · {_report.Hardware} donanım hatası (WHEA)", $"{_report.BugChecks} blue screen(s) · {_report.Unexpected} unexpected shutdown record(s) · {_report.DisplayResets} display driver reset(s) · {_report.Hardware} hardware error(s) (WHEA)");
    public string DumpNote => _report is null ? string.Empty
        : (_report.DumpNote ?? L.T($"{_report.Dumps.Count} minidump okundu", $"{_report.Dumps.Count} minidump(s) read")) +
          (_report.FullDumpExists ? L.T($" · Tam bellek dökümü (MEMORY.DMP) var: {Formats.Bytes(_report.FullDumpSize ?? 0)}", $" · Full memory dump (MEMORY.DMP) present: {Formats.Bytes(_report.FullDumpSize ?? 0)}") : "");
    public bool HasEvents => Events.Count > 0;
    public bool HasDumps => Dumps.Count > 0;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Çökme kayıtları taranıyor…", "Scanning crash records…");
        var r = await Task.Run(() => _service.AnalyzeAsync(TimeSpan.FromDays(DayCount), Host.IsAdmin, ct), ct);
        _report = r;
        Events.Clear();
        foreach (var e in r.Events) Events.Add(e);
        Dumps.Clear();
        foreach (var d in r.Dumps) Dumps.Add(d);
        if (r.EventError is not null) ErrorText = r.EventError;
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(DumpNote));
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(HasDumps));
        StatusText = r.Events.Count == 0 ? L.T($"Son {DayCount} günde kayıt yok", $"No records in the last {DayCount} days") : L.T($"Son {DayCount} günde {r.Events.Count} kayıt", $"{r.Events.Count} record(s) in the last {DayCount} days");
        if (DayCount >= 30)
        {
            var recent = r.Events.Where(e => e.Time >= DateTime.Now.AddDays(-30)).ToList();
            var bug = recent.Count(e => e.Kind is CrashKind.BugCheck or CrashKind.Hardware);
            Host.ReportDiagnostic("crash", new CheckResult(L.T("Çökme Geçmişi", "Crash History"),
                r.EventError is not null ? CheckState.Unknown : bug > 0 ? CheckState.Error : recent.Count > 0 ? CheckState.Warning : CheckState.Healthy,
                r.EventError ?? (recent.Count == 0 ? L.T("Son 30 günde çökme kaydı yok", "No crash records in the last 30 days") : L.T($"Son 30 günde {recent.Count} kayıt ({bug} mavi ekran / donanım hatası)", $"{recent.Count} record(s) in the last 30 days ({bug} blue screen / hardware error)")),
                null, Nav.Health, Nav.Crash));
        }
    }
}

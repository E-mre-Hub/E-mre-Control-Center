using System.Collections.ObjectModel;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services;
using RtxWindowsUpdater.Services.Diagnostics;
using RtxWindowsUpdater.ViewModels.Tools;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Özet ekranındaki tanılama satırı: son GERÇEK sonuç ya da "Henüz kontrol edilmedi".</summary>
public sealed class DiagnosticSummaryRowViewModel(string key, string title, string glyph, string category, string section, Action<string, string> open)
    : ObservableObject
{
    private CheckState _state = CheckState.NotChecked;
    private string _summary = "Tek Tıkla Tanıla veya ilgili ekrandan kontrol edilir.";
    private DateTime? _time;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public CheckState State { get => _state; private set => Set(ref _state, value); }
    public string StateText => CheckStates.Text(_state);
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string TimeText => _time is { } t ? t.ToString("HH:mm") : string.Empty;
    public ICommand OpenCommand { get; } = new RelayCommand(() => open(category, section));

    public void Apply(CheckResult r)
    {
        State = r.State;
        Summary = r.Summary;
        _time = DateTime.Now;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(TimeText));
    }
}

/// <summary>Yeni tanılama / bakım ekranlarının görünüm modelleri (her biri kendi bölmesinde gösterilir).</summary>
public sealed class ToolsViewModel
{
    public required SystemHealthViewModel SystemHealth { get; init; }
    public required DriversViewModel Drivers { get; init; }
    public required AppsViewModel Apps { get; init; }
    public required StorageAnalysisViewModel StorageAnalysis { get; init; }
    public required StorageHealthViewModel StorageHealth { get; init; }
    public required EventLogViewModel EventLog { get; init; }
    public required CrashViewModel Crash { get; init; }
    public required NetworkViewModel Network { get; init; }
    public required DnsViewModel Dns { get; init; }
    public required PrivacyViewModel Privacy { get; init; }
    public required BatteryViewModel Battery { get; init; }
    public required DiagnoseViewModel Diagnose { get; init; }
    public required StartupViewModel Startup { get; init; }
    public required ServicesViewModel Services { get; init; }
    public required ProcessesViewModel Processes { get; init; }
    public required SecurityViewModel Security { get; init; }
    public required ReportViewModel Report { get; init; }
    public required SupportPackageViewModel Support { get; init; }
}

/// <summary>
/// Sistem Tanılama genişletmesi (v1.8.0): yeni bölmelerin görünüm modelleri, bölme açılınca etkinleştirme / kapanınca durdurma,
/// Özet tanılama satırları, araç işlemlerinin işlem geçmişine kaydı ve "Tümünü Kontrol Et"in güvenli tanılama kısmı.
/// </summary>
public sealed partial class MainViewModel : IToolHost, ICardResultSource
{
    private DiagnosticOrchestrator _diagnostics = null!;
    private Dictionary<string, ToolViewModel> _toolBySection = new();
    private ToolViewModel? _currentTool;
    private double _progressScale = 1;

    public ToolsViewModel Tools { get; private set; } = null!;

    /// <summary>Açık bölmenin araç görünüm modeli (yeni bölmelerde; diğerlerinde null).</summary>
    public ToolViewModel? CurrentTool => _currentTool;

    /// <summary>Özet → Sağlık Özeti'ndeki tanılama satırları.</summary>
    public ObservableCollection<DiagnosticSummaryRowViewModel> DiagnosticSummary { get; } = [];

    Logger IToolHost.Logger => _logger;
    bool IToolHost.CanStartTool => CanStartOperation;

    private void InitTools()
    {
        _diagnostics = new DiagnosticOrchestrator(_logger);
        var network = new NetworkState();
        var reports = new SystemReportService(_logger, _systemInfo);
        Tools = new ToolsViewModel
        {
            SystemHealth = new SystemHealthViewModel(this, this, _diagnostics),
            Drivers = new DriversViewModel(this),
            Apps = new AppsViewModel(this, this),
            StorageAnalysis = new StorageAnalysisViewModel(this),
            StorageHealth = new StorageHealthViewModel(this),
            EventLog = new EventLogViewModel(this),
            Crash = new CrashViewModel(this),
            Network = new NetworkViewModel(this, network),
            Dns = new DnsViewModel(this, network),
            Privacy = new PrivacyViewModel(this),
            Battery = new BatteryViewModel(this),
            Diagnose = new DiagnoseViewModel(this, _diagnostics, network),
            Startup = new StartupViewModel(this),
            Services = new ServicesViewModel(this),
            Processes = new ProcessesViewModel(this),
            Security = new SecurityViewModel(this),
            Report = new ReportViewModel(this, reports, network),
            Support = new SupportPackageViewModel(this, new SupportPackageService(_logger, reports), () => RecentOperations.ToList())
        };
        _toolBySection = new Dictionary<string, ToolViewModel>
        {
            [SectionKeys.SystemHealth] = Tools.SystemHealth,
            [SectionKeys.Drivers] = Tools.Drivers,
            [SectionKeys.Apps] = Tools.Apps,
            [SectionKeys.StorageAnalysis] = Tools.StorageAnalysis,
            [SectionKeys.StorageHealth] = Tools.StorageHealth,
            [SectionKeys.EventLog] = Tools.EventLog,
            [SectionKeys.Crash] = Tools.Crash,
            [SectionKeys.Network] = Tools.Network,
            [SectionKeys.Dns] = Tools.Dns,
            [SectionKeys.Privacy] = Tools.Privacy,
            [SectionKeys.Battery] = Tools.Battery,
            [SectionKeys.Diagnose] = Tools.Diagnose,
            [SectionKeys.Startup] = Tools.Startup,
            [SectionKeys.Services] = Tools.Services,
            [SectionKeys.Processes] = Tools.Processes,
            [SectionKeys.Security] = Tools.Security,
            [SectionKeys.Report] = Tools.Report,
            [SectionKeys.Support] = Tools.Support
        };
        (string Key, string Title, string Glyph, string Category, string Section)[] rows =
        [
            ("windows", "Sistem Sağlığı", "", Nav.Health, Nav.SystemHealth),
            ("network", "Ağ", "", Nav.SpeedTest, Nav.Network),
            ("dns", "DNS", "", Nav.SpeedTest, Nav.Dns),
            ("storage", "Depolama", "", Nav.Health, Nav.StorageHealth),
            ("security", "Güvenlik", "", Nav.SystemTools, Nav.Security),
            ("drivers", "Sürücüler", "", Nav.Update, Nav.Drivers),
            ("events", "Olay Günlüğü", "", Nav.Health, Nav.EventLog),
            ("crash", "Çökme Geçmişi", "", Nav.Health, Nav.Crash)
        ];
        foreach (var r in rows) DiagnosticSummary.Add(new DiagnosticSummaryRowViewModel(r.Key, r.Title, r.Glyph, r.Category, r.Section, OpenSection));
    }

    /// <summary>Bölme değişince: önceki aracı durdur (izleme / süren okuma), yenisini etkinleştir (gerekiyorsa bir kez okur).</summary>
    private void UpdateToolActivation()
    {
        var next = IsDashboard && CurrentSectionKey is { } key && _toolBySection.TryGetValue(key, out var t) ? t : null;
        if (ReferenceEquals(next, _currentTool)) return;
        _currentTool?.Deactivate();
        _currentTool = next;
        OnPropertyChanged(nameof(CurrentTool));
        next?.Activate();
    }

    public void ReportDiagnostic(string key, CheckResult result)
    {
        void Apply()
        {
            DiagnosticSummary.FirstOrDefault(r => r.Key == key)?.Apply(result);
            RefreshCategories();
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }

    /// <summary>Sistem Araçları kutucuğunun durum satırı: yalnızca gerçekten yapılmış tanılamalardan.</summary>
    private (ComponentStatus, string) DiagnosticTile()
    {
        var done = DiagnosticSummary.Where(r => r.State is not CheckState.NotChecked).ToList();
        if (done.Count == 0) return (ComponentStatus.NotChecked, "Henüz tanılama yapılmadı");
        var worst = CheckStates.Worst(done.Select(r => r.State));
        var status = worst switch
        {
            CheckState.Healthy => ComponentStatus.UpToDate,
            CheckState.Warning => ComponentStatus.UpdateAvailable,
            CheckState.Error => ComponentStatus.Failed,
            _ => ComponentStatus.NotChecked
        };
        var ok = done.Count(r => r.State is CheckState.Healthy or CheckState.Info);
        var warn = done.Count(r => r.State == CheckState.Warning);
        var err = done.Count(r => r.State is CheckState.Error or CheckState.Unknown);
        return (status, $"Tanılama: {ok} sağlıklı" + (warn > 0 ? $" · {warn} uyarı" : "") + (err > 0 ? $" · {err} hata" : ""));
    }

    public void RecordToolOperation(string title, CheckState state, string summary, TimeSpan duration, string? error = null, bool cancelled = false)
    {
        void Apply()
        {
            var rec = new OperationRecord
            {
                CompletedAt = DateTime.Now,
                Title = cancelled ? $"{title} iptal edildi." : state is CheckState.Error or CheckState.Unknown ? $"{title} – hata var." : $"{title} tamamlandı.",
                SummaryText = $"{summary} · Süre: {UpdateOrchestrator.FormatDuration(duration)}",
                Keys = ["tool"],
                DurationMs = (long)duration.TotalMilliseconds,
                Cancelled = cancelled,
                Completed = state is CheckState.Healthy or CheckState.Info or CheckState.Warning ? 1 : 0,
                Warnings = state == CheckState.Warning ? 1 : 0,
                Errors = state is CheckState.Error or CheckState.Unknown ? 1 : 0,
                Skipped = state == CheckState.Skipped ? 1 : 0,
                ErrorText = error
            };
            _state.AddOperation(rec);
            RecentOperations.Insert(0, rec);
            while (RecentOperations.Count > 50) RecentOperations.RemoveAt(RecentOperations.Count - 1);
            LastOperation = rec;
            if (rec.Errors > 0) _logger.Warning($"Son işlem: {rec.Title} {rec.SummaryText}" + (error is null ? "" : " – " + error));
            else _logger.Info($"Son işlem: {rec.Title} {rec.SummaryText}");
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }

    public ModuleResult? LastCardResult(string key) => _checks.GetValueOrDefault(key);

    /// <summary>Kartı mevcut tek kart akışıyla kontrol eder (kart, ilerleme paneli, günlük ve işlem geçmişi güncellenir); salt okunur.</summary>
    public async Task<ModuleResult?> CheckCardForToolAsync(string key)
    {
        var card = Cards.FirstOrDefault(c => c.Key == key);
        if (card is null || card.IsUnavailable || !CanStartOperation || !IsAdmin || !IsSupported) return null;
        card.Reset();
        _checks.Remove(key);
        RaiseSummaryChanged();
        Dictionary<string, ModuleResult>? results = null;
        await RunBusyAsync(updatePhase: false,
            async ct => results = await _orchestrator.RunSingleCheckAsync(key, Reporters(), ct),
            done =>
            {
                StoreChecks(results);
                var anyError = FinishOperation($"{card.ShortTitle} kontrolü", results, done, updatePhase: false, single: true, NotifyPolicy.Never);
                return new OperationEnd(anyError, card.Title + ": kontrol tamamlandı", card.Title + ": kontrol başarısız oldu", "Kontrol iptal edildi");
            });
        return _checks.GetValueOrDefault(key);
    }

    /// <summary>
    /// "Tümünü Kontrol Et"in güvenli tanılama kısmı (kart kontrollerinden sonra): Windows sağlığı, sürücüler, ağ, DNS, depolama, güvenlik.
    /// Yalnızca okuma; ilerleme gerçek adım sayısından (tamamlanan adım / toplam adım) hesaplanır.
    /// </summary>
    private async Task<string> RunQuickDiagnosticsAsync(double fromPercent, CancellationToken ct)
    {
        var keys = DiagnosticOrchestrator.QuickKeys;
        var done = 0;
        var results = await Task.Run(() => _diagnostics.RunAsync(keys, (step, result) => _dispatcher.BeginInvoke(() =>
        {
            if (!IsBusy) return;
            if (result.State == CheckState.Checking)
            {
                StepText = step.RunningText;
                return;
            }
            done++;
            Progress = fromPercent + (100 - fromPercent) * done / keys.Count;
            ReportDiagnostic(step.Key, result with { Title = step.Title });
        }), ct), ct);
        var ok = results.Count(r => r.Result.State is CheckState.Healthy or CheckState.Info);
        var warn = results.Count(r => r.Result.State == CheckState.Warning);
        var err = results.Count(r => r.Result.State is CheckState.Error or CheckState.Unknown);
        return $"Tanılama: {ok} sağlıklı, {warn} uyarı, {err} hata / kontrol edilemedi";
    }

    private ICommand? _openDiagnoseCommand;
    /// <summary>Özet ve Sistem Araçları'ndan Tek Tıkla Tanıla'ya kısayol.</summary>
    public ICommand OpenDiagnoseCommand => _openDiagnoseCommand ??= new RelayCommand(() => OpenSection(CategoryKeys.SystemTools, SectionKeys.Diagnose));
}

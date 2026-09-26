using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using RtxWindowsUpdater.Services;
using RtxWindowsUpdater.Services.Diagnostics;

namespace RtxWindowsUpdater.ViewModels.Tools;

// ====================================================================== Tek Tıkla Tanıla

public sealed class DiagnoseViewModel : ToolViewModel
{
    private readonly DiagnosticOrchestrator _orchestrator;
    private readonly NetworkState _network;
    private string _countsText = "Tanılama henüz çalıştırılmadı.";

    public DiagnoseViewModel(IToolHost host, DiagnosticOrchestrator orchestrator, NetworkState network) : base(host)
    {
        _orchestrator = orchestrator;
        _network = network;
        foreach (var s in DiagnosticOrchestrator.Steps)
            Steps.Add(new CheckRowViewModel(new CheckResult(s.Title, CheckState.NotChecked, "Bekliyor", null, s.TargetCategory, s.TargetSection), host.OpenSection, s.Index));
        StartCommand = new AsyncCommand(() => RunAsync(DiagnoseAsync), () => !IsBusy && Host.CanStartTool);
        StatusText = "10 gerçek kontrol sırayla çalışır; hiçbiri sistemde değişiklik yapmaz.";
    }

    public ObservableCollection<CheckRowViewModel> Steps { get; } = [];
    public ICommand StartCommand { get; }
    public string CountsText { get => _countsText; private set => Set(ref _countsText, value); }
    public int Total { get; private set; }
    public int Passed { get; private set; }
    public int Warnings { get; private set; }
    public int Errors { get; private set; }
    public int SkippedCount { get; private set; }
    public int Unknown { get; private set; }
    public bool HasCounts => Total > 0;

    protected override bool AutoLoad => false;
    protected override Task LoadAsync(CancellationToken ct) => DiagnoseAsync(ct);

    private async Task DiagnoseAsync(CancellationToken ct)
    {
        var sw = Start();
        foreach (var row in Steps)
            row.Update(row.Result with { State = CheckState.NotChecked, Summary = "Bekliyor", Detail = null });
        Total = Passed = Warnings = Errors = SkippedCount = Unknown = 0;
        RaiseCounts();
        var dispatcher = Dispatcher.CurrentDispatcher;
        IReadOnlyList<DiagnosticStepResult> results;
        try
        {
            results = await Task.Run(() => _orchestrator.RunAsync(null, (step, result) => dispatcher.BeginInvoke(() =>
            {
                Steps[step.Index - 1].Update(result);
                if (result.State == CheckState.Checking) StatusText = $"{step.Index}/{DiagnosticOrchestrator.Steps.Count} · {step.RunningText}";
            }), ct), ct);
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Steps.Where(r => r.State is CheckState.Checking or CheckState.NotChecked))
                row.Update(row.Result with { State = CheckState.Skipped, Summary = "İptal edildiği için çalıştırılmadı" });
            Host.RecordToolOperation("Tek Tıkla Tanıla", CheckState.Skipped, "İptal edildi", sw.Elapsed, null, cancelled: true);
            throw;
        }
        // Son durum (BeginInvoke sırası korunur; yine de sonuçlar kesin olarak uygulanır).
        foreach (var r in results) Steps[r.Step.Index - 1].Update(r.Result);
        Total = results.Count;
        Passed = results.Count(r => r.Result.State is CheckState.Healthy or CheckState.Info);
        Warnings = results.Count(r => r.Result.State == CheckState.Warning);
        Errors = results.Count(r => r.Result.State == CheckState.Error);
        SkippedCount = results.Count(r => r.Result.State == CheckState.Skipped);
        Unknown = results.Count(r => r.Result.State == CheckState.Unknown);
        RaiseCounts();
        _network.Snapshot = _orchestrator.LastNetwork ?? _network.Snapshot;
        _network.Tests = _orchestrator.LastNetworkTests ?? _network.Tests;
        _network.Dns = _orchestrator.LastDns ?? _network.Dns;
        foreach (var r in results)
        {
            var key = r.Step.Key switch { "windows" => "windows", "drivers" => "drivers", "network" => "network", "dns" => "dns", "storage" => "storage",
                "security" => "security", "events" => "events", "crash" => "crash", _ => null };
            if (key is not null) Host.ReportDiagnostic(key, r.Result with { Title = r.Step.Title });
        }
        StatusText = $"Tamamlandı · {sw.Elapsed.TotalSeconds:0.0} sn · {DateTime.Now:HH:mm:ss}";
        var worst = CheckStates.Worst(results.Select(r => r.Result.State));
        Host.RecordToolOperation("Tek Tıkla Tanıla", worst, CountsText, sw.Elapsed,
            Errors > 0 ? string.Join("; ", results.Where(r => r.Result.State == CheckState.Error).Select(r => $"{r.Step.Title}: {r.Result.Summary}")) : null);
    }

    private void RaiseCounts()
    {
        CountsText = Total == 0 ? "Tanılama çalışıyor…"
            : $"Toplam {Total} kontrol · {Passed} başarılı · {Warnings} uyarı · {Errors} hata · {SkippedCount} atlandı" + (Unknown > 0 ? $" · {Unknown} kontrol edilemedi" : "");
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Passed));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(Unknown));
        OnPropertyChanged(nameof(HasCounts));
    }
}

// ====================================================================== Başlangıç Uygulamaları

public sealed class StartupViewModel : ToolViewModel
{
    private readonly StartupService _service;
    private StartupEntry? _selected;

    public StartupViewModel(IToolHost host) : base(host)
    {
        _service = new StartupService(host.Logger);
        ToggleCommand = new AsyncCommand(ToggleAsync, () => !IsBusy && _selected?.Toggleable == true);
        ShowCommand = new RelayCommand(() => ShowInExplorer(_selected?.TargetPath), () => _selected?.TargetPath is not null);
        OpenStartupSettingsCommand = new RelayCommand(() => OpenWindowsUri("ms-settings:startupapps"));
    }

    public ObservableCollection<StartupEntry> Entries { get; } = [];
    public ICommand ToggleCommand { get; }
    public ICommand ShowCommand { get; }
    public ICommand OpenStartupSettingsCommand { get; }
    public StartupEntry? Selected
    {
        get => _selected;
        set { Set(ref _selected, value); OnPropertyChanged(nameof(ToggleText)); OnPropertyChanged(nameof(SelectionNote)); CommandManager.InvalidateRequerySuggested(); }
    }
    public string ToggleText => _selected?.Enabled == false ? "Etkinleştir…" : "Devre dışı bırak…";
    public string SelectionNote => _selected is null ? "Bir kayıt seçin."
        : _selected.ProtectedReason ?? _selected.Note ?? (_selected.NeedsAdmin && !Host.IsAdmin ? "Tüm kullanıcılar için olan kayıt yönetici yetkisi gerektirir." : _selected.Command);

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Başlangıç kayıtları okunuyor…";
        var scan = await Task.Run(() => _service.ScanAsync(ct), ct);
        var keep = _selected;
        Entries.Clear();
        foreach (var e in scan.Entries) Entries.Add(e);
        Selected = Entries.FirstOrDefault(e => keep is not null && e.Name == keep.Name && e.Source == keep.Source);
        if (scan.Errors.Count > 0) ErrorText = string.Join(" ", scan.Errors);
        StatusText = $"{scan.Entries.Count} kayıt · {scan.Entries.Count(e => e.Enabled)} etkin";
    }

    /// <summary>Kullanıcı onayıyla, Görev Yöneticisi'nin yöntemiyle (StartupApproved) durum değişikliği; kayıt silinmez.</summary>
    private async Task ToggleAsync()
    {
        if (_selected is not { Toggleable: true } e) return;
        var enable = !e.Enabled;
        var ok = await ConfirmAsync(enable ? "Başlangıçta etkinleştirilsin mi?" : "Başlangıçtan devre dışı bırakılsın mı?",
            (enable ? "Uygulama bir sonraki oturum açılışında yeniden otomatik başlayacak." : "Uygulama bir sonraki oturum açılışında otomatik başlamayacak.") +
            " Başlangıç kaydı SİLİNMEZ; yalnızca Windows'un Görev Yöneticisi'nde de kullandığı durum değeri değiştirilir ve istediğiniz zaman geri alınabilir.",
            enable ? "Etkinleştir" : "Devre dışı bırak", [$"Ad: {e.Name}", $"Kaynak: {e.SourceText}", $"Yayıncı: {e.PublisherText}", $"Komut: {e.Command}"], warning: !enable);
        if (!ok) return;
        var sw = Start();
        var (success, message) = await Task.Run(() => _service.SetEnabled(e, enable));
        Host.RecordToolOperation(enable ? "Başlangıç kaydını etkinleştirme" : "Başlangıç kaydını devre dışı bırakma", success ? CheckState.Healthy : CheckState.Error,
            $"{e.Name}: {message}", sw.Elapsed, success ? null : message);
        await InformAsync(success ? "Değişiklik uygulandı" : "Değişiklik yapılamadı", message, !success);
        await RefreshAsync();
    }
}

// ====================================================================== Windows Servisleri

public sealed class ServicesViewModel : ToolViewModel
{
    private readonly WindowsServiceManager _service;
    private string _search = string.Empty;
    private string _filter = "all";
    private ServiceEntry? _selected;

    public ServicesViewModel(IToolHost host) : base(host)
    {
        _service = new WindowsServiceManager(host.Logger);
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = o => o is ServiceEntry s &&
            (_filter switch
            {
                "running" => s.IsRunning,
                "stopped" => !s.IsRunning,
                "thirdparty" => s.Publisher is { } p && !p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
                _ => true
            }) &&
            (_search.Length == 0 || TextSearch.Contains(s.DisplayName, _search) || TextSearch.Contains(s.Name, _search) || TextSearch.Contains(s.PublisherText, _search));
        StartCommand = new AsyncCommand(() => ActAsync(ServiceAction.Start), () => !IsBusy && _selected?.CanStart == true && Host.IsAdmin);
        StopCommand = new AsyncCommand(() => ActAsync(ServiceAction.Stop), () => !IsBusy && _selected?.CanStop == true && Host.IsAdmin);
        RestartCommand = new AsyncCommand(() => ActAsync(ServiceAction.Restart), () => !IsBusy && _selected?.CanStop == true && Host.IsAdmin);
        OpenServicesConsoleCommand = new RelayCommand(OpenConsole);
    }

    public ObservableCollection<ServiceEntry> Services { get; } = [];
    public ICollectionView ServicesView { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenServicesConsoleCommand { get; }
    public string SearchText { get => _search; set { if (Set(ref _search, (value ?? "").Trim())) ServicesView.Refresh(); } }
    /// <summary>all / running / stopped / thirdparty.</summary>
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ServicesView.Refresh(); } }
    public ServiceEntry? Selected
    {
        get => _selected;
        set { Set(ref _selected, value); OnPropertyChanged(nameof(SelectionNote)); CommandManager.InvalidateRequerySuggested(); }
    }
    public string SelectionNote => !Host.IsAdmin ? "Hizmet başlatma / durdurma yönetici yetkisi gerektirir."
        : _selected is null ? "Bir hizmet seçin."
        : _selected.IsRunning && !_selected.CanStop ? _selected.StopBlockedText
        : _selected.IsDisabled ? "Hizmet devre dışı; başlangıç türü bu uygulamadan değiştirilmez."
        : _selected.DescriptionText;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Windows hizmetleri okunuyor…";
        var scan = await Task.Run(() => _service.ListAsync(ct), ct);
        var keep = _selected?.Name;
        Services.Clear();
        foreach (var s in scan.Services) Services.Add(s);
        Selected = Services.FirstOrDefault(s => s.Name == keep);
        if (scan.Error is not null) ErrorText = scan.Error;
        StatusText = $"{scan.Services.Count} hizmet · {scan.Services.Count(s => s.IsRunning)} çalışıyor";
    }

    private async Task ActAsync(ServiceAction action)
    {
        if (_selected is not { } s) return;
        var (title, verb) = action switch
        {
            ServiceAction.Start => ("Hizmet başlatılsın mı?", "Başlat"),
            ServiceAction.Stop => ("Hizmet durdurulsun mu?", "Durdur"),
            _ => ("Hizmet yeniden başlatılsın mı?", "Yeniden başlat")
        };
        var ok = await ConfirmAsync(title,
            action == ServiceAction.Start ? "Hizmet Windows Hizmet Denetimi Yöneticisi ile başlatılacak; başlangıç türü değişmez."
                : "Hizmeti kullanan uygulamalar etkilenebilir. Kritik Windows hizmetleri bu uygulamadan durdurulamaz; başlangıç türü değişmez.",
            verb, [$"Hizmet: {s.DisplayName} ({s.Name})", $"Durum: {s.StateText} · Başlangıç: {s.StartModeText}", $"Yayıncı: {s.PublisherText}", $"Dosya: {s.PathText}"],
            warning: action != ServiceAction.Start);
        if (!ok) return;
        var sw = Start();
        (bool Success, string Message) result = (false, "");
        await RunAsync(async ct => result = await _service.RunAsync(s, action, ct));
        Host.RecordToolOperation($"Hizmet: {verb.ToLowerInvariant()} – {s.DisplayName}", result.Success ? CheckState.Healthy : CheckState.Error, result.Message, sw.Elapsed,
            result.Success ? null : result.Message);
        await InformAsync(result.Success ? "İşlem tamamlandı" : "İşlem başarısız", $"{s.DisplayName}: {result.Message}", !result.Success);
        await RefreshAsync();
    }

    private void OpenConsole()
    {
        try
        {
            var mmc = Path.Combine(Environment.SystemDirectory, "mmc.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(mmc) { UseShellExecute = true };
            psi.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "services.msc"));
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warning("Hizmetler konsolu açılamadı: " + ex.Message);
        }
    }
}

// ====================================================================== İşlemler

public sealed class ProcessRowViewModel(ProcessEntry e) : ObservableObject
{
    private ProcessEntry _e = e;
    public ProcessEntry Entry => _e;
    public int Pid => _e.Pid;
    public string Name => _e.Name;
    public double CpuValue => _e.CpuPercent ?? -1;
    public string CpuText => _e.CpuText;
    public long MemoryValue => _e.PrivateBytes;
    public string MemoryText => _e.MemoryText;
    public double GpuValue => _e.GpuPercent ?? -1;
    public string GpuText => _e.GpuText;
    public string Path => _e.PathText;
    public string Publisher => _e.PublisherText;
    public bool CanEnd => _e.CanEnd;
    public string? ProtectedReason => _e.ProtectedReason;

    public void Update(ProcessEntry e)
    {
        var old = _e;
        _e = e;
        if (old.CpuPercent != e.CpuPercent) { OnPropertyChanged(nameof(CpuValue)); OnPropertyChanged(nameof(CpuText)); }
        if (old.PrivateBytes != e.PrivateBytes) { OnPropertyChanged(nameof(MemoryValue)); OnPropertyChanged(nameof(MemoryText)); }
        if (old.GpuPercent != e.GpuPercent) { OnPropertyChanged(nameof(GpuValue)); OnPropertyChanged(nameof(GpuText)); }
    }
}

public sealed class ProcessesViewModel : ToolViewModel
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly ProcessService _service;
    private readonly Dictionary<(int, long), ProcessRowViewModel> _rows = new();
    private DispatcherTimer? _timer;
    private bool _sampling;
    private string _search = string.Empty;
    private ProcessRowViewModel? _selected;
    private string _totalsText = string.Empty;
    private string? _gpuNote;

    public ProcessesViewModel(IToolHost host) : base(host)
    {
        _service = new ProcessService(host.Logger);
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Processes);
        view.Filter = o => o is ProcessRowViewModel p && (_search.Length == 0 || TextSearch.Contains(p.Name, _search) ||
            TextSearch.Contains(p.Path, _search) || p.Pid.ToString() == _search || TextSearch.Contains(p.Publisher, _search));
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.CpuValue), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRowViewModel.CpuValue), nameof(ProcessRowViewModel.MemoryValue), nameof(ProcessRowViewModel.GpuValue) })
            view.LiveSortingProperties.Add(p);
        ProcessesView = view;
        EndCommand = new AsyncCommand(EndAsync, () => _selected?.CanEnd == true);
        ShowCommand = new RelayCommand(() => ShowInExplorer(_selected?.Entry.Path), () => _selected?.Entry.Path is not null);
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];
    public ICollectionView ProcessesView { get; }
    public ICommand EndCommand { get; }
    public ICommand ShowCommand { get; }
    public string TotalsText { get => _totalsText; private set => Set(ref _totalsText, value); }
    public string? GpuNote { get => _gpuNote; private set { Set(ref _gpuNote, value); OnPropertyChanged(nameof(HasGpuNote)); } }
    public bool HasGpuNote => !string.IsNullOrEmpty(_gpuNote);
    public string SearchText { get => _search; set { if (Set(ref _search, (value ?? "").Trim())) ProcessesView.Refresh(); } }
    public ProcessRowViewModel? Selected
    {
        get => _selected;
        set { Set(ref _selected, value); OnPropertyChanged(nameof(SelectionNote)); CommandManager.InvalidateRequerySuggested(); }
    }
    public string SelectionNote => _selected is null ? "Bir işlem seçin." : _selected.ProtectedReason ?? _selected.Path;

    /// <summary>Örnekleme yalnızca bu ekran açıkken 2 saniyede bir (ekran kapanınca durur).</summary>
    public override void Activate()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += async (_, _) => await SampleAsync();
        _timer.Start();
        Logger.Info("İşlemler: izleme başladı (2 saniyede bir).");
        _ = SampleAsync();
    }

    public override void Deactivate()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
        _service.Reset();
        Logger.Info("İşlemler: izleme durdu.");
    }

    public bool IsMonitoring => _timer is not null;

    protected override Task LoadAsync(CancellationToken ct) => SampleAsync();

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var snap = await Task.Run(() => _service.SampleAsync(true, CancellationToken.None));
            if (_timer is null && HasLoadedOnce) return; // ekran bu sırada kapandı
            HasLoadedOnce = true;
            if (snap.Error is not null)
            {
                ErrorText = snap.Error;
                return;
            }
            ErrorText = null;
            var seen = new HashSet<(int, long)>();
            foreach (var e in snap.Processes)
            {
                var key = (e.Pid, e.CreateTime);
                seen.Add(key);
                if (_rows.TryGetValue(key, out var row)) row.Update(e);
                else
                {
                    row = new ProcessRowViewModel(e);
                    _rows[key] = row;
                    Processes.Add(row);
                }
            }
            foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                if (ReferenceEquals(_selected, _rows[gone])) Selected = null;
                Processes.Remove(_rows[gone]);
                _rows.Remove(gone);
            }
            GpuNote = snap.GpuNote;
            TotalsText = $"{snap.Processes.Count} işlem · toplam CPU %{snap.TotalCpuPercent:0} · {DateTime.Now:HH:mm:ss}";
            StatusText = "Canlı · 2 saniyede bir güncellenir";
        }
        catch (Exception ex)
        {
            ErrorText = "İşlem listesi okunamadı: " + ex.Message;
        }
        finally
        {
            _sampling = false;
        }
    }

    private bool HasLoadedOnce { get; set; }

    private async Task EndAsync()
    {
        if (_selected is not { CanEnd: true } row) return;
        var e = row.Entry;
        var ok = await ConfirmAsync("İşlem sonlandırılsın mı?",
            "Önce normal kapatma istenir; yanıt vermezse işlem sonlandırılır. Kaydedilmemiş veriler kaybolabilir.", "İşlemi sonlandır",
            [$"İşlem: {e.Name} (PID {e.Pid})", $"Konum: {e.PathText}", $"Yayıncı: {e.PublisherText}", $"Bellek: {e.MemoryText} · CPU: {e.CpuText}"]);
        if (!ok) return;
        var sw = Start();
        var (success, message) = await _service.EndAsync(e);
        Host.RecordToolOperation($"İşlem sonlandırma – {e.Name}", success ? CheckState.Healthy : CheckState.Error, message, sw.Elapsed, success ? null : message);
        await InformAsync(success ? "İşlem kapatıldı" : "İşlem kapatılamadı", message, !success);
        await SampleAsync();
    }
}

// ====================================================================== Güvenlik

public sealed class SecurityViewModel : ToolViewModel
{
    private readonly SecurityStatusService _service;

    public SecurityViewModel(IToolHost host) : base(host)
    {
        _service = new SecurityStatusService(host.Logger);
        OpenSecurityCommand = new RelayCommand(() => OpenWindowsUri("windowsdefender:"));
    }

    public ObservableCollection<CheckRowViewModel> Checks { get; } = [];
    public ObservableCollection<SecurityProduct> Products { get; } = [];
    public ICommand OpenSecurityCommand { get; }
    public CheckState Overall { get; private set; } = CheckState.NotChecked;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Güvenlik durumu okunuyor…";
        var r = await Task.Run(() => _service.ReadAsync(ct), ct);
        Checks.Clear();
        foreach (var c in r.Checks) Checks.Add(new CheckRowViewModel(c, Host.OpenSection));
        Products.Clear();
        foreach (var p in r.Products) Products.Add(p);
        Overall = r.Overall;
        OnPropertyChanged(nameof(Overall));
        var issues = r.Checks.Where(c => c.State is CheckState.Warning or CheckState.Error).ToList();
        StatusText = issues.Count == 0 ? "Sorun bulunmadı (yalnızca okuma yapıldı)" : $"{issues.Count} konu dikkat gerektiriyor";
        Host.ReportDiagnostic("security", new CheckResult("Güvenlik", r.Overall,
            issues.Count == 0 ? "Virüsten koruma ve güvenlik duvarı etkin" : string.Join(" · ", issues.Select(i => $"{i.Title}: {i.Summary}")), null, Nav.SystemTools, Nav.Security));
    }
}

// ====================================================================== Sistem Raporu

public sealed class ReportPartViewModel(ReportParts part, string title) : ObservableObject
{
    private bool _checked = true;
    public ReportParts Part { get; } = part;
    public string Title { get; } = title;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

public sealed class ReportViewModel : ToolViewModel
{
    private readonly SystemReportService _service;
    private readonly NetworkState _network;
    private ReportDocument? _document;
    private string _preview = string.Empty;
    private string? _savedPath;

    public ReportViewModel(IToolHost host, SystemReportService service, NetworkState network) : base(host)
    {
        _service = service;
        _network = network;
        Parts =
        [
            new(ReportParts.Windows, "Windows"), new(ReportParts.Hardware, "Donanım (CPU / GPU / RAM / Disk)"), new(ReportParts.Storage, "Depolama sağlığı"),
            new(ReportParts.Network, "Ağ"), new(ReportParts.Drivers, "Sürücüler"), new(ReportParts.Security, "Güvenlik"), new(ReportParts.Services, "Hizmetler"),
            new(ReportParts.Startup, "Başlangıç uygulamaları"), new(ReportParts.Events, "Olay günlüğü hataları"), new(ReportParts.Crashes, "Çökme kayıtları"),
            new(ReportParts.Battery, "Batarya")
        ];
        CreateCommand = new AsyncCommand(() => RunAsync(CreateAsync), () => !IsBusy && Parts.Any(p => p.IsChecked));
        SaveTextCommand = new AsyncCommand(() => SaveAsync("txt"), () => _document is not null && !IsBusy);
        SaveHtmlCommand = new AsyncCommand(() => SaveAsync("html"), () => _document is not null && !IsBusy);
        SaveJsonCommand = new AsyncCommand(() => SaveAsync("json"), () => _document is not null && !IsBusy);
        ShowSavedCommand = new RelayCommand(() => ShowInExplorer(_savedPath), () => _savedPath is not null);
        StatusText = "Rapor yalnızca bu bilgisayarda oluşturulur; kaydetmediğiniz sürece hiçbir yere yazılmaz ve gönderilmez.";
    }

    public IReadOnlyList<ReportPartViewModel> Parts { get; }
    public string MaskedText => PersonalDataMask.DescriptionText;
    public ICommand CreateCommand { get; }
    public ICommand SaveTextCommand { get; }
    public ICommand SaveHtmlCommand { get; }
    public ICommand SaveJsonCommand { get; }
    public ICommand ShowSavedCommand { get; }
    public string Preview { get => _preview; private set { Set(ref _preview, value); OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => _preview.Length > 0;
    public string? SavedPath { get => _savedPath; private set { Set(ref _savedPath, value); OnPropertyChanged(nameof(HasSaved)); } }
    public bool HasSaved => _savedPath is not null;

    protected override bool AutoLoad => false;
    protected override Task LoadAsync(CancellationToken ct) => CreateAsync(ct);

    private async Task CreateAsync(CancellationToken ct)
    {
        var sw = Start();
        var parts = Parts.Where(p => p.IsChecked).Aggregate(ReportParts.None, (a, p) => a | p.Part);
        var progress = new Progress<string>(t => StatusText = t);
        var doc = await Task.Run(() => _service.CollectAsync(parts, progress, ct, _network.Tests, _network.Dns), ct);
        _document = doc;
        Preview = SystemReportService.ToText(doc);
        SavedPath = null;
        var failed = doc.Sections.Count(s => s.Note?.StartsWith("Bu bölüm okunamadı", StringComparison.Ordinal) == true);
        StatusText = $"Rapor hazır: {doc.Sections.Count} bölüm · {sw.Elapsed.TotalSeconds:0.0} sn" + (failed > 0 ? $" · {failed} bölüm okunamadı" : "") + ". Kaydetmek için bir biçim seçin.";
        Host.RecordToolOperation("Sistem raporu oluşturma", failed > 0 ? CheckState.Warning : CheckState.Healthy, $"{doc.Sections.Count} bölüm", sw.Elapsed,
            failed > 0 ? string.Join("; ", doc.Sections.Where(s => s.Note?.StartsWith("Bu bölüm okunamadı", StringComparison.Ordinal) == true).Select(s => $"{s.Title}: {s.Note}")) : null);
    }

    private async Task SaveAsync(string format)
    {
        if (_document is not { } doc) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Sistem raporunu kaydet",
            FileName = $"E-mre-Sistem-Raporu-{doc.CreatedAt:yyyyMMdd-HHmm}.{format}",
            Filter = format switch { "html" => "HTML dosyası (*.html)|*.html", "json" => "JSON dosyası (*.json)|*.json", _ => "Metin dosyası (*.txt)|*.txt" },
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        var content = format switch { "html" => SystemReportService.ToHtml(doc), "json" => SystemReportService.ToJson(doc), _ => SystemReportService.ToText(doc) };
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, content, new System.Text.UTF8Encoding(true));
            var size = new FileInfo(dialog.FileName).Length;
            SavedPath = dialog.FileName;
            StatusText = $"Kaydedildi: {dialog.FileName} ({Formats.Bytes(size)})";
            Logger.Info($"Sistem raporu kaydedildi: {dialog.FileName} ({size} bayt)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = "Rapor kaydedilemedi: " + ex.Message;
            Logger.Error(ErrorText);
        }
    }
}

// ====================================================================== Destek Paketi

public sealed class SupportItemViewModel(SupportItem item) : ObservableObject
{
    private bool _checked = true;
    public SupportItem Item { get; } = item;
    public string Title => Item.Title;
    public string Description => Item.Description;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

public sealed class SupportPackageViewModel : ToolViewModel
{
    private readonly SupportPackageService _service;
    private readonly Func<IReadOnlyList<OperationRecord>> _history;
    private SupportPackageResult? _result;

    public SupportPackageViewModel(IToolHost host, SupportPackageService service, Func<IReadOnlyList<OperationRecord>> history) : base(host)
    {
        _service = service;
        _history = history;
        Items = SupportPackageService.Items.Select(i => new SupportItemViewModel(i)).ToList();
        CreateCommand = new AsyncCommand(CreateAsync, () => !IsBusy && Items.Any(i => i.IsChecked));
        ShowCommand = new RelayCommand(() => ShowInExplorer(_result?.Path), () => _result?.Path is not null);
        StatusText = "Paket yalnızca seçtiğiniz konuma kaydedilir; hiçbir yere gönderilmez.";
    }

    public IReadOnlyList<SupportItemViewModel> Items { get; }
    public string MaskedText => PersonalDataMask.DescriptionText;
    public ICommand CreateCommand { get; }
    public ICommand ShowCommand { get; }
    public string ResultText => _result is null ? string.Empty
        : _result.Success ? $"{_result.Message}\n{_result.Path}\n{_result.Entries.Count} dosya · {Formats.Bytes(_result.Size)}" : _result.Message;
    public bool HasResult => _result is not null;
    public bool ResultOk => _result?.Success == true;
    public override bool ShowRefresh => false;

    protected override bool AutoLoad => false;
    protected override Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CreateAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Destek paketini kaydet",
            FileName = $"E-mre-Destek-Paketi-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "ZIP arşivi (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        var keys = Items.Where(i => i.IsChecked).Select(i => i.Item.Key).ToList();
        var sw = Start();
        await RunAsync(async ct =>
        {
            var progress = new Progress<string>(t => StatusText = t);
            var r = await Task.Run(() => _service.CreateAsync(dialog.FileName, keys, _history(), progress, ct), ct);
            _result = r;
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ResultOk));
            StatusText = r.Success ? "Destek paketi hazır." : "Destek paketi oluşturulamadı.";
            if (!r.Success) ErrorText = r.Message;
            Host.RecordToolOperation("Destek paketi oluşturma", r.Success ? CheckState.Healthy : CheckState.Error,
                r.Success ? $"{r.Entries.Count} dosya, {Formats.Bytes(r.Size)}" : r.Message, sw.Elapsed, r.Success ? null : r.Message);
        });
    }
}

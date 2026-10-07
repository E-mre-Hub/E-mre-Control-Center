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
    private string _countsText = L.T("Tanılama henüz çalıştırılmadı.", "Diagnosis has not run yet.");

    public DiagnoseViewModel(IToolHost host, DiagnosticOrchestrator orchestrator, NetworkState network) : base(host)
    {
        _orchestrator = orchestrator;
        _network = network;
        foreach (var s in DiagnosticOrchestrator.Steps)
            Steps.Add(new CheckRowViewModel(new CheckResult(s.Title, CheckState.NotChecked, L.T("Bekliyor", "Waiting"), null, s.TargetCategory, s.TargetSection), host.OpenSection, s.Index));
        StartCommand = new AsyncCommand(() => RunAsync(DiagnoseAsync), () => !IsBusy && Host.CanStartTool);
        StatusText = L.T("10 gerçek kontrol sırayla çalışır; hiçbiri sistemde değişiklik yapmaz.", "10 real checks run one after another; none of them changes the system.");
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
            row.Update(row.Result with { State = CheckState.NotChecked, Summary = L.T("Bekliyor", "Waiting"), Detail = null });
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
                row.Update(row.Result with { State = CheckState.Skipped, Summary = L.T("İptal edildiği için çalıştırılmadı", "Not run because it was cancelled") });
            Host.RecordToolOperation(L.T("Tek Tıkla Tanıla", "One-Click Diagnosis"), CheckState.Skipped, L.T("İptal edildi", "Cancelled"), sw.Elapsed, null, cancelled: true);
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
        StatusText = L.T($"Tamamlandı · {sw.Elapsed.TotalSeconds:0.0} sn · {DateTime.Now:HH:mm:ss}", $"Completed · {sw.Elapsed.TotalSeconds:0.0} sec · {DateTime.Now:HH:mm:ss}");
        var worst = CheckStates.Worst(results.Select(r => r.Result.State));
        Host.RecordToolOperation(L.T("Tek Tıkla Tanıla", "One-Click Diagnosis"), worst, CountsText, sw.Elapsed,
            Errors > 0 ? string.Join("; ", results.Where(r => r.Result.State == CheckState.Error).Select(r => $"{r.Step.Title}: {r.Result.Summary}")) : null);
    }

    private void RaiseCounts()
    {
        CountsText = Total == 0 ? L.T("Tanılama çalışıyor…", "Diagnosis running…")
            : L.T($"Toplam {Total} kontrol · {Passed} başarılı · {Warnings} uyarı · {Errors} hata · {SkippedCount} atlandı", $"Total {Total} checks · {Passed} passed · {Warnings} warning(s) · {Errors} error(s) · {SkippedCount} skipped") + (Unknown > 0 ? L.T($" · {Unknown} kontrol edilemedi", $" · {Unknown} could not be checked") : "");
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
    public string ToggleText => _selected?.Enabled == false ? L.T("Etkinleştir…", "Enable…") : L.T("Devre dışı bırak…", "Disable…");
    public string SelectionNote => _selected is null ? L.T("Bir kayıt seçin.", "Select an entry.")
        : _selected.ProtectedReason ?? _selected.Note ?? (_selected.NeedsAdmin && !Host.IsAdmin ? L.T("Tüm kullanıcılar için olan kayıt yönetici yetkisi gerektirir.", "Entries for all users require administrator rights.") : _selected.Command);

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Başlangıç kayıtları okunuyor…", "Reading startup entries…");
        var scan = await Task.Run(() => _service.ScanAsync(ct), ct);
        var keep = _selected;
        Entries.Clear();
        foreach (var e in scan.Entries) Entries.Add(e);
        Selected = Entries.FirstOrDefault(e => keep is not null && e.Name == keep.Name && e.Source == keep.Source);
        if (scan.Errors.Count > 0) ErrorText = string.Join(" ", scan.Errors);
        StatusText = L.T($"{scan.Entries.Count} kayıt · {scan.Entries.Count(e => e.Enabled)} etkin", $"{scan.Entries.Count} entries · {scan.Entries.Count(e => e.Enabled)} enabled");
    }

    /// <summary>Kullanıcı onayıyla, Görev Yöneticisi'nin yöntemiyle (StartupApproved) durum değişikliği; kayıt silinmez.</summary>
    private async Task ToggleAsync()
    {
        if (_selected is not { Toggleable: true } e) return;
        var enable = !e.Enabled;
        var ok = await ConfirmAsync(enable ? L.T("Başlangıçta etkinleştirilsin mi?", "Enable at startup?") : L.T("Başlangıçtan devre dışı bırakılsın mı?", "Disable at startup?"),
            (enable ? L.T("Uygulama bir sonraki oturum açılışında yeniden otomatik başlayacak.", "The app will start automatically again at the next sign-in.") : L.T("Uygulama bir sonraki oturum açılışında otomatik başlamayacak.", "The app will not start automatically at the next sign-in.")) +
            L.T(" Başlangıç kaydı SİLİNMEZ; yalnızca Windows'un Görev Yöneticisi'nde de kullandığı durum değeri değiştirilir ve istediğiniz zaman geri alınabilir.", " The startup entry is NOT DELETED; only the state value that Windows Task Manager also uses is changed, and you can undo it at any time."),
            enable ? L.T("Etkinleştir", "Enable") : L.T("Devre dışı bırak", "Disable"), [$"Ad: {e.Name}", L.T($"Kaynak: {e.SourceText}", $"Source: {e.SourceText}"), L.T($"Yayıncı: {e.PublisherText}", $"Publisher: {e.PublisherText}"), L.T($"Komut: {e.Command}", $"Command: {e.Command}")], warning: !enable);
        if (!ok) return;
        var sw = Start();
        var (success, message) = await Task.Run(() => _service.SetEnabled(e, enable));
        Host.RecordToolOperation(enable ? L.T("Başlangıç kaydını etkinleştirme", "Enable startup entry") : L.T("Başlangıç kaydını devre dışı bırakma", "Disable startup entry"), success ? CheckState.Healthy : CheckState.Error,
            $"{e.Name}: {message}", sw.Elapsed, success ? null : message);
        await InformAsync(success ? L.T("Değişiklik uygulandı", "Change applied") : L.T("Değişiklik yapılamadı", "Change could not be made"), message, !success);
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
    public string SelectionNote => !Host.IsAdmin ? L.T("Hizmet başlatma / durdurma yönetici yetkisi gerektirir.", "Starting / stopping services requires administrator rights.")
        : _selected is null ? L.T("Bir hizmet seçin.", "Select a service.")
        : _selected.IsRunning && !_selected.CanStop ? _selected.StopBlockedText
        : _selected.IsDisabled ? L.T("Hizmet devre dışı; başlangıç türü bu uygulamadan değiştirilmez.", "The service is disabled; its startup type is not changed from this app.")
        : _selected.DescriptionText;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Windows hizmetleri okunuyor…", "Reading Windows services…");
        var scan = await Task.Run(() => _service.ListAsync(ct), ct);
        var keep = _selected?.Name;
        Services.Clear();
        foreach (var s in scan.Services) Services.Add(s);
        Selected = Services.FirstOrDefault(s => s.Name == keep);
        if (scan.Error is not null) ErrorText = scan.Error;
        StatusText = L.T($"{scan.Services.Count} hizmet · {scan.Services.Count(s => s.IsRunning)} çalışıyor", $"{scan.Services.Count} services · {scan.Services.Count(s => s.IsRunning)} running");
    }

    private async Task ActAsync(ServiceAction action)
    {
        if (_selected is not { } s) return;
        var (title, verb) = action switch
        {
            ServiceAction.Start => (L.T("Hizmet başlatılsın mı?", "Start the service?"), L.T("Başlat", "Start")),
            ServiceAction.Stop => (L.T("Hizmet durdurulsun mu?", "Stop the service?"), L.T("Durdur", "Stop")),
            _ => (L.T("Hizmet yeniden başlatılsın mı?", "Restart the service?"), L.T("Yeniden başlat", "Restart"))
        };
        var ok = await ConfirmAsync(title,
            action == ServiceAction.Start ? L.T("Hizmet Windows Hizmet Denetimi Yöneticisi ile başlatılacak; başlangıç türü değişmez.", "The service will be started with the Windows Service Control Manager; its startup type does not change.")
                : L.T("Hizmeti kullanan uygulamalar etkilenebilir. Kritik Windows hizmetleri bu uygulamadan durdurulamaz; başlangıç türü değişmez.", "Apps using the service may be affected. Critical Windows services cannot be stopped from this app; the startup type does not change."),
            verb, [L.T($"Hizmet: {s.DisplayName} ({s.Name})", $"Service: {s.DisplayName} ({s.Name})"), L.T($"Durum: {s.StateText} · Başlangıç: {s.StartModeText}", $"Status: {s.StateText} · Startup: {s.StartModeText}"), L.T($"Yayıncı: {s.PublisherText}", $"Publisher: {s.PublisherText}"), L.T($"Dosya: {s.PathText}", $"File: {s.PathText}")],
            warning: action != ServiceAction.Start);
        if (!ok) return;
        var sw = Start();
        (bool Success, string Message) result = (false, "");
        await RunAsync(async ct => result = await _service.RunAsync(s, action, ct));
        Host.RecordToolOperation(L.T($"Hizmet: {verb.ToLowerInvariant()} – {s.DisplayName}", $"Service: {verb.ToLowerInvariant()} – {s.DisplayName}"), result.Success ? CheckState.Healthy : CheckState.Error, result.Message, sw.Elapsed,
            result.Success ? null : result.Message);
        await InformAsync(result.Success ? L.T("İşlem tamamlandı", "Operation completed") : L.T("İşlem başarısız", "Operation failed"), $"{s.DisplayName}: {result.Message}", !result.Success);
        await RefreshAsync();
    }

    private void OpenConsole()
    {
        try
        {
            var mmc = Path.Combine(Environment.SystemDirectory, "mmc.exe");
            // Yönetici olarak çalışırken doğrudan CreateProcess (ShellExecute'un kullanıcı kayıt defterindeki ilişkilendirmelerine
            // bakılmaz); yönetici değilken ShellExecute, mmc'nin kendi yükseltme isteğini (UAC) gösterebilsin.
            var psi = new System.Diagnostics.ProcessStartInfo(mmc) { UseShellExecute = !AdminPrivilegeManager.IsElevated };
            psi.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "services.msc"));
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warning(L.T("Hizmetler konsolu açılamadı: ", "Could not open the Services console: ") + ex.Message);
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
    public string SelectionNote => _selected is null ? L.T("Bir işlem seçin.", "Select a process.") : _selected.ProtectedReason ?? _selected.Path;

    /// <summary>Örnekleme yalnızca bu ekran açıkken 2 saniyede bir (ekran kapanınca durur).</summary>
    public override void Activate()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += async (_, _) => await SampleAsync();
        _timer.Start();
        Logger.Info(L.T("İşlemler: izleme başladı (2 saniyede bir).", "Processes: monitoring started (every 2 seconds)."));
        _ = SampleAsync();
    }

    public override void Deactivate()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
        _service.Reset();
        Logger.Info(L.T("İşlemler: izleme durdu.", "Processes: monitoring stopped."));
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
            TotalsText = L.T($"{snap.Processes.Count} işlem · toplam CPU %{snap.TotalCpuPercent:0} · {DateTime.Now:HH:mm:ss}", $"{snap.Processes.Count} processes · total CPU {snap.TotalCpuPercent:0}% · {DateTime.Now:HH:mm:ss}");
            StatusText = L.T("Canlı · 2 saniyede bir güncellenir", "Live · updated every 2 seconds");
        }
        catch (Exception ex)
        {
            ErrorText = L.T("İşlem listesi okunamadı: ", "Could not read the process list: ") + ex.Message;
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
        var ok = await ConfirmAsync(L.T("İşlem sonlandırılsın mı?", "End the process?"),
            L.T("Önce normal kapatma istenir; yanıt vermezse işlem sonlandırılır. Kaydedilmemiş veriler kaybolabilir.", "A normal close is requested first; if it does not respond, the process is ended. Unsaved data may be lost."), L.T("İşlemi sonlandır", "End process"),
            [L.T($"İşlem: {e.Name} (PID {e.Pid})", $"Process: {e.Name} (PID {e.Pid})"), L.T($"Konum: {e.PathText}", $"Location: {e.PathText}"), L.T($"Yayıncı: {e.PublisherText}", $"Publisher: {e.PublisherText}"), L.T($"Bellek: {e.MemoryText} · CPU: {e.CpuText}", $"Memory: {e.MemoryText} · CPU: {e.CpuText}")]);
        if (!ok) return;
        var sw = Start();
        var (success, message) = await _service.EndAsync(e);
        Host.RecordToolOperation(L.T($"İşlem sonlandırma – {e.Name}", $"End process – {e.Name}"), success ? CheckState.Healthy : CheckState.Error, message, sw.Elapsed, success ? null : message);
        await InformAsync(success ? L.T("İşlem kapatıldı", "Process closed") : L.T("İşlem kapatılamadı", "Process could not be closed"), message, !success);
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
        StatusText = L.T("Güvenlik durumu okunuyor…", "Reading security status…");
        var r = await Task.Run(() => _service.ReadAsync(ct), ct);
        Checks.Clear();
        foreach (var c in r.Checks) Checks.Add(new CheckRowViewModel(c, Host.OpenSection));
        Products.Clear();
        foreach (var p in r.Products) Products.Add(p);
        Overall = r.Overall;
        OnPropertyChanged(nameof(Overall));
        var issues = r.Checks.Where(c => c.State is CheckState.Warning or CheckState.Error).ToList();
        StatusText = issues.Count == 0 ? L.T("Sorun bulunmadı (yalnızca okuma yapıldı)", "No problems found (read-only)") : L.T($"{issues.Count} konu dikkat gerektiriyor", $"{issues.Count} item(s) need attention");
        Host.ReportDiagnostic("security", new CheckResult(L.T("Güvenlik", "Security"), r.Overall,
            issues.Count == 0 ? L.T("Virüsten koruma ve güvenlik duvarı etkin", "Antivirus and firewall are on") : string.Join(" · ", issues.Select(i => $"{i.Title}: {i.Summary}")), null, Nav.SystemTools, Nav.Security));
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
            new(ReportParts.Windows, "Windows"), new(ReportParts.Hardware, L.T("Donanım (CPU / GPU / RAM / Disk)", "Hardware (CPU / GPU / RAM / Disk)")), new(ReportParts.Storage, L.T("Depolama sağlığı", "Storage health")),
            new(ReportParts.Network, L.T("Ağ", "Network")), new(ReportParts.Drivers, L.T("Sürücüler", "Drivers")), new(ReportParts.Security, L.T("Güvenlik", "Security")), new(ReportParts.Services, L.T("Hizmetler", "Services")),
            new(ReportParts.Startup, L.T("Başlangıç uygulamaları", "Startup apps")), new(ReportParts.Events, L.T("Olay günlüğü hataları", "Event log errors")), new(ReportParts.Crashes, L.T("Çökme kayıtları", "Crash records")),
            new(ReportParts.Battery, L.T("Batarya", "Battery"))
        ];
        CreateCommand = new AsyncCommand(() => RunAsync(CreateAsync), () => !IsBusy && Parts.Any(p => p.IsChecked));
        SaveTextCommand = new AsyncCommand(() => SaveAsync("txt"), () => _document is not null && !IsBusy);
        SaveHtmlCommand = new AsyncCommand(() => SaveAsync("html"), () => _document is not null && !IsBusy);
        SaveJsonCommand = new AsyncCommand(() => SaveAsync("json"), () => _document is not null && !IsBusy);
        ShowSavedCommand = new RelayCommand(() => ShowInExplorer(_savedPath), () => _savedPath is not null);
        StatusText = L.T("Rapor yalnızca bu bilgisayarda oluşturulur; kaydetmediğiniz sürece hiçbir yere yazılmaz ve gönderilmez.", "The report is created only on this computer; it is not written anywhere or sent unless you save it.");
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
        var failed = doc.Sections.Count(s => s.Note?.StartsWith(L.T("Bu bölüm okunamadı", "This section could not be read"), StringComparison.Ordinal) == true);
        StatusText = L.T($"Rapor hazır: {doc.Sections.Count} bölüm · {sw.Elapsed.TotalSeconds:0.0} sn", $"Report ready: {doc.Sections.Count} sections · {sw.Elapsed.TotalSeconds:0.0} sec") + (failed > 0 ? L.T($" · {failed} bölüm okunamadı", $" · {failed} section(s) could not be read") : "") + L.T(". Kaydetmek için bir biçim seçin.", ". Choose a format to save it.");
        Host.RecordToolOperation(L.T("Sistem raporu oluşturma", "Create system report"), failed > 0 ? CheckState.Warning : CheckState.Healthy, L.T($"{doc.Sections.Count} bölüm", $"{doc.Sections.Count} sections"), sw.Elapsed,
            failed > 0 ? string.Join("; ", doc.Sections.Where(s => s.Note?.StartsWith(L.T("Bu bölüm okunamadı", "This section could not be read"), StringComparison.Ordinal) == true).Select(s => $"{s.Title}: {s.Note}")) : null);
    }

    private async Task SaveAsync(string format)
    {
        if (_document is not { } doc) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Sistem raporunu kaydet", "Save the system report"),
            FileName = L.T($"E-mre-Sistem-Raporu-{doc.CreatedAt:yyyyMMdd-HHmm}.{format}", $"E-mre-System-Report-{doc.CreatedAt:yyyyMMdd-HHmm}.{format}"),
            Filter = format switch { "html" => L.T("HTML dosyası (*.html)|*.html", "HTML file (*.html)|*.html"), "json" => L.T("JSON dosyası (*.json)|*.json", "JSON file (*.json)|*.json"), _ => L.T("Metin dosyası (*.txt)|*.txt", "Text file (*.txt)|*.txt") },
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
            StatusText = L.T($"Kaydedildi: {dialog.FileName} ({Formats.Bytes(size)})", $"Saved: {dialog.FileName} ({Formats.Bytes(size)})");
            Logger.Info(L.T($"Sistem raporu kaydedildi: {dialog.FileName} ({size} bayt)", $"System report saved: {dialog.FileName} ({size} bytes)"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = L.T("Rapor kaydedilemedi: ", "Could not save the report: ") + ex.Message;
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
        StatusText = L.T("Paket yalnızca seçtiğiniz konuma kaydedilir; hiçbir yere gönderilmez.", "The package is saved only to the location you choose; it is not sent anywhere.");
    }

    public IReadOnlyList<SupportItemViewModel> Items { get; }
    public string MaskedText => PersonalDataMask.DescriptionText;
    public ICommand CreateCommand { get; }
    public ICommand ShowCommand { get; }
    public string ResultText => _result is null ? string.Empty
        : _result.Success ? L.T($"{_result.Message}\n{_result.Path}\n{_result.Entries.Count} dosya · {Formats.Bytes(_result.Size)}", $"{_result.Message}\n{_result.Path}\n{_result.Entries.Count} files · {Formats.Bytes(_result.Size)}") : _result.Message;
    public bool HasResult => _result is not null;
    public bool ResultOk => _result?.Success == true;
    public override bool ShowRefresh => false;

    protected override bool AutoLoad => false;
    protected override Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CreateAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Destek paketini kaydet", "Save the support package"),
            FileName = L.T($"E-mre-Destek-Paketi-{DateTime.Now:yyyyMMdd-HHmm}.zip", $"E-mre-Support-Package-{DateTime.Now:yyyyMMdd-HHmm}.zip"),
            Filter = L.T("ZIP arşivi (*.zip)|*.zip", "ZIP archive (*.zip)|*.zip"),
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
            StatusText = r.Success ? L.T("Destek paketi hazır.", "Support package ready.") : L.T("Destek paketi oluşturulamadı.", "Support package could not be created.");
            if (!r.Success) ErrorText = r.Message;
            Host.RecordToolOperation(L.T("Destek paketi oluşturma", "Create support package"), r.Success ? CheckState.Healthy : CheckState.Error,
                r.Success ? L.T($"{r.Entries.Count} dosya, {Formats.Bytes(r.Size)}", $"{r.Entries.Count} files, {Formats.Bytes(r.Size)}") : r.Message, sw.Elapsed, r.Success ? null : r.Message);
        });
    }
}

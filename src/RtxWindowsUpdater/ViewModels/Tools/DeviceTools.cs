using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services.Diagnostics;

namespace RtxWindowsUpdater.ViewModels.Tools;

// ====================================================================== Sürücüler

public sealed class DriversViewModel : ToolViewModel
{
    private readonly DriverService _service;
    private string _filter = "important";
    private string _search = string.Empty;
    private string _updateStatus = "Windows Update sürücü kataloğunda arama yapılmadı.";
    private DriverScan? _scan;

    public DriversViewModel(IToolHost host) : base(host)
    {
        _service = new DriverService(host.Logger);
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = o => o is DriverInfo d && PassesFilter(d);
        SearchUpdatesCommand = new AsyncCommand(() => RunAsync(SearchUpdatesAsync), () => !IsBusy);
        OpenOptionalUpdatesCommand = new RelayCommand(() => OpenWindowsUri("ms-settings:windowsupdate-optionalupdates"));
        OpenNvidiaCommand = new RelayCommand(() => Host.OpenSection(Nav.Update, Nav.Cards));
    }

    public ObservableCollection<DriverInfo> Drivers { get; } = [];
    public ICollectionView DriversView { get; }
    public ObservableCollection<DriverUpdate> Updates { get; } = [];
    public ICommand SearchUpdatesCommand { get; }
    public ICommand OpenOptionalUpdatesCommand { get; }
    public ICommand OpenNvidiaCommand { get; }
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool HasUpdates => Updates.Count > 0;
    public string CountsText => _scan is null ? "—" : $"{_scan.Drivers.Count} sürücü · {_scan.Drivers.Count(d => d.Important)} önemli aygıt · {_scan.ProblemCount} sorunlu";

    /// <summary>important / problems / all.</summary>
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) DriversView.Refresh(); } }
    public string SearchText { get => _search; set { if (Set(ref _search, (value ?? "").Trim())) DriversView.Refresh(); } }

    private bool PassesFilter(DriverInfo d) =>
        (_filter switch { "problems" => d.State is CheckState.Error or CheckState.Warning, "all" => true, _ => d.Important || d.State != CheckState.Healthy }) &&
        (_search.Length == 0 || TextSearch.Contains(d.DeviceName, _search) || TextSearch.Contains(d.Manufacturer, _search) ||
         TextSearch.Contains(d.Category, _search) || TextSearch.Contains(d.Provider, _search));

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Sürücüler okunuyor…";
        var scan = await Task.Run(() => _service.ScanAsync(ct), ct);
        _scan = scan;
        Drivers.Clear();
        foreach (var d in scan.Drivers.OrderByDescending(d => d.State is CheckState.Error or CheckState.Warning).ThenBy(d => d.Category).ThenBy(d => d.DeviceName))
            Drivers.Add(d);
        if (scan.Error is not null) ErrorText = "Sürücü bilgisi alınamadı. " + scan.Error;
        OnPropertyChanged(nameof(CountsText));
        StatusText = CountsText;
        var problems = scan.Drivers.Where(d => d.State is CheckState.Error or CheckState.Warning).ToList();
        Host.ReportDiagnostic("drivers", new CheckResult("Sürücüler",
            scan.Error is not null && scan.Drivers.Count == 0 ? CheckState.Unknown : problems.Any(p => p.State == CheckState.Error) ? CheckState.Error : problems.Count > 0 ? CheckState.Warning : CheckState.Healthy,
            scan.Error ?? (problems.Count == 0 ? "Sorunlu aygıt yok" : $"{problems.Count} aygıtta sorun"), null, Nav.Update, Nav.Drivers));
    }

    /// <summary>Yalnızca ARAMA: Windows Update'in resmî sürücü kataloğu. Kurulum Windows Ayarlar → İsteğe bağlı güncellemeler'den yapılır.</summary>
    private async Task SearchUpdatesAsync(CancellationToken ct)
    {
        var sw = Start();
        UpdateStatus = "Windows Update sürücü kataloğu aranıyor (birkaç dakika sürebilir)…";
        var r = await Task.Run(() => _service.SearchWindowsUpdateAsync(ct), ct);
        Updates.Clear();
        foreach (var u in r.Updates) Updates.Add(u);
        OnPropertyChanged(nameof(HasUpdates));
        UpdateStatus = r.Error is not null ? "Arama başarısız: " + r.Error
            : r.Updates.Count == 0 ? $"Windows Update bu cihaz için yeni sürücü bildirmedi ({DateTime.Now:HH:mm})."
            : $"Windows Update {r.Updates.Count} sürücü güncellemesi bildirdi. Kurmak için Windows'un İsteğe bağlı güncellemeler sayfasını kullanın.";
        Host.RecordToolOperation("Sürücü güncelleme denetimi (Windows Update)", r.Error is not null ? CheckState.Error : r.Updates.Count > 0 ? CheckState.Warning : CheckState.Healthy,
            r.Error is not null ? "Arama başarısız" : r.Updates.Count == 0 ? "Yeni sürücü bulunmadı" : $"{r.Updates.Count} sürücü güncellemesi bulundu", sw.Elapsed, r.Error);
    }
}

// ====================================================================== Uygulamalar

public sealed class AppRowViewModel(InstalledApp app) : ObservableObject
{
    private string? _update;
    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string Location => App.LocationText;
    public string InstallDate => App.InstallDateText;
    public string Source => App.Source;
    public string Size => App.SizeText;
    public string? UpdateText { get => _update; set { Set(ref _update, value); OnPropertyChanged(nameof(HasUpdate)); } }
    public bool HasUpdate => !string.IsNullOrEmpty(_update);
}

public sealed class AppsViewModel : ToolViewModel
{
    private readonly ApplicationService _service;
    private readonly ICardResultSource _cards;
    private string _search = string.Empty;
    private string _source = "all";
    private string _wingetStatus = "Winget güncelleme denetimi bu ekranda henüz çalıştırılmadı.";

    public AppsViewModel(IToolHost host, ICardResultSource cards) : base(host)
    {
        _service = new ApplicationService(host.Logger);
        _cards = cards;
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = o => o is AppRowViewModel a &&
            (_source switch { "desktop" => a.Source != "Microsoft Store", "store" => a.Source == "Microsoft Store", "updates" => a.HasUpdate, _ => true }) &&
            (_search.Length == 0 || TextSearch.Contains(a.Name, _search) || TextSearch.Contains(a.Publisher, _search));
        CheckWingetCommand = new AsyncCommand(CheckWingetAsync, () => !IsBusy && Host.CanStartTool && Host.IsAdmin);
        UninstallCommand = new AsyncCommand(UninstallAsync);
        OpenLocationCommand = new RelayCommand(p => { if (p is AppRowViewModel a) ShowInExplorer(a.App.InstallLocation); });
        OpenUpdatesCommand = new RelayCommand(() => Host.OpenSection(Nav.Update, Nav.Cards));
    }

    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public ICollectionView AppsView { get; }
    public ICommand CheckWingetCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand OpenUpdatesCommand { get; }
    public string WingetStatus { get => _wingetStatus; private set => Set(ref _wingetStatus, value); }
    public string WingetHint => Host.IsAdmin ? "Denetim mevcut Winget kartıyla yapılır (yalnızca kontrol; güncelleme Güncelleme ekranından, onayınızla)."
        : "Winget denetimi yönetici yetkisi gerektirir (Genel Ayarlar → Yönetici Yetkisi).";
    public string SearchText { get => _search; set { if (Set(ref _search, (value ?? "").Trim())) AppsView.Refresh(); } }
    /// <summary>all / desktop / store / updates.</summary>
    public string SourceFilter { get => _source; set { if (Set(ref _source, value)) AppsView.Refresh(); } }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Kurulu uygulamalar okunuyor…";
        var scan = await Task.Run(() => _service.ListAsync(true, ct), ct);
        Apps.Clear();
        foreach (var a in scan.Apps) Apps.Add(new AppRowViewModel(a));
        if (scan.StoreError is not null) ErrorText = scan.StoreError;
        ApplyWinget(_cards.LastCardResult(ComponentKeys.Winget));
        StatusText = $"{scan.Apps.Count} uygulama ({scan.Apps.Count(a => a.Source != "Microsoft Store")} masaüstü, {scan.Apps.Count(a => a.Source == "Microsoft Store")} Microsoft Store)";
    }

    private async Task CheckWingetAsync()
    {
        WingetStatus = "Winget ile güncellemeler kontrol ediliyor…";
        var r = await _cards.CheckCardForToolAsync(ComponentKeys.Winget);
        if (r is null)
        {
            WingetStatus = "Winget denetimi başlatılamadı (başka bir işlem sürüyor veya yönetici yetkisi yok).";
            return;
        }
        ApplyWinget(r);
    }

    /// <summary>Winget kartının GERÇEK sonucundaki paketler ada göre eşlenir (eşleşmeyen güncellemeler yine sayılır).</summary>
    private void ApplyWinget(ModuleResult? r)
    {
        foreach (var a in Apps) a.UpdateText = null;
        if (r is null) return;
        if (r.Status is ComponentStatus.CheckFailed or ComponentStatus.Failed)
        {
            WingetStatus = "Winget denetimi başarısız: " + (r.Reason ?? r.Summary);
            return;
        }
        var items = r.Items.Where(i => i.UpdateAvailable).ToList();
        var matched = 0;
        foreach (var i in items)
        {
            var row = Apps.FirstOrDefault(a => string.Equals(a.Name, i.Name, StringComparison.OrdinalIgnoreCase))
                      ?? Apps.FirstOrDefault(a => a.Name.StartsWith(i.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            row.UpdateText = $"{i.CurrentVersion} → {i.NewVersion}";
            matched++;
        }
        WingetStatus = items.Count == 0 ? $"Winget güncelleme bildirmedi ({r.CompletedAt:HH:mm})."
            : $"Winget {items.Count} güncelleme bildirdi ({matched} tanesi listede eşleşti). Uygulamak için Güncelleme → Winget kartını kullanın.";
        AppsView.Refresh();
    }

    /// <summary>Sessiz kaldırma YOK: Windows'un kendi Uygulamalar sayfası açılır, kaldırmayı kullanıcı orada yapar.</summary>
    private async Task UninstallAsync()
    {
        var ok = await ConfirmAsync("Uygulama kaldırma",
            "Bu uygulama hiçbir programı kendiliğinden kaldırmaz. Windows Ayarlar → Uygulamalar → Yüklü uygulamalar sayfası açılacak; " +
            "kaldırmak istediğiniz uygulamayı orada seçip Windows'un kendi kaldırıcısıyla kaldırabilirsiniz.", "Ayarlar'ı aç", warning: false);
        if (ok) OpenWindowsUri("ms-settings:appsfeatures");
    }
}

// ====================================================================== Depolama Analizi

/// <summary>Taranabilecek kök (sürücü veya seçilen klasör); radyo çipiyle seçilir.</summary>
public sealed class RootOption(string path, Action<RootOption> selected) : ObservableObject
{
    private bool _isSelected;
    public string Path { get; } = path;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) selected(this);
        }
    }
}

public sealed class StorageAnalysisViewModel : ToolViewModel
{
    private string _view = "files";
    private readonly LargeFileAnalyzer _analyzer;
    private string? _root;
    private AnalysisResult? _result;
    private string _progressText = string.Empty;
    private FileEntry? _selectedFile;

    public StorageAnalysisViewModel(IToolHost host) : base(host)
    {
        _analyzer = new LargeFileAnalyzer(host.Logger);
        StatusText = "Taranacak sürücüyü veya klasörü seçin.";
        ScanCommand = new AsyncCommand(() => RunAsync(ScanAsync), () => !IsBusy && _root is not null);
        PickFolderCommand = new RelayCommand(PickFolder, () => !IsBusy);
        RecycleCommand = new AsyncCommand(RecycleAsync, () => !IsBusy && _selectedFile is not null);
        ShowFileCommand = new RelayCommand(p => ShowInExplorer((p as FileEntry)?.Path ?? (p as FolderEntry)?.Path ?? _selectedFile?.Path));
        foreach (var v in StorageHealthService.ReadVolumes()) AddRoot(v.Letter + "\\");
        if (Roots.FirstOrDefault() is { } first) first.IsSelected = true;
    }

    public ObservableCollection<RootOption> Roots { get; } = [];
    /// <summary>files / folders / types – hangi tablonun gösterileceği.</summary>
    public string View { get => _view; set => Set(ref _view, value); }
    public ObservableCollection<FileEntry> Files { get; } = [];
    public ObservableCollection<FolderEntry> Folders { get; } = [];
    public ObservableCollection<TypeEntry> Types { get; } = [];
    public ICommand ScanCommand { get; }
    public ICommand PickFolderCommand { get; }
    public ICommand RecycleCommand { get; }
    public ICommand ShowFileCommand { get; }

    public string? Root { get => _root; set { if (Set(ref _root, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public bool HasResult => _result is not null;
    public string SummaryText => _result is null ? string.Empty
        : $"{_result.Root}: {Formats.Bytes(_result.TotalSize)} · {_result.FileCount:N0} dosya · {_result.FolderCount:N0} klasör" +
          (_result.Skipped > 0 ? $" · erişilemeyen {_result.Skipped:N0} öğe atlandı" : "") + (_result.Cancelled ? " · İPTAL EDİLDİ (kısmi sonuç)" : "") +
          $" · {_result.Duration.TotalSeconds:0.0} sn";
    public string VolumeText => _result?.Volume is { } v ? $"{v.Letter} bölümü: {v.UsageText}" : string.Empty;
    public FileEntry? SelectedFile { get => _selectedFile; set { Set(ref _selectedFile, value); CommandManager.InvalidateRequerySuggested(); } }

    private RootOption AddRoot(string path)
    {
        var option = new RootOption(path, o =>
        {
            foreach (var r in Roots.Where(r => !ReferenceEquals(r, o))) r.IsSelected = false;
            Root = o.Path;
        });
        Roots.Add(option);
        return option;
    }

    protected override bool AutoLoad => false;
    protected override Task LoadAsync(CancellationToken ct) => ScanAsync(ct);

    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Taranacak klasörü seçin", Multiselect = false };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        var option = Roots.FirstOrDefault(r => string.Equals(r.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)) ?? AddRoot(dialog.FolderName);
        option.IsSelected = true;
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        if (_root is null) return;
        var sw = Start();
        StatusText = "Taranıyor: " + _root;
        var progress = new Progress<ScanProgress>(p => ProgressText = $"{p.Files:N0} dosya · {Formats.Bytes(p.Bytes)} · {p.Folder}");
        var r = await _analyzer.AnalyzeAsync(_root, progress, ct);
        _result = r;
        Files.Clear();
        foreach (var f in r.LargestFiles) Files.Add(f);
        Folders.Clear();
        foreach (var f in r.LargestFolders) Folders.Add(f);
        Types.Clear();
        foreach (var t in r.Types) Types.Add(t);
        ProgressText = string.Empty;
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(VolumeText));
        StatusText = r.Cancelled ? "Tarama iptal edildi; o ana kadarki gerçek sonuç gösteriliyor." : "Tarama tamamlandı.";
        Host.RecordToolOperation("Depolama analizi", r.Cancelled ? CheckState.Skipped : CheckState.Info,
            $"{r.Root}: {Formats.Bytes(r.TotalSize)}, {r.FileCount:N0} dosya", sw.Elapsed, null, r.Cancelled);
    }

    /// <summary>
    /// Kullanıcının seçtiği TEK dosya: ad, boyut, konum gösterilir; açık onaydan sonra Windows'un kendi işlemiyle Geri Dönüşüm Kutusu'na
    /// gönderilir (Windows ayrıca kendi onayını gösterir; dosya kutuya sığmıyorsa kalıcı silineceğini Windows söyler).
    /// </summary>
    private async Task RecycleAsync()
    {
        if (_selectedFile is not { } f) return;
        if (LargeFileAnalyzer.ProtectedReason(f.Path) is { } reason)
        {
            await InformAsync("Dosya korunuyor", reason);
            return;
        }
        var ok = await ConfirmAsync("Dosya Geri Dönüşüm Kutusu'na gönderilsin mi?",
            "Seçtiğiniz dosya Geri Dönüşüm Kutusu'na taşınacak. Oradan geri yüklenebilir; ancak dosya Geri Dönüşüm Kutusu'nun kapasitesinden " +
            "büyükse Windows kalıcı olarak silineceğini ayrıca bildirir ve sizden yeniden onay ister.", "Geri Dönüşüm Kutusu'na gönder",
            [$"Dosya: {f.Name}", $"Boyut: {f.SizeText}", $"Konum: {f.Folder}", $"Son değişiklik: {f.ModifiedText}"]);
        if (!ok) return;
        var sw = Start();
        var hwnd = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        var (success, message) = await Task.Run(() => _analyzer.SendToRecycleBin(f.Path, hwnd));
        if (success)
        {
            Files.Remove(f);
            SelectedFile = null;
        }
        Host.RecordToolOperation("Dosyayı Geri Dönüşüm Kutusu'na gönderme", success ? CheckState.Healthy : CheckState.Error,
            $"{f.Name} ({f.SizeText}): {message}", sw.Elapsed, success ? null : message);
        await InformAsync(success ? "Dosya taşındı" : "Dosya taşınamadı", message, !success);
    }
}

// ====================================================================== Gizlilik

public sealed class PrivacyViewModel : ToolViewModel
{
    private readonly PrivacyService _service;

    public PrivacyViewModel(IToolHost host) : base(host)
    {
        _service = new PrivacyService(host.Logger);
        OpenCommand = new RelayCommand(p => { if (p is PrivacySetting s) OpenWindowsUri(s.SettingsUri); });
        OpenPrivacyCommand = new RelayCommand(() => OpenWindowsUri("ms-settings:privacy"));
    }

    public ObservableCollection<PrivacySetting> Settings { get; } = [];
    public ObservableCollection<CapabilityUse> RecentUses { get; } = [];
    public ICommand OpenCommand { get; }
    public ICommand OpenPrivacyCommand { get; }
    public bool HasUses => RecentUses.Count > 0;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Gizlilik ayarları okunuyor…";
        var r = await Task.Run(() => _service.ReadAsync(ct), ct);
        Settings.Clear();
        foreach (var s in r.Settings) Settings.Add(s);
        RecentUses.Clear();
        foreach (var u in r.RecentUses) RecentUses.Add(u);
        OnPropertyChanged(nameof(HasUses));
        if (r.Error is not null) ErrorText = r.Error;
        StatusText = $"{r.Settings.Count} ayar okundu · değiştirmek için ilgili Windows Ayarlar sayfası açılır";
    }
}

// ====================================================================== Batarya

public sealed class BatteryViewModel(IToolHost host) : ToolViewModel(host)
{
    private readonly BatteryService _service = new(host.Logger);
    private BatteryReport? _report;

    public ObservableCollection<BatteryInfo> Batteries { get; } = [];
    public bool HasBattery => _report?.HasBattery == true;
    public bool NoBattery => _report is { HasBattery: false, Error: null };
    public string NoBatteryText => BatteryService.NoBatteryText;
    public string PowerText => _report is null ? "—" : $"Güç kaynağı: {_report.AcText ?? "—"}" + (_report.RemainingTimeText is { } t ? $" · Kalan süre: {t}" : "");
    public string? Note => _report?.Note;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Batarya bilgisi okunuyor…";
        var r = await Task.Run(() => _service.ReadAsync(ct), ct);
        _report = r;
        Batteries.Clear();
        foreach (var b in r.Batteries) Batteries.Add(b);
        if (r.Error is not null) ErrorText = r.Error;
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(NoBattery));
        OnPropertyChanged(nameof(PowerText));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        StatusText = r.HasBattery ? $"{r.Batteries.Count} batarya" : r.Error ?? BatteryService.NoBatteryText;
    }
}

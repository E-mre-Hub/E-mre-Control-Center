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
    private string _updateStatus = L.T("Windows Update sürücü kataloğunda arama yapılmadı.", "The Windows Update driver catalog has not been searched.");
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
    public string CountsText => _scan is null ? "—" : L.T($"{_scan.Drivers.Count} sürücü · {_scan.Drivers.Count(d => d.Important)} önemli aygıt · {_scan.ProblemCount} sorunlu", $"{_scan.Drivers.Count} drivers · {_scan.Drivers.Count(d => d.Important)} key devices · {_scan.ProblemCount} with problems");

    /// <summary>important / problems / all.</summary>
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) DriversView.Refresh(); } }
    public string SearchText { get => _search; set { if (Set(ref _search, (value ?? "").Trim())) DriversView.Refresh(); } }

    private bool PassesFilter(DriverInfo d) =>
        (_filter switch { "problems" => d.State is CheckState.Error or CheckState.Warning, "all" => true, _ => d.Important || d.State != CheckState.Healthy }) &&
        (_search.Length == 0 || TextSearch.Contains(d.DeviceName, _search) || TextSearch.Contains(d.Manufacturer, _search) ||
         TextSearch.Contains(d.Category, _search) || TextSearch.Contains(d.Provider, _search));

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Sürücüler okunuyor…", "Reading drivers…");
        var scan = await Task.Run(() => _service.ScanAsync(ct), ct);
        _scan = scan;
        Drivers.Clear();
        foreach (var d in scan.Drivers.OrderByDescending(d => d.State is CheckState.Error or CheckState.Warning).ThenBy(d => d.Category).ThenBy(d => d.DeviceName))
            Drivers.Add(d);
        if (scan.Error is not null) ErrorText = L.T("Sürücü bilgisi alınamadı. ", "Driver information unavailable. ") + scan.Error;
        OnPropertyChanged(nameof(CountsText));
        StatusText = CountsText;
        var problems = scan.Drivers.Where(d => d.State is CheckState.Error or CheckState.Warning).ToList();
        Host.ReportDiagnostic("drivers", new CheckResult(L.T("Sürücüler", "Drivers"),
            scan.Error is not null && scan.Drivers.Count == 0 ? CheckState.Unknown : problems.Any(p => p.State == CheckState.Error) ? CheckState.Error : problems.Count > 0 ? CheckState.Warning : CheckState.Healthy,
            scan.Error ?? (problems.Count == 0 ? L.T("Sorunlu aygıt yok", "No devices with problems") : L.T($"{problems.Count} aygıtta sorun", $"Problems on {problems.Count} device(s)")), null, Nav.Update, Nav.Drivers));
    }

    /// <summary>Yalnızca ARAMA: Windows Update'in resmî sürücü kataloğu. Kurulum Windows Ayarlar → İsteğe bağlı güncellemeler'den yapılır.</summary>
    private async Task SearchUpdatesAsync(CancellationToken ct)
    {
        var sw = Start();
        UpdateStatus = L.T("Windows Update sürücü kataloğu aranıyor (birkaç dakika sürebilir)…", "Searching the Windows Update driver catalog (this can take a few minutes)…");
        var r = await Task.Run(() => _service.SearchWindowsUpdateAsync(ct), ct);
        Updates.Clear();
        foreach (var u in r.Updates) Updates.Add(u);
        OnPropertyChanged(nameof(HasUpdates));
        UpdateStatus = r.Error is not null ? L.T("Arama başarısız: ", "Search failed: ") + r.Error
            : r.Updates.Count == 0 ? L.T($"Windows Update bu cihaz için yeni sürücü bildirmedi ({DateTime.Now:HH:mm}).", $"Windows Update reported no new drivers for this device ({DateTime.Now:HH:mm}).")
            : L.T($"Windows Update {r.Updates.Count} sürücü güncellemesi bildirdi. Kurmak için Windows'un İsteğe bağlı güncellemeler sayfasını kullanın.", $"Windows Update reported {r.Updates.Count} driver update(s). Use Windows' Optional updates page to install them.");
        Host.RecordToolOperation(L.T("Sürücü güncelleme denetimi (Windows Update)", "Driver update check (Windows Update)"), r.Error is not null ? CheckState.Error : r.Updates.Count > 0 ? CheckState.Warning : CheckState.Healthy,
            r.Error is not null ? L.T("Arama başarısız", "Search failed") : r.Updates.Count == 0 ? L.T("Yeni sürücü bulunmadı", "No new drivers found") : L.T($"{r.Updates.Count} sürücü güncellemesi bulundu", $"{r.Updates.Count} driver update(s) found"), sw.Elapsed, r.Error);
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
    private string _wingetStatus = L.T("Winget güncelleme denetimi bu ekranda henüz çalıştırılmadı.", "The winget update check has not been run on this screen yet.");

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
    public string WingetHint => Host.IsAdmin ? L.T("Denetim mevcut Winget kartıyla yapılır (yalnızca kontrol; güncelleme Güncelleme ekranından, onayınızla).", "The check is done with the existing Winget card (check only; updates from the Updates screen, with your approval).")
        : L.T("Winget denetimi yönetici yetkisi gerektirir (Genel Ayarlar → Yönetici Yetkisi).", "The winget check requires administrator rights (General Settings → Administrator Rights).");
    public string SearchText { get => _search; set { if (Set(ref _search, (value ?? "").Trim())) AppsView.Refresh(); } }
    /// <summary>all / desktop / store / updates.</summary>
    public string SourceFilter { get => _source; set { if (Set(ref _source, value)) AppsView.Refresh(); } }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Kurulu uygulamalar okunuyor…", "Reading installed apps…");
        var scan = await Task.Run(() => _service.ListAsync(true, ct), ct);
        Apps.Clear();
        foreach (var a in scan.Apps) Apps.Add(new AppRowViewModel(a));
        if (scan.StoreError is not null) ErrorText = scan.StoreError;
        ApplyWinget(_cards.LastCardResult(ComponentKeys.Winget));
        StatusText = L.T($"{scan.Apps.Count} uygulama ({scan.Apps.Count(a => a.Source != "Microsoft Store")} masaüstü, {scan.Apps.Count(a => a.Source == "Microsoft Store")} Microsoft Store)", $"{scan.Apps.Count} apps ({scan.Apps.Count(a => a.Source != "Microsoft Store")} desktop, {scan.Apps.Count(a => a.Source == "Microsoft Store")} Microsoft Store)");
    }

    private async Task CheckWingetAsync()
    {
        WingetStatus = L.T("Winget ile güncellemeler kontrol ediliyor…", "Checking for updates with winget…");
        var r = await _cards.CheckCardForToolAsync(ComponentKeys.Winget);
        if (r is null)
        {
            WingetStatus = L.T("Winget denetimi başlatılamadı (başka bir işlem sürüyor veya yönetici yetkisi yok).", "The winget check could not start (another operation is running or there are no administrator rights).");
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
            WingetStatus = L.T("Winget denetimi başarısız: ", "Winget check failed: ") + (r.Reason ?? r.Summary);
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
        WingetStatus = items.Count == 0 ? L.T($"Winget güncelleme bildirmedi ({r.CompletedAt:HH:mm}).", $"Winget reported no updates ({r.CompletedAt:HH:mm}).")
            : L.T($"Winget {items.Count} güncelleme bildirdi ({matched} tanesi listede eşleşti). Uygulamak için Güncelleme → Winget kartını kullanın.", $"Winget reported {items.Count} update(s) ({matched} matched in the list). Use the Updates → Winget card to apply them.");
        AppsView.Refresh();
    }

    /// <summary>Sessiz kaldırma YOK: Windows'un kendi Uygulamalar sayfası açılır, kaldırmayı kullanıcı orada yapar.</summary>
    private async Task UninstallAsync()
    {
        var ok = await ConfirmAsync(L.T("Uygulama kaldırma", "Uninstall app"),
            L.T("Bu uygulama hiçbir programı kendiliğinden kaldırmaz. Windows Ayarlar → Uygulamalar → Yüklü uygulamalar sayfası açılacak; ", "This app never uninstalls a program by itself. The Windows Settings → Apps → Installed apps page will open; ") +
            L.T("kaldırmak istediğiniz uygulamayı orada seçip Windows'un kendi kaldırıcısıyla kaldırabilirsiniz.", "there you can select the app you want to remove and uninstall it with Windows' own uninstaller."), L.T("Ayarlar'ı aç", "Open Settings"), warning: false);
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
        StatusText = L.T("Taranacak sürücüyü veya klasörü seçin.", "Select the drive or folder to scan.");
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
        : L.T($"{_result.Root}: {Formats.Bytes(_result.TotalSize)} · {_result.FileCount:N0} dosya · {_result.FolderCount:N0} klasör", $"{_result.Root}: {Formats.Bytes(_result.TotalSize)} · {_result.FileCount:N0} files · {_result.FolderCount:N0} folders") +
          (_result.Skipped > 0 ? L.T($" · erişilemeyen {_result.Skipped:N0} öğe atlandı", $" · {_result.Skipped:N0} inaccessible item(s) skipped") : "") + (_result.Cancelled ? L.T(" · İPTAL EDİLDİ (kısmi sonuç)", " · CANCELLED (partial result)") : "") +
          $" · {_result.Duration.TotalSeconds:0.0} sn";
    public string VolumeText => _result?.Volume is { } v ? L.T($"{v.Letter} bölümü: {v.UsageText}", $"{v.Letter} partition: {v.UsageText}") : string.Empty;
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
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Taranacak klasörü seçin", "Select the folder to scan"), Multiselect = false };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        var option = Roots.FirstOrDefault(r => string.Equals(r.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)) ?? AddRoot(dialog.FolderName);
        option.IsSelected = true;
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        if (_root is null) return;
        var sw = Start();
        StatusText = L.T("Taranıyor: ", "Scanning: ") + _root;
        var progress = new Progress<ScanProgress>(p => ProgressText = L.T($"{p.Files:N0} dosya · {Formats.Bytes(p.Bytes)} · {p.Folder}", $"{p.Files:N0} files · {Formats.Bytes(p.Bytes)} · {p.Folder}"));
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
        StatusText = r.Cancelled ? L.T("Tarama iptal edildi; o ana kadarki gerçek sonuç gösteriliyor.", "Scan cancelled; showing the real result up to that point.") : L.T("Tarama tamamlandı.", "Scan completed.");
        Host.RecordToolOperation(L.T("Depolama analizi", "Storage analysis"), r.Cancelled ? CheckState.Skipped : CheckState.Info,
            L.T($"{r.Root}: {Formats.Bytes(r.TotalSize)}, {r.FileCount:N0} dosya", $"{r.Root}: {Formats.Bytes(r.TotalSize)}, {r.FileCount:N0} files"), sw.Elapsed, null, r.Cancelled);
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
            await InformAsync(L.T("Dosya korunuyor", "File is protected"), reason);
            return;
        }
        var ok = await ConfirmAsync(L.T("Dosya Geri Dönüşüm Kutusu'na gönderilsin mi?", "Send the file to the Recycle Bin?"),
            L.T("Seçtiğiniz dosya Geri Dönüşüm Kutusu'na taşınacak. Oradan geri yüklenebilir; ancak dosya Geri Dönüşüm Kutusu'nun kapasitesinden ", "The selected file will be moved to the Recycle Bin. It can be restored from there; however, if the file is larger than the Recycle Bin's capacity, ") +
            L.T("büyükse Windows kalıcı olarak silineceğini ayrıca bildirir ve sizden yeniden onay ister.", "Windows warns separately that it will be deleted permanently and asks you to confirm again."), L.T("Geri Dönüşüm Kutusu'na gönder", "Send to Recycle Bin"),
            [L.T($"Dosya: {f.Name}", $"File: {f.Name}"), L.T($"Boyut: {f.SizeText}", $"Size: {f.SizeText}"), L.T($"Konum: {f.Folder}", $"Location: {f.Folder}"), L.T($"Son değişiklik: {f.ModifiedText}", $"Last modified: {f.ModifiedText}")]);
        if (!ok) return;
        var sw = Start();
        var hwnd = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        var (success, message) = await Task.Run(() => _analyzer.SendToRecycleBin(f.Path, hwnd));
        if (success)
        {
            Files.Remove(f);
            SelectedFile = null;
        }
        Host.RecordToolOperation(L.T("Dosyayı Geri Dönüşüm Kutusu'na gönderme", "Send file to Recycle Bin"), success ? CheckState.Healthy : CheckState.Error,
            $"{f.Name} ({f.SizeText}): {message}", sw.Elapsed, success ? null : message);
        await InformAsync(success ? L.T("Dosya taşındı", "File moved") : L.T("Dosya taşınamadı", "File could not be moved"), message, !success);
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
        StatusText = L.T("Gizlilik ayarları okunuyor…", "Reading privacy settings…");
        var r = await Task.Run(() => _service.ReadAsync(ct), ct);
        Settings.Clear();
        foreach (var s in r.Settings) Settings.Add(s);
        RecentUses.Clear();
        foreach (var u in r.RecentUses) RecentUses.Add(u);
        OnPropertyChanged(nameof(HasUses));
        if (r.Error is not null) ErrorText = r.Error;
        StatusText = L.T($"{r.Settings.Count} ayar okundu · değiştirmek için ilgili Windows Ayarlar sayfası açılır", $"{r.Settings.Count} settings read · the related Windows Settings page opens to change them");
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
    public string PowerText => _report is null ? "—" : L.T($"Güç kaynağı: {_report.AcText ?? "—"}", $"Power source: {_report.AcText ?? "—"}") + (_report.RemainingTimeText is { } t ? L.T($" · Kalan süre: {t}", $" · Time remaining: {t}") : "");
    public string? Note => _report?.Note;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = L.T("Batarya bilgisi okunuyor…", "Reading battery information…");
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
        StatusText = r.HasBattery ? L.T($"{r.Batteries.Count} batarya", $"{r.Batteries.Count} battery(ies)") : r.Error ?? BatteryService.NoBatteryText;
    }
}

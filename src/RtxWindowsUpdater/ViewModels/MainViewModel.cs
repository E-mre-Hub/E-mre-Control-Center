using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Sol paneldeki işlem durumunun birbirinden ayrı hâlleri.</summary>
public enum OperationState { Idle, Checking, Updating, Completed, Failed, Cancelled }

/// <summary>
/// Arayüz durumu ve akışı. Sistem işlemlerini doğrudan yapmaz; servisleri/orkestratörü çağırır.
/// Seçim durumu merkezi olarak <see cref="SelectedOperationsManager"/> içinde tutulur.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static class Icons
    {
        public const string Windows = "";
        public const string Gpu = "";
        public const string Admin = "";
        public const string WindowsUpdate = "";
        public const string Winget = "";
        public const string Store = "";
        public const string Nvidia = "";
        public const string Defender = "";
        public const string RecycleBin = "";
        public const string Sfc = "";
        public const string Dism = "";
        public const string Mrt = "";
        public const string Info = "";
        public const string Shield = "";
        public const string Warning = "";
        public const string Check = "";
        public const string Restart = "";
        public const string Download = "";
        public const string Play = "";
        public const string Search = "";
        public const string SelectAll = "";
        public const string TempFiles = "\uE8B7";
    }

    private const int MaxLogEntries = 5000;

    private readonly Logger _logger;
    private readonly SystemRequirementsChecker _requirements;
    private readonly UpdateOrchestrator _orchestrator;
    private readonly SelectedOperationsManager _selection;
    private readonly bool _argAccepted;
    private readonly bool _argStartCheck;

    private CancellationTokenSource? _cts;

    /// <summary>Kart başına en son GERÇEK kontrol sonucu. Bir kart işlendiğinde yeniden kontrol gerekir.</summary>
    private readonly Dictionary<string, ModuleResult> _checks = new();

    private bool _isDashboard;
    private bool _requirementsChecked;
    private bool _isSupported;
    private bool _isAdmin;
    private bool _accepted;
    private string _unsupportedMessage = string.Empty;
    private bool _hasRtx = true; // gereksinim kontrolü bitene kadar "kartsız" seçeneği gösterilmez
    private string _noRtxMessage = string.Empty;
    private CategoryViewModel? _currentCategory;
    private SectionViewModel? _currentSection;
    private bool _isBusy;
    private bool _isUpdatePhase;
    private string _stepText = L.T("Henüz işlem yapılmadı", "No operation yet");
    private double _progress;
    private bool _cleanRecycleBin = true;
    private bool _cancelRequested;
    private bool _updatesApplied;

    // --- v1.2: sistem bilgileri, sağlık özeti, işlem geçmişi, bildirimler, günlük yönetimi ---
    private readonly AppStateStore _state;
    private readonly NotificationService _notifications;
    private readonly SystemInfoService _systemInfo;
    private TimeSpan _lastBusyDuration;
    private bool _isSystemInfoExpanded;
    private bool _isSystemInfoLoading;
    private string _systemInfoStatus = L.T("Sistem bilgileri okunuyor...", "Reading system information...");
    private string _healthHeadline = L.T("Sistem henüz kontrol edilmedi", "System not checked yet");
    private string _healthCounts = string.Empty;
    private ComponentStatus _healthStatus = ComponentStatus.NotChecked;
    private OperationRecord? _lastOperation;
    private string _logFilter = "all";
    private bool _notificationsEnabled;

    // --- performans: günlük satırları toplu (batch) işlenir, işlem durumu ayrı tutulur ---
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private readonly DispatcherTimer _logTimer;
    private int _logFlushScheduled;
    private OperationState _operationState = OperationState.Idle;
    private ObservableCollection<UpdateRowViewModel> _updateRows = [];

    /// <summary>Bir grup günlük satırı ekrana eklendiğinde (görünüm tek seferde en alta kaydırılır).</summary>
    public event EventHandler? LogsAppended;

    public MainViewModel(Logger logger, bool argAccepted, bool argStartCheck,
        AppStateStore state, NotificationService notifications)
    {
        _logger = logger;
        _state = state;
        _notifications = notifications;
        _systemInfo = new SystemInfoService(logger);
        _notificationsEnabled = state.State.NotificationsEnabled;
        _notifications.Enabled = _notificationsEnabled;
        // Önceki oturumlarda winget'in gerçekten 0x8A15008E döndürdüğü paketler (aynı sürüm tekrar otomatik denenmez).
        WingetManager.LoadKnownTechnologyMismatches(state.State.WingetTechnologyMismatch);
        WingetManager.LoadKnownNoVersionChange(state.State.WingetNoVersionChange);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _logTimer.Tick += FlushPendingLogs;
        _argAccepted = argAccepted;
        _argStartCheck = argStartCheck;
        _requirements = new SystemRequirementsChecker(logger);
        _orchestrator = new UpdateOrchestrator(logger);
        _selection = new SelectedOperationsManager(_orchestrator.ModuleOrder);

        WindowsRow = new RequirementRowViewModel("Windows 11", Icons.Windows);
        GpuRow = new RequirementRowViewModel("NVIDIA RTX GPU", Icons.Gpu);
        AdminRow = new RequirementRowViewModel(L.T("Yönetici yetkisi", "Administrator rights"), Icons.Admin);
        Requirements = [WindowsRow, GpuRow, AdminRow, InternetRow];

        // Tüm kartlar tek "Sistem İşlemleri" kategorisinde, aynı kart yapısıyla gösterilir.
        Cards =
        [
            new ComponentCardViewModel(ComponentKeys.WindowsUpdate, "Windows Update", Icons.WindowsUpdate)
            {
                CommandText = "Windows Update Agent API",
                Description = L.T("Windows için kullanılabilir sistem güncellemelerini kontrol eder.", "Checks for available system updates for Windows."),
                InfoText = L.T("Windows Update üzerinden kullanılabilir Windows güncellemelerini kontrol eder. Güncelleme bulunduğunda kullanıcı onayıyla yüklenebilir.", "Checks for available Windows updates through Windows Update. Updates that are found can be installed with your approval.")
            },
            new ComponentCardViewModel(ComponentKeys.Winget, "Winget", Icons.Winget)
            {
                CommandText = "winget upgrade",
                Description = L.T("Yüklü uygulamalar için kullanılabilir yazılım güncellemelerini kontrol eder.", "Checks for available software updates for installed apps."),
                InfoText = L.T("Windows Package Manager (winget) üzerinden sistemde kurulu desteklenen uygulamalar için kullanılabilir güncellemeleri kontrol eder.", "Checks for available updates for supported apps installed on the system through Windows Package Manager (winget).")
            },
            new ComponentCardViewModel(ComponentKeys.Store, "Microsoft Store", Icons.Store)
            {
                CommandText = "winget --source msstore",
                Description = L.T("Microsoft Store uygulamalarındaki kullanılabilir güncellemeleri kontrol eder.", "Checks for available updates for Microsoft Store apps."),
                InfoText = L.T("Microsoft Store üzerinden yüklenen uygulamalar için kullanılabilir güncellemeleri kontrol eder ve desteklenen güncellemeleri kullanıcı onayıyla başlatabilir.", "Checks for available updates for apps installed from Microsoft Store and can start supported updates with your approval.")
            },
            new ComponentCardViewModel(ComponentKeys.Nvidia, "NVIDIA Driver", Icons.Nvidia)
            {
                CommandText = L.T("NVIDIA sürücü servisi", "NVIDIA driver service"),
                Description = L.T("NVIDIA RTX ekran kartı sürücüsünün güncel olup olmadığını kontrol eder.", "Checks whether the NVIDIA RTX graphics card driver is up to date."),
                InfoText = L.T("Sistemdeki NVIDIA RTX ekran kartını ve mevcut sürücüsünü kontrol eder. Kullanılabilir sürücü güncellemesi varsa kullanıcıya bildirir.", "Checks the NVIDIA RTX graphics card in the system and its current driver. Notifies you if a driver update is available.")
            },
            new ComponentCardViewModel(ComponentKeys.Defender, "Microsoft Defender", Icons.Defender)
            {
                CommandText = "Update-MpSignature",
                Description = L.T("Defender koruma durumunu ve güvenlik tanımlarının güncelliğini kontrol eder.", "Checks Defender protection status and whether the security definitions are up to date."),
                InfoText = L.T("Microsoft Defender'ın koruma durumunu ve güvenlik tanımlarının güncelliğini kontrol eder. Gerekli olduğunda Defender tanımları güncellenebilir.", "Checks Microsoft Defender's protection status and whether its security definitions are up to date. Defender definitions can be updated when needed.")
            },
            new ComponentCardViewModel(ComponentKeys.RecycleBin, L.T("Çöp Kutusu", "Recycle Bin"), Icons.RecycleBin)
            {
                CommandText = "Shell Recycle Bin API",
                Description = L.T("Windows Çöp Kutusu'ndaki öğeleri kontrol eder ve kullanıcı onayıyla temizler.", "Checks the items in the Windows Recycle Bin and empties it with your approval."),
                InfoText = L.T("Windows Çöp Kutusu'ndaki öğeleri kontrol eder. Kullanıcı onayıyla Çöp Kutusu temizlenebilir.", "Checks the items in the Windows Recycle Bin. The Recycle Bin can be emptied with your approval.")
            },
            new ComponentCardViewModel(ComponentKeys.Sfc, L.T("Windows Sistem Dosyası Kontrolü", "Windows System File Check"), Icons.Sfc)
            {
                IsMaintenance = true,
                ShortTitle = "SFC",
                CommandText = "SFC /SCANNOW",
                Description = L.T("Windows sistem dosyalarını tarar ve bozuk veya eksik dosyaları onarmayı dener.", "Scans Windows system files and tries to repair corrupt or missing files."),
                InfoText = L.T("Windows sistem dosyalarının bütünlüğünü kontrol eder. Bozuk veya eksik sistem dosyaları tespit edilirse Windows tarafından desteklenen şekilde onarılmaya çalışılır.", "Checks the integrity of Windows system files. If corrupt or missing system files are found, Windows tries to repair them in the supported way."),
                ActionText = L.T("Tarama Başlat", "Start Scan"),
                ActionGlyph = Icons.Play
            },
            new ComponentCardViewModel(ComponentKeys.Dism, L.T("Windows Image Sağlık Kontrolü", "Windows Image Health Check"), Icons.Dism)
            {
                IsMaintenance = true,
                ShortTitle = "DISM",
                CommandText = DismManager.DisplayCommand,
                Description = L.T("Windows bileşen deposunun sağlık durumunu kontrol eder; onarılabilir bozulma bulunursa onayınızla onarır.", "Checks the health of the Windows component store; repairs it with your approval if repairable corruption is found."),
                InfoText = L.T("DISM /CheckHealth ile Windows bileşen deposunun sağlık durumunu kontrol eder. Sonuç \"onarılabilir\" ise, ", "Checks the health of the Windows component store with DISM /CheckHealth. If the result is \"repairable\", ") +
                           L.T("yalnızca sizin onayınızla (Güncelle / Onar) DISM /RestoreHealth çalıştırılır ve onarımdan sonra durum ", "DISM /RestoreHealth runs only with your approval (Update / Repair), and after the repair the status ") +
                           L.T("yeniden kontrol edilerek doğrulanır. Onayınız olmadan hiçbir onarım yapılmaz.", "is checked again to verify it. No repair is made without your approval."),
                ActionText = L.T("Kontrolü Başlat", "Start Check"),
                ActionGlyph = Icons.Search
            },
            new ComponentCardViewModel(ComponentKeys.Mrt, L.T("Microsoft Kötü Amaçlı Yazılım Temizleme Aracı", "Microsoft Malicious Software Removal Tool"), Icons.Mrt)
            {
                IsMaintenance = true,
                ShortTitle = "MRT",
                CommandText = L.T("MRT — Hızlı Tarama", "MRT — Quick Scan"),
                Description = L.T("Windows MRT aracını kullanarak hızlı bir kötü amaçlı yazılım taraması gerçekleştirir.", "Runs a quick malware scan with the Windows MRT tool."),
                InfoText = L.T("Windows'un yerleşik Microsoft Kötü Amaçlı Yazılım Temizleme Aracıdır. Bu kart MRT'nin hızlı tarama modunu kullanır.", "The built-in Microsoft Malicious Software Removal Tool of Windows. This card uses MRT's quick scan mode."),
                ActionText = L.T("Hızlı Taramayı Başlat", "Start Quick Scan"),
                ActionGlyph = Icons.Play
            },
            new ComponentCardViewModel(ComponentKeys.TempFiles, L.T("Windows Geçici Dosyalar", "Windows Temporary Files"), Icons.TempFiles)
            {
                ShortTitle = L.T("Geçici Dosyalar", "Temporary Files"),
                CommandText = L.T("%TEMP% · %WINDIR%\\Temp · DO önbelleği", "%TEMP% · %WINDIR%\\Temp · DO cache"),
                Description = L.T("Windows'un güvenli şekilde temizlenebilecek geçici dosyalarını kontrol eder ve onayınızla temizler.", "Checks the Windows temporary files that can be cleaned safely and cleans them with your approval."),
                InfoText = L.T("Ayarlar > Sistem > Depolama > Geçici dosyalar bölümündeki güvenli kategorileri ölçer: kullanıcı ve Windows geçici ", "Measures the safe categories in Settings > System > Storage > Temporary files: user and Windows temporary ") +
                           L.T("klasörleri, Teslim En İyileştirme önbelleği, Windows hata raporları ve DirectX gölgelendirici önbelleği. ", "folders, the Delivery Optimization cache, Windows error reports and the DirectX shader cache. ") +
                           L.T("Son 24 saatte değişen ve kullanımdaki dosyalara dokunulmaz; Çöp Kutusu, İndirilenler ve Windows.old hariçtir.", "Files changed in the last 24 hours and files in use are not touched; the Recycle Bin, Downloads and Windows.old are excluded.")
            }
        ];

        foreach (var card in Cards)
        {
            var c = card;
            card.SelectionChangedCallback = (key, selected) => _selection.SetSelected(key, selected);
            card.ActionCommand = new AsyncCommand(
                () => c.IsMaintenance ? RunMaintenanceAsync(c) : RunCardCheckAsync(c),
                () => CanStartOperation && IsSupported && !c.IsUnavailable, OnCommandError);
            card.DetailsCommand = new RelayCommand(() => Detail.Show(c));
            if (_state.State.Cards.TryGetValue(card.Key, out var saved))
                card.LoadHistory(saved);
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ComponentCardViewModel.HealthText) or nameof(ComponentCardViewModel.Status))
                    RecomputeHealth();
            };
        }
        _selection.SelectionChanged += OnSelectionChanged;

        // Hız Testi: sistem işlemi sürerken başlatılamaz; test sürerken de sistem işlemleri başlatılamaz (ölçümü bozar).
        SpeedTest = new SpeedTestViewModel(_logger, _state, Dialog, () => IsBusy);
        SpeedTest.Changed += (_, _) =>
        {
            RefreshCategories();
            RaiseUpdateAvailabilityChanged();
            OnPropertyChanged(nameof(AvailableSummary));
            OnPropertyChanged(nameof(ShowUpdateOverlay));
            OnPropertyChanged(nameof(ShowOfflineOverlay));
        };

        // Uygulama içi güncelleme (zorunlu): yeni sürüm varsa ana ekranın önüne pencere gelir.
        Update = new UpdateViewModel(_logger);
        Update.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateViewModel.IsRequired)) OnPropertyChanged(nameof(ShowUpdateOverlay));
        };
        // Uygulama açıkken yeni sürüm bulundu: pencere görünürse zorunlu pencere zaten gelir; bildirim alanındaysa Windows bildirimi.
        Update.UpdateFound += info =>
        {
            if (_windowVisible || !NotificationsEnabled) return;
            _ = _notifications.ShowAsync(L.T("Yeni sürüm yayınlandı", "New version available"),
                L.T($"{AppInfo.Name} {info.Tag} yayınlandı. Güncellemek için uygulamayı açın (güncelleme zorunludur).", $"{AppInfo.Name} {info.Tag} has been released. Open the app to update (the update is required)."));
            _logger.Info(L.T($"Windows bildirimi gönderildi: yeni sürüm {info.Tag}.", $"Windows notification sent: new version {info.Tag}."));
        };

        // Kontrol Merkezi: mevcut kartlar (aynı nesneler) 7 kategori altında gruplanır; yalnızca arayüz düzenidir.
        // Her kategori soldaki alt menüde bölmelere ayrılır (Monster düzeni); aynı bölme türü birden fazla kategoride bulunabilir.
        ComponentCardViewModel CardOf(string key) => Cards.First(c => c.Key == key);
        SectionViewModel Log() => new(SectionKeys.Log, L.T("İşlem Günlüğü", "Operation Log"), "");
        SectionViewModel Found() => new(SectionKeys.Found, L.T("Bulunan Güncellemeler", "Updates Found"), "");
        InitTools();
        Categories =
        [
            new CategoryViewModel(CategoryKeys.Update, L.T("Güncelleme", "Updates"), "", L.T("Windows ve uygulama güncellemeleri", "Windows and app updates"),
                [CardOf(ComponentKeys.WindowsUpdate), CardOf(ComponentKeys.Winget), CardOf(ComponentKeys.Store),
                 CardOf(ComponentKeys.Nvidia), CardOf(ComponentKeys.Defender)],
                [new(SectionKeys.Cards, L.T("Güncellemeler", "Updates"), ""), new(SectionKeys.Drivers, L.T("Sürücüler", "Drivers"), ""),
                 new(SectionKeys.Apps, L.T("Uygulamalar", "Apps"), ""), Found(), Log()]),
            new CategoryViewModel(CategoryKeys.Cleanup, L.T("Temizleme", "Cleanup"), "", L.T("Geçici dosyalar ve Çöp Kutusu", "Temporary files and Recycle Bin"),
                [CardOf(ComponentKeys.TempFiles), CardOf(ComponentKeys.RecycleBin)],
                [new(SectionKeys.Cards, L.T("Temizlik", "Cleanup"), ""), new(SectionKeys.StorageAnalysis, L.T("Depolama Analizi", "Storage Analysis"), ""), Log()]),
            new CategoryViewModel(CategoryKeys.Health, L.T("Cihaz Sağlık", "Device Health"), "", L.T("Windows sistem sağlık kontrolleri", "Windows system health checks"),
                [CardOf(ComponentKeys.Sfc), CardOf(ComponentKeys.Dism), CardOf(ComponentKeys.Mrt)],
                [new(SectionKeys.Cards, L.T("Sağlık Araçları", "Health Tools"), ""), new(SectionKeys.SystemHealth, L.T("Sistem Sağlığı", "System Health"), ""),
                 new(SectionKeys.StorageHealth, L.T("Depolama Sağlığı", "Storage Health"), ""), new(SectionKeys.EventLog, L.T("Olay Günlüğü", "Event Log"), ""),
                 new(SectionKeys.Crash, L.T("Çökme Analizi", "Crash Analysis"), ""), Log()]),
            new CategoryViewModel(CategoryKeys.SpeedTest, L.T("Hız Testi", "Speed Test"), "", L.T("İnternet hızı, ağ bağlantısı ve DNS tanılama", "Internet speed, network connection and DNS diagnostics"), [],
                [new(SectionKeys.SpeedTest, L.T("Hız Testi", "Speed Test"), ""), new(SectionKeys.Network, L.T("Ağ Merkezi", "Network Center"), ""),
                 new(SectionKeys.Dns, L.T("DNS Tanılama", "DNS Diagnostics"), ""), new(SectionKeys.SpeedServers, L.T("Sunucu", "Server"), ""),
                 new(SectionKeys.SpeedHistory, L.T("Sonuçlar", "Results"), ""),
                 new(SectionKeys.SpeedMethod, L.T("Yöntem", "Method"), "")]),
            new CategoryViewModel(CategoryKeys.Settings, L.T("Genel Ayarlar", "General Settings"), "", L.T("Bildirimler, günlük ve uygulama tercihleri", "Notifications, log and app preferences"), [],
                [new(SectionKeys.Quick, L.T("Kolay Ayar", "Quick Settings"), ""), new(SectionKeys.Privacy, L.T("Gizlilik", "Privacy"), ""), new(SectionKeys.Admin, L.T("Yönetici Yetkisi", "Administrator Rights"), ""),
                 new(SectionKeys.LogFiles, L.T("Günlük Dosyaları", "Log Files"), ""),
                 new(SectionKeys.Legal, L.T("Yasal ve Gizlilik", "Legal and Privacy"), "")]),
            new CategoryViewModel(CategoryKeys.Summary, L.T("Özet", "Summary"), "", L.T("Sistem sağlığı, son işlem ve geçmiş", "System health, last operation and history"), [],
                [new(SectionKeys.Health, L.T("Sağlık Özeti", "Health Summary"), ""), new(SectionKeys.Recent, L.T("İşlem Geçmişi", "Operation History"), ""), Found(), Log()]),
            new CategoryViewModel(CategoryKeys.Device, L.T("Cihaz Bilgileri", "Device Info"), "", L.T("Donanım, Windows ve sistem durumu", "Hardware, Windows and system status"), [],
                [new(SectionKeys.DeviceInfo, L.T("Cihaz Bilgileri", "Device Info"), ""), new(SectionKeys.DeviceStatus, L.T("Performans", "Performance"), ""),
                 new(SectionKeys.Battery, L.T("Batarya", "Battery"), ""), new(SectionKeys.DeviceAbout, L.T("Hakkında", "About"), "")]),
            new CategoryViewModel(CategoryKeys.SystemTools, L.T("Sistem Araçları", "System Tools"), "", L.T("Tanılama, başlangıç, servisler ve raporlar", "Diagnostics, startup, services and reports"), [],
                [new(SectionKeys.Diagnose, L.T("Tek Tıkla Tanıla", "One-Click Diagnosis"), ""), new(SectionKeys.Startup, L.T("Başlangıç Uygulamaları", "Startup Apps"), ""),
                 new(SectionKeys.Services, L.T("Windows Servisleri", "Windows Services"), ""), new(SectionKeys.Processes, L.T("İşlemler", "Processes"), ""),
                 new(SectionKeys.Security, L.T("Güvenlik", "Security"), ""), new(SectionKeys.Report, L.T("Sistem Raporu", "System Report"), ""),
                 new(SectionKeys.Support, L.T("Destek Paketi", "Support Package"), "")])
        ];
        foreach (var category in Categories)
        {
            var c = category;
            category.OpenCommand = new RelayCommand(() => CurrentCategory = c);
        }
        GoHomeCommand = new RelayCommand(() => CurrentCategory = null);
        InitSearch();

        RecentOperations = new ObservableCollection<OperationRecord>(_state.State.Recent);
        _lastOperation = RecentOperations.FirstOrDefault();
        LogsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Logs);
        LogsView.Filter = o => o is LogEntry e && PassesLogFilter(e);
        RecomputeHealth();

        ContinueCommand = new RelayCommand(EnterFromRequirements, () => Accepted && IsSupported && HasRtx && HasInternet);
        ContinueWithoutGpuCommand = new RelayCommand(EnterFromRequirements, () => Accepted && IsSupported && !HasRtx && HasInternet);
        InitializeInternetCommands();
        InitializeLogArchive();
        InitializeLegal();
        ElevateCommand = new AsyncCommand(() => PromptElevationAsync(false), () => !IsAdmin && IsSupported, OnCommandError);
        // Yönetici yetkisi yoksa tüm kartlar kullanım dışıdır; toplu kontrol butonları da kapalıdır (yalnızca yeniden başlatma sunulur).
        StartCheckCommand = new AsyncCommand(StartCheckAsync, () => CanStartOperation && IsSupported && IsAdmin, OnCommandError);
        CheckSelectedCommand = new AsyncCommand(CheckSelectedAsync, () => CanStartOperation && IsSupported && IsAdmin, OnCommandError);
        UpdateAllCommand = new AsyncCommand(UpdateAllAsync, () => CanUpdateAll, OnCommandError);
        UpdateSelectedCommand = new AsyncCommand(UpdateSelectedAsync, () => CanRunSelected, OnCommandError);
        ClearSelectionCommand = new RelayCommand(() => _selection.Clear(), () => _selection.HasSelection);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && !_cancelRequested);
        OpenLogCommand = new AsyncCommand(OpenLogFileAsync, onError: OnCommandError);
        OpenLogFolderCommand = new AsyncCommand(OpenLogFolderAsync, onError: OnCommandError);
        ExportLogsCommand = new AsyncCommand(ExportLogsAsync, onError: OnCommandError);
        ClearLogsCommand = new RelayCommand(ClearLogs, () => Logs.Count > 0);
        ShowRecentCommand = new RelayCommand(() => OpenSection(CategoryKeys.Summary, SectionKeys.Recent));
        ShowSpeedServersCommand = new RelayCommand(() => OpenSection(CategoryKeys.SpeedTest, SectionKeys.SpeedServers));
        RefreshSystemInfoCommand = new AsyncCommand(RefreshSystemInfoAsync, () => !IsSystemInfoLoading, OnCommandError);

        _logger.LogAdded += OnLogAdded;
    }

    // ------------------------------------------------------------------ state

    public DialogViewModel Dialog { get; } = new();

    /// <summary>Hız Testi kategorisi (gerçek ölçüm; sonuç geçmişi state.json'da).</summary>
    public SpeedTestViewModel SpeedTest { get; }

    /// <summary>Yeni bir sistem işlemi (kontrol / güncelleme / kart işlemi) başlatılabilir mi? Hız testi sürerken hayır.</summary>
    // İnternet yokken hiçbir işlem başlatılamaz (KULLANICI KARARI 2026-09-27): bildirim alanı menüsü / kısayollar kilit ekranını
    // (yalnızca işlem sürmüyorken görünür) bir işlem başlatarak gizleyemesin.
    private bool CanStartOperation => !IsBusy && SpeedTest?.IsWorking != true && HasInternet;

    public UpdateViewModel Update { get; }

    /// <summary>Güncelleme kurulumunun ilettiği önceki sürüm (<see cref="LaunchModes.ArgUpdatedFrom"/>); App açılışta verir.</summary>
    public string? UpdatedFrom { get; set; }

    /// <summary>"E-mre Control Center v1.7.2 → v1.7.3 sürümüne güncellendi." – önceki sürüm geçerli ve şimdikinden eskiyse; yoksa null.</summary>
    internal static string? DescribeUpdate(string? from)
    {
        if (!Version.TryParse(from, out var previous) || !Version.TryParse(AppInfo.Version, out var current)) return null;
        previous = new Version(previous.Major, previous.Minor, Math.Max(0, previous.Build));
        return previous < current ? L.T($"{AppInfo.Name} v{previous.ToString(3)} → v{AppInfo.Version} sürümüne güncellendi.", $"{AppInfo.Name} has been updated from v{previous.ToString(3)} to v{AppInfo.Version}.") : null;
    }

    /// <summary>
    /// "Yeni sürüm yayınlandı" penceresi: girişten sonra (Kontrol Merkezi), sistem işlemi veya hız testi sürmüyorken gösterilir
    /// (süren işlem yarıda bırakılmasın; bitince pencere gelir).
    /// </summary>
    public bool ShowUpdateOverlay => Update.IsRequired && IsDashboard && !IsBusy && SpeedTest?.IsWorking != true;
    public ObservableCollection<RequirementRowViewModel> Requirements { get; }
    public RequirementRowViewModel WindowsRow { get; }
    public RequirementRowViewModel GpuRow { get; }
    public RequirementRowViewModel AdminRow { get; }

    /// <summary>"Sistem İşlemleri" altındaki 10 kartın tamamı (ekran sırasıyla).</summary>
    public ObservableCollection<ComponentCardViewModel> Cards { get; }

    public ObservableCollection<LogEntry> Logs { get; } = [];
    /// <summary>"Bulunan Güncellemeler" tablosu; her yenilemede tek seferde değiştirilir (satır satır bildirim yok).</summary>
    public ObservableCollection<UpdateRowViewModel> UpdateRows
    {
        get => _updateRows;
        private set => Set(ref _updateRows, value);
    }

    // --- Detaylı Sonuç paneli ---
    public DetailViewModel Detail { get; } = new();

    // --- Sistem Bilgileri ---
    public ObservableCollection<SystemInfoField> SystemInfoFields { get; } = [];
    public ObservableCollection<SystemInfoField> SystemInfoPrimaryFields { get; } = [];

    public bool IsSystemInfoExpanded { get => _isSystemInfoExpanded; set => Set(ref _isSystemInfoExpanded, value); }

    public bool IsSystemInfoLoading
    {
        get => _isSystemInfoLoading;
        private set { Set(ref _isSystemInfoLoading, value); CommandManager.InvalidateRequerySuggested(); }
    }

    public string SystemInfoStatus { get => _systemInfoStatus; private set => Set(ref _systemInfoStatus, value); }

    // --- Sistem Sağlık Özeti (kartların GERÇEK durumlarından hesaplanır) ---
    public string HealthHeadline { get => _healthHeadline; private set => Set(ref _healthHeadline, value); }
    public string HealthCounts { get => _healthCounts; private set => Set(ref _healthCounts, value); }
    public ComponentStatus HealthStatus { get => _healthStatus; private set => Set(ref _healthStatus, value); }

    // --- Son işlem özeti ve işlem geçmişi ---
    public ObservableCollection<OperationRecord> RecentOperations { get; }

    public OperationRecord? LastOperation
    {
        get => _lastOperation;
        private set { Set(ref _lastOperation, value); OnPropertyChanged(nameof(HasLastOperation)); }
    }

    public bool HasLastOperation => LastOperation is not null;

    // --- Günlük yönetimi ---
    public System.ComponentModel.ICollectionView LogsView { get; }

    /// <summary>Günlük filtresi: all / info / success / warning / error. Yalnızca görünümü değiştirir.</summary>
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (Set(ref _logFilter, value))
                LogsView.Refresh();
        }
    }

    // --- Bildirimler ---
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (!Set(ref _notificationsEnabled, value)) return;
            _notifications.Enabled = value;
            _state.SetNotificationsEnabled(value);
            _logger.Info(value ? L.T("Windows bildirimleri açıldı.", "Windows notifications turned on.") : L.T("Windows bildirimleri kapatıldı.", "Windows notifications turned off."));
            RefreshCategories();
        }
    }

    // --- Kontrol Merkezi (gezinme; yalnızca arayüz düzeni) ---

    /// <summary>Ana sayfadaki 7 kategori (üstte 4, altta 3): Güncelleme, Temizleme, Cihaz Sağlık, Hız Testi, Genel Ayarlar, Özet, Cihaz Bilgileri.</summary>
    public ObservableCollection<CategoryViewModel> Categories { get; }

    /// <summary>Açık kategori; null ise Kontrol Merkezi ana sayfası gösterilir. Kategori her açılışta ilk bölmesiyle açılır.</summary>
    public CategoryViewModel? CurrentCategory
    {
        get => _currentCategory;
        private set
        {
            if (!Set(ref _currentCategory, value)) return;
            _currentSection = value?.Sections.FirstOrDefault();
            OnPropertyChanged(nameof(IsHome));
            OnPropertyChanged(nameof(IsHomeScreen));
            if (value is not null) IsSearchOpen = false;
            OnPropertyChanged(nameof(IsCardsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsSummaryPage));
            OnPropertyChanged(nameof(IsDevicePage));
            OnPropertyChanged(nameof(ShowProgressPanel));
            // Bölme bildirimi kategoriden SONRA gelir: sol menü önce yeni bölme listesine geçer, sonra seçimi alır.
            OnSectionChanged();
        }
    }

    /// <summary>
    /// Açık kategorinin seçili bölmesi (sol alt menü; sağda başlığı ve içeriği gösterilir). Menü listesi değişirken
    /// gelen null veya başka kategoriye ait bölme yok sayılır.
    /// </summary>
    public SectionViewModel? CurrentSection
    {
        get => _currentSection;
        set
        {
            if (value is null || ReferenceEquals(value, _currentSection) || CurrentCategory?.Sections.Contains(value) != true) return;
            _currentSection = value;
            OnSectionChanged();
        }
    }

    /// <summary>Seçili bölmenin anahtarı (<see cref="SectionKeys"/>); görünüm bölme içeriklerini buna göre gösterir.</summary>
    public string? CurrentSectionKey => _currentSection?.Key;

    private void OnSectionChanged()
    {
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(CurrentSectionKey));
        // Cihaz Durumu canlı ölçümü yalnızca o bölme açıkken çalışır.
        UpdateDeviceMonitoring();
        // Tanılama araçları: yalnızca açık bölmenin aracı etkin (izleme / okuma kapanınca durur).
        UpdateToolActivation();
        // Sunucu bölmesi (veya Ookla seçiliyken Hız Testi) açılınca Ookla aracı denetlenir; sistemde değişiklik yapmaz.
        if (CurrentSectionKey == SectionKeys.SpeedServers || CurrentSectionKey == SectionKeys.SpeedTest && SpeedTest.IsOoklaProvider)
            _ = SpeedTest.EnsureOoklaAsync();
        if (CurrentSectionKey == SectionKeys.LogFiles) RefreshLogArchive();
    }

    /// <summary>Belirtilen kategoriyi açar ve bölmesini seçer (ör. "Geçmiş" → Özet / Son İşlemler).</summary>
    public void OpenSection(string categoryKey, string sectionKey)
    {
        var category = Categories.FirstOrDefault(c => c.Key == categoryKey);
        if (category is null) return;
        CurrentCategory = category;
        CurrentSection = category.Sections.FirstOrDefault(s => s.Key == sectionKey);
    }

    public bool IsHome => CurrentCategory is null;

    /// <summary>Kontrol Merkezi ana sayfası gerçekten ekranda (dalga arka planı yalnızca burada çizilir).</summary>
    public bool IsHomeScreen => IsDashboard && IsHome;
    public bool IsCardsPage => CurrentCategory?.HasCards == true;
    public bool IsSettingsPage => CurrentCategory?.Key == CategoryKeys.Settings;
    public bool IsSummaryPage => CurrentCategory?.Key == CategoryKeys.Summary;
    public bool IsDevicePage => CurrentCategory?.Key == CategoryKeys.Device;

    /// <summary>Sol menüdeki İlerleme kartı: işlem başlatılabilen kart kategorilerinde ve Özet'te gösterilir.</summary>
    public bool ShowProgressPanel => IsCardsPage || IsSummaryPage;

    public ICommand GoHomeCommand { get; }

    /// <summary>Ana sayfa kartlarındaki kısa durum satırlarını mevcut gerçek durumlardan günceller.</summary>
    private void RefreshCategories()
    {
        if (Categories is null) return; // kurucu tamamlanmadan gelen bildirimler
        foreach (var c in Categories)
        {
            if (c.HasCards)
            {
                c.RefreshFromCards();
                continue;
            }
            (c.Status, c.StatusText) = c.Key switch
            {
                CategoryKeys.Summary => (HealthStatus, HealthHeadline),
                CategoryKeys.Device => (ComponentStatus.NotChecked, PlatformText),
                CategoryKeys.SpeedTest => SpeedTest.Tile,
                CategoryKeys.SystemTools => DiagnosticTile(),
                _ => (ComponentStatus.NotChecked, NotificationsEnabled ? L.T("Bildirimler açık", "Notifications on") : L.T("Bildirimler kapalı", "Notifications off"))
            };
        }
    }

    public bool IsDashboard
    {
        get => _isDashboard;
        private set
        {
            Set(ref _isDashboard, value);
            OnPropertyChanged(nameof(IsRequirementsPage));
            OnPropertyChanged(nameof(IsHomeScreen));
            OnPropertyChanged(nameof(ShowUpdateOverlay));
            OnPropertyChanged(nameof(ShowOfflineOverlay));
        }
    }

    public bool IsRequirementsPage => !IsDashboard;

    public bool RequirementsChecked { get => _requirementsChecked; private set => Set(ref _requirementsChecked, value); }
    public bool IsSupported { get => _isSupported; private set { Set(ref _isSupported, value); OnPropertyChanged(nameof(ShowUnsupported)); } }
    public bool ShowUnsupported => RequirementsChecked && !IsSupported;
    public bool IsAdmin
    {
        get => _isAdmin;
        private set
        {
            Set(ref _isAdmin, value);
            OnPropertyChanged(nameof(ShowElevate));
            OnPropertyChanged(nameof(AvailableSummary));
        }
    }

    /// <summary>Yönetici değil: gereksinim sayfasında, ana sayfada, kategori sol menüsünde ve Genel Ayarlar → Yönetici Yetkisi'nde "yönetici olarak yeniden başlat" gösterilir.</summary>
    public bool ShowElevate => RequirementsChecked && IsSupported && !IsAdmin;
    public bool Accepted { get => _accepted; set => Set(ref _accepted, value); }
    public string UnsupportedMessage { get => _unsupportedMessage; private set => Set(ref _unsupportedMessage, value); }

    /// <summary>NVIDIA RTX ekran kartı var mı? Yoksa uygulama "kartsız" kullanılır ve NVIDIA Driver kartı kullanım dışıdır.</summary>
    public bool HasRtx
    {
        get => _hasRtx;
        private set
        {
            if (!Set(ref _hasRtx, value)) return;
            OnPropertyChanged(nameof(ShowNoRtx));
            OnPropertyChanged(nameof(ShowContinue));
            OnPropertyChanged(nameof(PlatformText));
            RefreshCategories();
        }
    }

    /// <summary>Ana ekran başlığının altındaki platform satırı.</summary>
    public string PlatformText => HasRtx ? "Windows 11 · NVIDIA RTX" : L.T("Windows 11 · kartsız mod", "Windows 11 · without RTX");

    /// <summary>Windows 11 uygun ancak RTX yok: "Kartsız Devam Et" seçeneği gösterilir.</summary>
    public bool ShowNoRtx => RequirementsChecked && IsSupported && !HasRtx;

    /// <summary>Normal "Devam Et" butonu (RTX varsa ya da sistem desteklenmiyorsa – o durumda buton devre dışıdır).</summary>
    public bool ShowContinue => !ShowNoRtx;

    public string NoRtxMessage { get => _noRtxMessage; private set => Set(ref _noRtxMessage, value); }

    /// <summary>Bir işlem sürüyor mu? Yalnızca <see cref="SetBusy"/> ile değişir (işlem başı ve tek final geçişi).</summary>
    public bool IsBusy => _isBusy;

    /// <summary>Bir işlem gerçekten çalışırken true (ilerleme animasyonları yalnızca bu durumda çalışır).</summary>
    public bool IsOperationRunning => IsBusy;

    public OperationState OperationState
    {
        get => _operationState;
        private set
        {
            if (Set(ref _operationState, value))
                OnPropertyChanged(nameof(OperationStateText));
        }
    }

    public string OperationStateText => OperationState switch
    {
        OperationState.Checking => L.T("Kontrol devam ediyor...", "Check in progress..."),
        OperationState.Updating => L.T("İşlem devam ediyor...", "Operation in progress..."),
        OperationState.Completed => L.T("İşlem tamamlandı", "Operation completed"),
        OperationState.Failed => L.T("İşlem tamamlandı – hata var", "Operation completed – with errors"),
        OperationState.Cancelled => L.T("İşlem iptal edildi", "Operation cancelled"),
        _ => L.T("Hazır", "Ready")
    };

    /// <summary>
    /// "Tümünü Güncelle" yalnızca bu oturumda GERÇEKTEN kontrol edilmiş ve işlem gerektiren kart varsa etkindir.
    /// Kontrol edilmemiş, güncel/sağlıklı veya kontrolü başarısız kartlar güncelleme akışına girmez.
    /// </summary>
    public bool CanUpdateAll =>
        CanStartOperation && IsSupported &&
        _checks.Values.Any(c => c.HasActionableUpdates && (c.Key != ComponentKeys.RecycleBin || CleanRecycleBin));

    /// <summary>"Seçilenleri Güncelle / Çalıştır": seçili kartlardan en az biri kontrol edilmiş ve işlem gerektiriyorsa etkin.</summary>
    public bool CanRunSelected =>
        CanStartOperation && IsSupported &&
        _selection.SelectedKeys.Any(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates);

    public bool IsUpdatePhase { get => _isUpdatePhase; private set => Set(ref _isUpdatePhase, value); }
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public bool CleanRecycleBin
    {
        get => _cleanRecycleBin;
        set { Set(ref _cleanRecycleBin, value); OnPropertyChanged(nameof(UpdateAllText)); RaiseUpdateAvailabilityChanged(); }
    }

    public string UpdateAllText =>
        CleanRecycleBin && _checks.TryGetValue(ComponentKeys.RecycleBin, out var rb) && rb.HasActionableUpdates
            ? L.T("Tümünü Güncelle ve Temizle", "Update and Clean All")
            : L.T("Tümünü Güncelle", "Update All");

    public string AvailableSummary
    {
        get
        {
            if (RequirementsChecked && !IsAdmin)
                return L.T("Yönetici yetkisi yok: tüm kartlar kullanım dışı. Kontrol ve güncelleme için uygulamayı yönetici olarak yeniden başlatın.", "No administrator rights: all cards are unavailable. Restart the app as administrator to run checks and updates.");
            if (SpeedTest?.IsInstalling == true)
                return L.T("Ookla Speedtest aracı kuruluyor. Kontrol ve güncelleme işlemleri kurulum bitince başlatılabilir.", "The Ookla Speedtest tool is being installed. Checks and updates can start when the installation finishes.");
            if (SpeedTest?.IsRunning == true)
                return L.T("Hız testi sürüyor. Ölçümü etkilememesi için kontrol ve güncelleme işlemleri test bitince başlatılabilir.", "A speed test is running. To avoid affecting the measurement, checks and updates can start when the test finishes.");
            if (_checks.Count == 0)
                return _updatesApplied
                    ? L.T("İşlemler uygulandı. Yeni durum için yeniden kontrol edin.", "Operations applied. Check again for the new status.")
                    : L.T("Henüz kontrol yapılmadı. Önce \"Tümünü Kontrol Et\" veya \"Seçilenleri Kontrol Et\" çalıştırılmalı.", "No check has been run yet. Run \"Check All\" or \"Check Selected\" first.");
            var n = _checks.Values.Count(c => c.HasActionableUpdates);
            return n == 0 ? L.T("Kontrol edilen kartlarda uygulanacak işlem yok", "No actions to apply on the checked cards") : L.T($"{n} kartta güncelleme / işlem uygulanmaya hazır", $"Updates / actions ready to apply on {n} card(s)");
        }
    }

    // --- seçim ---
    public int SelectedCount => _selection.Count;
    public bool HasSelection => _selection.HasSelection;
    public string SelectionText => L.T($"{_selection.Count} işlem seçildi", $"{_selection.Count} item(s) selected");

    /// <summary>
    /// Seçilen kartların işlem türüne göre buton metni: yalnızca güncelleme kartları seçiliyse
    /// "Seçilenleri Güncelle", bakım/temizlik kartı da seçiliyse "Seçilenleri Çalıştır".
    /// </summary>
    public string UpdateSelectedText =>
        _selection.SelectedKeys.Any(k => k is ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt
            or ComponentKeys.RecycleBin or ComponentKeys.TempFiles)
            ? L.T("Seçilenleri Çalıştır", "Run Selected")
            : L.T("Seçilenleri Güncelle", "Update Selected");

    public string LogFilePath => _logger.LogFilePath;

    public string AppName => AppInfo.Name;

    public string AppVersion { get; } = "v" + AppInfo.Version;

    /// <summary>Hakkında → Kurulum: bu kopya Program Files'taki kurulu uygulama mı, taşınabilir (ZIP) kopya mı (Uninstall kaydından).</summary>
    public string InstallStatusText => _installStatusText ??= DescribeInstall();
    private string? _installStatusText;

    private string DescribeInstall()
    {
        var layout = InstallLayout.Machine;
        var exe = Environment.ProcessPath;
        if (exe is not null && string.Equals(System.IO.Path.GetFullPath(exe), layout.ExePath, StringComparison.OrdinalIgnoreCase))
            return L.T($"Yüklü · {layout.InstallDir} · kaldırmak için Windows Ayarlar → Uygulamalar", $"Installed · {layout.InstallDir} · to uninstall use Windows Settings → Apps");
        var installed = new InstallerService(layout, _logger.Info).ReadInstalled();
        return installed?.Version is { } version
            ? L.T($"Taşınabilir kopya çalışıyor · yüklü sürüm {version} ({layout.InstallDir})", $"Portable copy running · installed version {version} ({layout.InstallDir})")
            : L.T("Taşınabilir (yüklü değil) · kurmak için E-mre Control Center Setup dosyasını kullanın", "Portable (not installed) · use the E-mre Control Center Setup file to install");
    }

    public ICommand ContinueCommand { get; }
    public ICommand ContinueWithoutGpuCommand { get; }
    public ICommand ElevateCommand { get; }
    public ICommand StartCheckCommand { get; }
    public ICommand CheckSelectedCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand UpdateSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ExportLogsCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand ShowRecentCommand { get; }

    /// <summary>Hız Testi → Sunucu bölmesi ("Değiştir" bağlantısı).</summary>
    public ICommand ShowSpeedServersCommand { get; }
    public ICommand RefreshSystemInfoCommand { get; }

    // ------------------------------------------------------------------ startup

    public async Task InitializeAsync()
    {
        _logger.Info(AppInfo.Name + " v" + AppInfo.Version + L.T(" başlatıldı.", " started."));
        var internetCheck = RefreshInternetAsync(); // gereksinim kontrolüyle aynı anda (ağ isteği beklerken WMI okunur)
        AutoDeleteOldLogsAtStartup();
        RequirementsResult req;
        try
        {
            req = await _requirements.CheckAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(L.T("Sistem gereksinimleri kontrol edilemedi: ", "System requirements could not be checked: ") + ex.Message);
            req = new RequirementsResult { Error = ex.Message, OsDescription = L.T("Okunamadı", "Unreadable"), GpuDescription = L.T("Okunamadı", "Unreadable") };
        }

        if (!ApplyRequirements(req)) return;
        await internetCheck;
        StartInternetMonitoring();

        // Sistem bilgileri arka planda okunur; arayüz beklemez.
        _ = RefreshSystemInfoAsync();

        // Yeni sürüm denetimi (arka planda; sonuç gelince zorunlu güncelleme penceresi girişten sonra gösterilir).
        if (UpdateViewModel.AutoCheckEnabled)
        {
            _ = Update.CheckAsync();
            Update.StartPeriodicChecks(); // uygulama açıkken de: yeni sürüm yayınlanınca yeniden başlatmayı beklemeden gelir
        }

        if (_argAccepted && !IsLegalAccepted)
        {
            // Güncellemeden sonra ilk açılış ve yasal belgeler yeni / değişmiş: giriş kendiliğinden yapılmaz, kullanıcı gereksinim
            // sayfasındaki kutuyla Kullanım Koşulları ve Gizlilik Politikası'nı kabul eder (bir kez).
            _logger.Info(L.T($"Kullanım Koşulları ve Gizlilik Politikası (sürüm {AppInfo.LegalVersion}) bu bilgisayarda henüz kabul edilmedi: ", $"The Terms of Use and Privacy Policy (version {AppInfo.LegalVersion}) have not been accepted on this computer yet: ") +
                         L.T("gereksinim sayfası gösteriliyor.", "showing the requirements page."));
        }
        else if (_argAccepted)
        {
            Accepted = true;
            if (HasInternet)
            {
                await EnterAcceptedAsync();
            }
            else
            {
                // İnternet yok: gereksinim sayfası gösterilir; bağlantı gelince giriş kendiliğinden tamamlanır.
                _pendingAcceptedEntry = true;
                _logger.Warning(L.T("İnternet bağlantısı yok: gereksinim sayfası gösteriliyor; bağlantı gelince uygulama açılacak.", "No internet connection: showing the requirements page; the app will open when the connection is back."));
            }
            return;
        }

        if (!IsAdmin)
            await PromptElevationAsync(false);
    }

    /// <summary>"--accepted" ile açılışta ana ekrana giriş (güncelleme / UAC sonrası). İnternet varsa hemen, yoksa bağlantı gelince.</summary>
    private async Task EnterAcceptedAsync()
    {
        GoToDashboard();
        if (!IsDashboard) return;
        await ShowUpdatedOnceAsync();
        if (_argStartCheck && IsAdmin)
            await StartCheckAsync();
    }

    /// <summary>Gereksinim sayfasındaki "Devam Et" / "Kartsız Devam Et".</summary>
    private void EnterFromRequirements()
    {
        GoToDashboard();
        if (IsDashboard) _ = ShowUpdatedOnceAsync();
    }

    private bool _updatedShown;

    /// <summary>Uygulama içi güncellemeden sonraki ilk açılış: güncellemenin gerçekten yapıldığı gösterilir (bir kez).</summary>
    private async Task ShowUpdatedOnceAsync()
    {
        if (_updatedShown || DescribeUpdate(UpdatedFrom) is not { } updated) return;
        _updatedShown = true;
        _logger.Success(updated);
        await Dialog.ShowAsync(L.T("Güncelleme tamamlandı", "Update completed"), updated + L.T(" Ayarlarınız, geçmişiniz ve günlükleriniz korundu.", " Your settings, history and logs have been kept."),
            Icons.Check, DialogKind.Info, L.T("Tamam", "OK"));
    }

    private static string NoAdminReason =>
        L.T("Yönetici yetkisi yok. Kontrol ve güncelleme için uygulamayı \"Yönetici olarak yeniden başlat\" ile açın (UAC onayı).", "No administrator rights. Open the app with \"Restart as administrator\" to run checks and updates (UAC approval).");

    /// <summary>
    /// Gereksinim kontrolünün gerçek sonucunu gereksinim sayfasına ve kartlara uygular. Windows 11 değilse false döner
    /// (giriş engellenir). RTX yoksa uygulama engellenmez: NVIDIA Driver kartı ve modülü kullanım dışı yapılır.
    /// Yönetici yetkisi yoksa uygulamaya girilebilir ama tüm kartlar kullanım dışıdır.
    /// </summary>
    private bool ApplyRequirements(RequirementsResult req)
    {
        WindowsRow.State = req.IsWindows11 ? RequirementState.Ok : RequirementState.Failed;
        WindowsRow.Detail = req.OsDescription;
        // RTX yoksa uygulama engellenmez: GPU satırı uyarı olur ve "Kartsız Devam Et" sunulur (yalnızca NVIDIA kartı kullanım dışı).
        GpuRow.State = req.HasRtxGpu ? RequirementState.Ok : RequirementState.Warning;
        GpuRow.Detail = req.GpuDescription;
        IsAdmin = req.IsAdministrator;
        AdminRow.State = req.IsAdministrator ? RequirementState.Ok : RequirementState.Failed;
        AdminRow.Detail = req.IsAdministrator ? L.T("Yönetici olarak çalışıyor", "Running as administrator") : L.T("Yönetici olarak çalışmıyor – tüm kartlar kullanım dışı", "Not running as administrator – all cards unavailable");

        HasRtx = req.HasRtxGpu;
        if (!req.HasRtxGpu)
        {
            NoRtxMessage = L.T("NVIDIA GeForce RTX ekran kartı bulunamadı. Algılanan: ", "No NVIDIA GeForce RTX graphics card found. Detected: ") + req.GpuDescription + ".\n" +
                           L.T("Uygulamayı \"Kartsız Devam Et\" ile kullanabilirsiniz: NVIDIA Driver kartı kullanım dışı olur, ", "You can use the app with \"Continue without RTX\": the NVIDIA Driver card becomes unavailable, ") +
                           L.T("diğer tüm işlemler normal çalışır.", "everything else works normally.");
            var nvidia = Cards.First(c => c.Key == ComponentKeys.Nvidia);
            nvidia.MarkUnavailable(L.T("NVIDIA GeForce RTX ekran kartı bulunamadı. Algılanan: ", "No NVIDIA GeForce RTX graphics card found. Detected: ") + req.GpuDescription + ". " +
                                   L.T("NVIDIA sürücü kontrolü ve güncellemesi yapılmaz.", "NVIDIA driver checks and updates are not performed."));
            _orchestrator.SetUnavailable(ComponentKeys.Nvidia);
            RecomputeHealth();
        }

        if (!req.IsAdministrator)
        {
            // Yönetici yetkisi olmadan hiçbir kontrol / güncelleme çalıştırılamaz: 10 kartın tamamı kullanım dışıdır.
            // Yetki yalnızca "Yönetici olarak (yeniden) başlat" → UAC onayıyla alınır (yeni işlem; UAC atlatılmaz).
            foreach (var card in Cards)
            {
                card.MarkUnavailable(NoAdminReason);
                _orchestrator.SetUnavailable(card.Key);
            }
            RecomputeHealth();
        }

        IsSupported = req.IsSupported;
        RequirementsChecked = true;
        OnPropertyChanged(nameof(AvailableSummary));
        OnPropertyChanged(nameof(ShowUnsupported));
        OnPropertyChanged(nameof(ShowElevate));
        OnPropertyChanged(nameof(ShowNoRtx));
        OnPropertyChanged(nameof(ShowContinue));
        OnPropertyChanged(nameof(ShowNoInternet));

        if (!req.IsSupported)
        {
            // Windows 11 zorunludur: uygun değilse uygulamanın hiçbir işlemi kullanılamaz (giriş engellenir).
            UnsupportedMessage = L.T("Bu uygulama bu sistem için desteklenmiyor.\n", "This app is not supported on this system.\n") +
                                 L.T("Windows 11 gerekli (algılanan: ", "Windows 11 is required (detected: ") + req.OsDescription + L.T("). Uygulamanın hiçbir işlemi kullanılamaz.", "). None of the app's operations can be used.");
            _logger.Error(L.T("Sistem desteklenmiyor (Windows 11 değil); devam edilemez.", "System not supported (not Windows 11); cannot continue."));
            CommandManager.InvalidateRequerySuggested();
            return false;
        }

        CommandManager.InvalidateRequerySuggested();
        return true;
    }

    private void GoToDashboard()
    {
        if (!Accepted || !IsSupported || !HasInternet) return;
        RecordLegalAcceptance();
        IsDashboard = true;
        if (HasRtx) _logger.Info(L.T("Ana ekran açıldı.", "Main screen opened."));
        else _logger.Warning(L.T("Ana ekran kartsız açıldı: NVIDIA RTX ekran kartı yok, NVIDIA Driver kartı kullanım dışı.", "Main screen opened without RTX: no NVIDIA RTX graphics card, the NVIDIA Driver card is unavailable."));
        if (!IsAdmin)
            _logger.Warning(L.T("Yönetici yetkisi yok: tüm kartlar kullanım dışı. Kontrol ve güncelleme için \"Yönetici olarak yeniden başlat\" kullanılmalı.", "No administrator rights: all cards are unavailable. Use \"Restart as administrator\" to run checks and updates."));
    }

    // ------------------------------------------------------------------ elevation

    private async Task PromptElevationAsync(bool startCheckAfter)
    {
        var ok = await Dialog.ShowAsync(
            L.T("Yönetici izni gerekiyor", "Administrator permission required"),
            AppInfo.Name + L.T("; Windows Update, NVIDIA sürücüsü, uygulama ve Defender güncellemelerini kurabilmek, ", " needs administrator rights to install Windows Update, NVIDIA driver, app and Defender updates ") +
            L.T("SFC / DISM / MRT sistem bakım araçlarını çalıştırabilmek için yönetici yetkisine ihtiyaç duyar.\n\n", "and to run the SFC / DISM / MRT system maintenance tools.\n\n") +
            L.T("Devam ettiğinizde Windows, \"Bu uygulamanın cihazınızda değişiklik yapmasına izin veriyor musunuz?\" sorusunu soran UAC penceresini açacak. ", "When you continue, Windows opens the UAC window asking \"Do you want to allow this app to make changes to your device?\". ") +
            L.T("Bu izin yalnızca sistem güncellemeleri ve bakım işlemleri için kullanılır. İzin vermezseniz sistemde hiçbir değişiklik yapılmaz.", "This permission is used only for system updates and maintenance. If you decline, nothing on the system is changed."),
            Icons.Shield, DialogKind.Question, L.T("Yönetici olarak başlat", "Start as administrator"), L.T("Şimdi değil", "Not now"));

        if (!ok)
        {
            _logger.Warning(L.T("Kullanıcı yönetici olarak yeniden başlatmayı erteledi.", "The user postponed restarting as administrator."));
            return;
        }

        _logger.Info(L.T("Windows UAC onayı isteniyor...", "Requesting Windows UAC approval..."));
        // Kutu işaretliyse kabul yeni örnekten önce kaydedilir (yeni örnek "--accepted" ile gereksinim sayfasını atlar).
        if (Accepted) RecordLegalAcceptance();
        var args = Accepted || IsDashboard
            ? (startCheckAfter
                ? new[] { AdminPrivilegeManager.ArgAccepted, AdminPrivilegeManager.ArgStartCheck }
                : new[] { AdminPrivilegeManager.ArgAccepted })
            : Array.Empty<string>();

        var (outcome, error) = AdminPrivilegeManager.RelaunchElevated(args);
        switch (outcome)
        {
            case ElevationOutcome.Started:
                _logger.Success(L.T("Uygulama yönetici olarak yeniden başlatılıyor.", "Restarting the app as administrator."));
                AppLifetime.Exit();
                break;
            case ElevationOutcome.Declined:
                _logger.Error(L.T("Yönetici izni reddedildi. Sistem üzerinde değişiklik yapılmayacak.", "Administrator permission was denied. No changes will be made to the system."));
                AdminRow.Detail = L.T("UAC izni reddedildi – tüm kartlar kullanım dışı", "UAC permission denied – all cards unavailable");
                await Dialog.ShowAsync(L.T("Yönetici izni reddedildi", "Administrator permission denied"),
                    L.T("UAC penceresinde izin verilmedi. Yönetici yetkisi olmadan uygulamaya girebilirsiniz ancak tüm kartlar kullanım dışı ", "Permission was not granted in the UAC window. You can enter the app without administrator rights, but all cards will be unavailable ") +
                    L.T("olur; hiçbir kontrol veya güncelleme yapılamaz. Sistemde herhangi bir değişiklik yapılmadı.\n\n", "and no checks or updates can be run. No changes were made to the system.\n\n") +
                    L.T("İstediğiniz zaman \"Yönetici olarak yeniden başlat\" butonuyla tekrar deneyebilirsiniz.", "You can try again at any time with the \"Restart as administrator\" button."),
                    Icons.Warning, DialogKind.Warning, L.T("Tamam", "OK"));
                break;
            default:
                _logger.Error(error ?? L.T("Yönetici olarak yeniden başlatılamadı.", "Could not restart as administrator."));
                await Dialog.ShowAsync(L.T("Yeniden başlatılamadı", "Could not restart"), error ?? L.T("Bilinmeyen hata.", "Unknown error."), Icons.Warning, DialogKind.Warning, L.T("Tamam", "OK"));
                break;
        }
    }

    /// <summary>Yönetici değilse izin ister ve false döner (işlem yapılmaz).</summary>
    private async Task<bool> EnsureElevatedAsync(bool startCheckAfter)
    {
        if (AdminPrivilegeManager.IsElevated) return true;
        _logger.Warning(L.T("Bu işlem için yönetici yetkisi gerekiyor.", "Administrator rights are required for this operation."));
        await PromptElevationAsync(startCheckAfter);
        return false;
    }

    // ------------------------------------------------------------------ TÜMÜNÜ KONTROL ET

    private async Task StartCheckAsync()
    {
        if (!CanStartOperation || !await EnsureElevatedAsync(startCheckAfter: true)) return;

        foreach (var c in Cards) c.Reset();
        _checks.Clear();
        _updatesApplied = false;
        RaiseSummaryChanged();

        Dictionary<string, ModuleResult>? results = null;
        string? diagnostics = null;
        var completed = await RunBusyAsync(updatePhase: false,
            async ct =>
            {
                // Kart kontrolleri ilerlemenin %70'i, ardından güvenli tanılama adımları (yalnızca okuma) kalan %30.
                _progressScale = 0.7;
                try { results = await _orchestrator.RunAllChecksAsync(Reporters(), ct); }
                finally { _progressScale = 1; }
                diagnostics = await RunQuickDiagnosticsAsync(70, ct);
            },
            done =>
            {
                StoreChecks(results);
                var anyError = FinishOperation(L.T("Sistem kontrolü", "System check"), results, done, updatePhase: false, single: false, NotifyPolicy.Always, diagnostics);
                var ready = _checks.Values.Any(c => c.HasActionableUpdates);
                return new OperationEnd(anyError,
                    ready ? L.T("Kontrol tamamlandı – işlemler hazır", "Check completed – actions ready") : L.T("Kontrol tamamlandı – uygulanacak işlem yok", "Check completed – nothing to apply"),
                    ready ? L.T("Kontrol tamamlandı – işlemler hazır, bazı kontroller başarısız", "Check completed – actions ready, some checks failed") : L.T("Kontrol tamamlandı – bazı kontroller başarısız oldu", "Check completed – some checks failed"),
                    L.T("Kontrol iptal edildi", "Check cancelled"));
            });
        if (!completed) return;

        if (!_checks.Values.Any(c => c.HasActionableUpdates))
        {
            // Otomatik uygulanacak bir şey yoksa, varsa manuel güncellemeler (bu oturumda henüz sorulmamışsa) sunulur.
            if (await OfferManualUpdatesAsync(_checks.Values.ToList(), onlyNotOffered: true))
            {
                await ShowResultsAsync(L.T("İşlem Tamamlandı", "Operation Completed"), L.T("Aşağıdaki sonuçlar sistemden okunan gerçek durumu gösterir.", "The results below show the real status read from the system."));
                return;
            }
            await ShowResultsAsync(L.T("Kontrol Tamamlandı", "Check Completed"), OperationState == OperationState.Failed
                ? L.T("Kontroller tamamlandı ancak bazıları başarısız oldu (nedenleri aşağıda). Başarılı kontrollerde uygulanacak bir işlem bulunamadı.", "The checks completed but some of them failed (reasons below). No action was found to apply in the successful checks.")
                : L.T("Tüm kontroller gerçek sistem verileriyle tamamlandı. Uygulanacak bir güncelleme veya işlem bulunamadı.", "All checks completed with real system data. No update or action was found to apply."));
        }
        else
        {
            _logger.Info(L.T("İşlemleri uygulamak için \"", "To apply the actions, use the \"") + UpdateAllText + L.T("\" veya \"", "\" or \"") + UpdateSelectedText + L.T("\" butonunu kullanın.", "\" button."));
        }
    }

    // ------------------------------------------------------------------ SEÇİLENLERİ KONTROL ET

    private async Task CheckSelectedAsync()
    {
        if (!CanStartOperation) return;
        var keys = _selection.SelectedKeys;
        if (keys.Count == 0)
        {
            await ShowNoSelectionAsync();
            return;
        }
        if (!await EnsureElevatedAsync(startCheckAfter: false)) return;

        foreach (var c in Cards.Where(c => keys.Contains(c.Key))) c.Reset();
        foreach (var k in keys) _checks.Remove(k);
        RaiseSummaryChanged();

        Dictionary<string, ModuleResult>? results = null;
        var completed = await RunBusyAsync(updatePhase: false,
            async ct => results = await _orchestrator.RunSelectedChecksAsync(keys, Reporters(), ct),
            done =>
            {
                StoreChecks(results);
                var anyError = FinishOperation(L.T("Seçilen kontroller", "Selected checks"), results, done, updatePhase: false, single: false, NotifyPolicy.Always);
                var ready = keys.Any(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates);
                return new OperationEnd(anyError,
                    ready ? L.T("Seçilen kontroller tamamlandı – işlemler hazır", "Selected checks completed – actions ready") : L.T("Seçilen kontroller tamamlandı – uygulanacak işlem yok", "Selected checks completed – nothing to apply"),
                    ready ? L.T("Seçilen kontroller tamamlandı – işlemler hazır, bazı kontroller başarısız", "Selected checks completed – actions ready, some checks failed") : L.T("Seçilen kontroller tamamlandı – bazıları başarısız oldu", "Selected checks completed – some failed"),
                    L.T("Kontrol iptal edildi", "Check cancelled"));
            });
        if (!completed) return;

        if (!keys.Any(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates))
        {
            var selectedChecks = keys.Where(_checks.ContainsKey).Select(k => _checks[k]).ToList();
            if (await OfferManualUpdatesAsync(selectedChecks, onlyNotOffered: true))
            {
                await ShowResultsAsync(L.T("İşlem Tamamlandı", "Operation Completed"), L.T("Seçilen kartların sistemden okunan gerçek sonuçları:", "Real results of the selected cards read from the system:"), keys);
                return;
            }
            await ShowResultsAsync(L.T("Kontrol Tamamlandı", "Check Completed"), OperationState == OperationState.Failed
                ? L.T("Seçilen kontrollerin bazıları başarısız oldu (nedenleri aşağıda). Başarılı kontrollerde uygulanacak bir işlem bulunamadı.", "Some of the selected checks failed (reasons below). No action was found to apply in the successful checks.")
                : L.T("Seçilen kartlar gerçek sistem verileriyle kontrol edildi. Uygulanacak bir güncelleme veya işlem bulunamadı.", "The selected cards were checked with real system data. No update or action was found to apply."), keys);
        }
        else
        {
            _logger.Info(L.T("Seçilen kartlardaki işlemleri uygulamak için \"", "To apply the actions on the selected cards, use the \"") + UpdateSelectedText + L.T("\" butonunu kullanın.", "\" button."));
        }
    }

    // ------------------------------------------------------------------ TÜMÜNÜ GÜNCELLE

    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll || !await EnsureElevatedAsync(startCheckAfter: false)) return;

        var keys = _orchestrator.ModuleOrder
            .Where(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates && (k != ComponentKeys.RecycleBin || CleanRecycleBin))
            .ToList();
        var bullets = keys.Select(k => BuildBullet(k, _checks[k])).ToList();
        var choices = BuildTempChoices(keys);

        var ok = await Dialog.ShowAsync(
            L.T("Güncellemeleri onaylayın", "Confirm the updates"),
            L.T("Aşağıdaki işlemler sırayla gerçekleştirilecek. Yalnızca kontrolde işlem gerektirdiği görülen bileşenlere dokunulur. ", "The following operations will run one after another. Only the components that the check found to need action are touched. ") +
            L.T("Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.", "Your computer will not restart without your approval."),
            Icons.Download, DialogKind.Question, L.T("Onayla ve başlat", "Confirm and start"), L.T("Vazgeç", "Cancel"), bullets,
            choices: choices, choicesTitle: choices is null ? null : L.T("TEMİZLENECEK GEÇİCİ DOSYA KATEGORİLERİ", "TEMPORARY FILE CATEGORIES TO CLEAN"));
        if (!ok)
        {
            _logger.Info(L.T("Kullanıcı güncellemeyi iptal etti; hiçbir değişiklik yapılmadı.", "The user cancelled the update; no changes were made."));
            return;
        }
        ApplyTempChoices(choices);

        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? results = null;
        await RunBusyAsync(updatePhase: true,
            async ct => results = await _orchestrator.RunAllUpdatesAsync(snapshot, CleanRecycleBin, Reporters(), ct),
            done =>
            {
                StoreUpdates(results);
                var anyError = FinishOperation(L.T("Güncelleme işlemleri", "Update operations"), results, done, updatePhase: true, single: false, NotifyPolicy.Always);
                return new OperationEnd(anyError, L.T("Güncelleme işlemleri tamamlandı", "Update operations completed"), L.T("Güncelleme işlemleri bitti – bazı işlemler başarısız oldu", "Update operations finished – some operations failed"));
            });

        await OfferInUseRetryAsync(results);
        // Otomatik uygulanmayan güncellemeler (varsa) ayrıca ve seçmeli olarak sunulur.
        await OfferManualUpdatesAsync(snapshot.Values.ToList(), onlyNotOffered: false);
        await ShowResultsAsync(L.T("İşlem Tamamlandı", "Operation Completed"), L.T("Aşağıdaki sonuçlar sistemden okunan gerçek durumu gösterir.", "The results below show the real status read from the system."));
    }

    // ------------------------------------------------------------------ SEÇİLENLERİ GÜNCELLE / ÇALIŞTIR

    private async Task UpdateSelectedAsync()
    {
        if (!CanStartOperation) return;
        var keys = _selection.SelectedKeys;
        if (keys.Count == 0)
        {
            await ShowNoSelectionAsync();
            return;
        }
        if (!await EnsureElevatedAsync(startCheckAfter: false)) return;

        // 1) Kontrol edilmemiş kart güncellenemez: bu oturumda gerçek kontrol sonucu olmayan seçili kartlar atlanır.
        var unchecked_ = keys.Where(k => !_checks.ContainsKey(k)).ToList();
        foreach (var k in unchecked_)
            _logger.Warning(L.T($"{_orchestrator.NameOf(k)}: bu oturumda kontrol edilmediği için atlandı (önce kontrol edilmeli).", $"{_orchestrator.NameOf(k)}: skipped because it was not checked in this session (check it first)."));
        // 2) Seçilenlerden gerçekten işlem gerektirenleri belirle.
        var actionable = keys.Where(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates).ToList();
        var notNeeded = keys.Except(actionable)
            .Select(k => $"{_orchestrator.NameOf(k)} ({(_checks.TryGetValue(k, out var c) ? c.Summary : L.T("kontrol edilmedi", "not checked"))})")
            .ToList();

        if (actionable.Count == 0)
        {
            if (await OfferManualUpdatesAsync(keys.Where(_checks.ContainsKey).Select(k => _checks[k]).ToList(), onlyNotOffered: false))
            {
                await ShowResultsAsync(L.T("İşlem Tamamlandı", "Operation Completed"), L.T("Seçilen kartların sistemden okunan gerçek sonuçları:", "Real results of the selected cards read from the system:"), keys);
                return;
            }
            StepText = L.T("Seçilen kartlarda uygulanacak işlem yok", "No actions to apply on the selected cards");
            _logger.Info(L.T("Seçilen kartların hiçbiri işlem gerektirmiyor; hiçbir şey çalıştırılmadı.", "None of the selected cards needs action; nothing was run."));
            await ShowResultsAsync(L.T("Yapılacak işlem yok", "Nothing to do"),
                L.T("Seçilen kartlarda güncelleme veya işlem gerektiren bir durum bulunamadı. Hiçbir işlem çalıştırılmadı.", "No update or action is needed on the selected cards. No operation was run."), keys);
            return;
        }

        var message = L.T("Yalnızca seçtiğiniz kartlardan işlem gerektirenler sırayla çalıştırılacak. Seçilmeyen kartlara dokunulmaz. ", "Only the selected cards that need action will run, one after another. Cards that are not selected are not touched. ") +
                      L.T("Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.", "Your computer will not restart without your approval.");
        if (notNeeded.Count > 0)
            message += L.T("\n\nİşlem gerektirmediği için atlanacak: ", "\n\nSkipped because no action is needed: ") + string.Join(", ", notNeeded) + ".";

        var selectedChoices = BuildTempChoices(actionable);
        var ok = await Dialog.ShowAsync(
            UpdateSelectedText + L.T(" – onay", " – confirmation"),
            message,
            Icons.Play, DialogKind.Question, L.T("Onayla ve başlat", "Confirm and start"), L.T("Vazgeç", "Cancel"),
            actionable.Select(k => BuildBullet(k, _checks[k])),
            choices: selectedChoices, choicesTitle: selectedChoices is null ? null : L.T("TEMİZLENECEK GEÇİCİ DOSYA KATEGORİLERİ", "TEMPORARY FILE CATEGORIES TO CLEAN"));
        if (!ok)
        {
            _logger.Info(L.T("Kullanıcı seçilen işlemleri iptal etti; hiçbir değişiklik yapılmadı.", "The user cancelled the selected operations; no changes were made."));
            return;
        }
        ApplyTempChoices(selectedChoices);

        // 3) Yalnızca seçilenleri çalıştır.
        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? results = null;
        await RunBusyAsync(updatePhase: true,
            async ct => results = await _orchestrator.RunSelectedUpdatesAsync(snapshot, actionable, Reporters(), ct),
            done =>
            {
                StoreUpdates(results);
                var anyError = FinishOperation(L.T("Seçilen işlemler", "Selected operations"), results, done, updatePhase: true, single: false, NotifyPolicy.Always);
                return new OperationEnd(anyError, L.T("Seçilen işlemler tamamlandı", "Selected operations completed"), L.T("Seçilen işlemler bitti – bazı işlemler başarısız oldu", "Selected operations finished – some operations failed"));
            });

        await OfferInUseRetryAsync(results);
        await OfferManualUpdatesAsync(keys.Where(snapshot.ContainsKey).Select(k => snapshot[k]).ToList(), onlyNotOffered: false);
        await ShowResultsAsync(L.T("İşlem Tamamlandı", "Operation Completed"), L.T("Seçilen kartların sistemden okunan gerçek sonuçları:", "Real results of the selected cards read from the system:"), keys);
    }

    // ------------------------------------------------------------------ KART "KONTROL ET" BUTONU

    /// <summary>
    /// Güncelleme kartlarının (Windows Update, Winget, Store, NVIDIA, Defender, Geçici Dosyalar, Çöp Kutusu) kendi butonu:
    /// yalnızca o kartı gerçekten kontrol eder. İşlem gerekiyorsa uygulamak için ayrıca onay istenir.
    /// </summary>
    private async Task RunCardCheckAsync(ComponentCardViewModel card)
    {
        if (!CanStartOperation || !await EnsureElevatedAsync(startCheckAfter: false)) return;

        card.Reset();
        _checks.Remove(card.Key);
        RaiseSummaryChanged();

        Dictionary<string, ModuleResult>? results = null;
        var completed = await RunBusyAsync(updatePhase: false,
            async ct => results = await _orchestrator.RunSingleCheckAsync(card.Key, Reporters(), ct),
            done =>
            {
                StoreChecks(results);
                var anyError = FinishOperation(L.T($"{card.ShortTitle} kontrolü", $"{card.ShortTitle} check"), results, done, updatePhase: false, single: true, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, card.Title + L.T(": kontrol tamamlandı", ": check completed"), card.Title + L.T(": kontrol başarısız oldu", ": check failed"), L.T("Kontrol iptal edildi", "Check cancelled"));
            });
        if (!completed || !_checks.TryGetValue(card.Key, out var check)) return;
        if (!check.HasActionableUpdates)
        {
            // Kartın kendi butonu açık bir kullanıcı isteğidir: otomatik uygulanmayan güncellemeler her seferinde sunulur.
            if (HasManualUpdates(check) && await OfferManualUpdatesAsync([check], onlyNotOffered: false))
                await ShowResultsAsync(card.Title, L.T("Sistemden okunan gerçek sonuç:", "Real result read from the system:"), [card.Key]);
            return;
        }

        var isRecycle = card.Key == ComponentKeys.RecycleBin;
        var isTemp = card.Key == ComponentKeys.TempFiles;
        var cardChoices = BuildTempChoices([card.Key]);
        var tempMessage = L.T("Geçici dosyalar temizlenecek.\n\nTahmini temizlenecek alan: ", "Temporary files will be cleaned.\n\nEstimated space to free: ") +
                          check.Summary.Replace(TemporaryFilesManager.CleanablePrefix, "") +
                          (string.IsNullOrWhiteSpace(check.Reason) ? string.Empty : "\n\n" + check.Reason) +
                          L.T("\n\nYalnızca seçili kategoriler temizlenir; kullanımdaki ve son 24 saatte değişen dosyalara dokunulmaz. ", "\n\nOnly the selected categories are cleaned; files in use and files changed in the last 24 hours are not touched. ") +
                          L.T("Temizlikten sonra alan yeniden ölçülür. Devam etmek istiyor musunuz?", "The space is measured again after cleaning. Do you want to continue?");
        var ok = await Dialog.ShowAsync(
            isRecycle ? L.T("Çöp kutusu temizlensin mi?", "Empty the Recycle Bin?") : isTemp ? L.T("Geçici dosyalar temizlensin mi?", "Clean temporary files?") : card.Title + L.T(": güncelleme mevcut", ": update available"),
            isRecycle
                ? L.T("Çöp kutusundaki öğeler kalıcı olarak silinecek. Bu işlem geri alınamaz.", "The items in the Recycle Bin will be deleted permanently. This cannot be undone.")
                : isTemp
                    ? tempMessage
                    : L.T("Kontrol sonucunda aşağıdaki işlem bulundu. Şimdi uygulamak ister misiniz? ", "The check found the following action. Do you want to apply it now? ") +
                      L.T("Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.", "Your computer will not restart without your approval."),
            isRecycle || isTemp ? Icons.RecycleBin : Icons.Download, DialogKind.Question,
            isRecycle || isTemp ? L.T("Temizle", "Clean") : L.T("Şimdi güncelle", "Update now"), L.T("Şimdi değil", "Not now"),
            isTemp ? null : [BuildBullet(card.Key, check)],
            choices: cardChoices, choicesTitle: cardChoices is null ? null : L.T("KATEGORİLER", "CATEGORIES"));
        if (!ok)
        {
            _logger.Info(L.T($"{card.Title}: kullanıcı işlemi erteledi; hiçbir değişiklik yapılmadı.", $"{card.Title}: the user postponed the operation; no changes were made."));
            return;
        }
        ApplyTempChoices(cardChoices);

        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? updateResults = null;
        await RunBusyAsync(updatePhase: true,
            async ct => updateResults = await _orchestrator.RunSingleUpdateAsync(snapshot, card.Key, Reporters(), ct),
            done =>
            {
                StoreUpdates(updateResults);
                var anyError = FinishOperation(L.T($"{card.ShortTitle} işlemi", $"{card.ShortTitle} operation"), updateResults, done, updatePhase: true, single: true, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, card.Title + L.T(": işlem tamamlandı", ": operation completed"), card.Title + L.T(": işlem başarısız oldu", ": operation failed"));
            });

        await OfferInUseRetryAsync(updateResults);
        await OfferManualUpdatesAsync([check], onlyNotOffered: false);
        await ShowResultsAsync(card.Title, L.T("Sistemden okunan gerçek sonuç:", "Real result read from the system:"), [card.Key]);
    }

    // ------------------------------------------------------------------ BAKIM KARTI BUTONLARI

    private async Task RunMaintenanceAsync(ComponentCardViewModel card)
    {
        if (!CanStartOperation || !await EnsureElevatedAsync(startCheckAfter: false)) return;

        if (card.Key == ComponentKeys.Sfc)
        {
            var ok = await Dialog.ShowAsync(
                L.T("Sistem dosyası taraması", "System file scan"),
                L.T("SFC /SCANNOW, Windows sistem dosyalarını tarar ve bozuk dosya bulursa bunları Windows'un kendi bileşen deposundan onarmayı dener.\n\n", "SFC /SCANNOW scans Windows system files and, if it finds corrupt files, tries to repair them from Windows' own component store.\n\n") +
                L.T("İşlem 10-30 dakika sürebilir. Onarım başladıktan sonra yarıda kesilmez; bu sırada bilgisayarı kapatmayın.", "It can take 10-30 minutes. Once the repair starts it is not interrupted; do not turn off the computer meanwhile."),
                Icons.Sfc, DialogKind.Question, L.T("Taramayı başlat", "Start scan"), L.T("Vazgeç", "Cancel"));
            if (!ok)
            {
                _logger.Info(L.T("[SFC] Kullanıcı taramayı başlatmadı.", "[SFC] The user did not start the scan."));
                return;
            }
        }

        var scope = card.Key switch
        {
            ComponentKeys.Sfc => L.T("SFC taraması", "SFC scan"),
            ComponentKeys.Mrt => L.T("MRT hızlı taraması", "MRT quick scan"),
            _ => L.T("DISM sağlık kontrolü", "DISM health check")
        };
        ModuleResult? result = null;
        await RunBusyAsync(updatePhase: card.Key == ComponentKeys.Sfc,
            async ct => result = await _orchestrator.RunMaintenanceActionAsync(card.Key, Reporters(), ct),
            done =>
            {
                if (result is null) return new OperationEnd(false, card.Title + L.T(" tamamlandı", " completed"));
                var anyError = FinishOperation(scope, new Dictionary<string, ModuleResult> { [card.Key] = result },
                    done && result.Status != ComponentStatus.Skipped, updatePhase: false, single: true,
                    card.Key == ComponentKeys.Dism ? NotifyPolicy.IfLong : NotifyPolicy.Always);
                if (card.Key == ComponentKeys.Sfc)
                {
                    // /scannow bir onarım işlemidir; önceki kontrol sonucu artık geçersiz.
                    _checks.Remove(ComponentKeys.Sfc);
                    _updatesApplied = true;
                }
                else if (result.Status != ComponentStatus.Skipped)
                {
                    _checks[card.Key] = result;
                }
                return new OperationEnd(anyError, card.Title + L.T(" tamamlandı", " completed"), card.Title + L.T(": işlem başarısız oldu", ": operation failed"));
            });
        if (result is null) return;

        // MRT tehdit bulduysa temizlik yalnızca açık onayla yapılır.
        if (card.Key == ComponentKeys.Mrt && result.HasActionableUpdates)
        {
            var clean = await Dialog.ShowAsync(
                L.T("Kötü amaçlı yazılım tespit edildi", "Malicious software detected"),
                L.T("MRT hızlı taraması tehdit tespit etti:\n", "The MRT quick scan detected threats:\n") + result.Details +
                L.T("\n\nTespit edilenleri kaldırmak için MRT hızlı taraması temizleme modunda çalıştırılacak. Onaylıyor musunuz?", "\n\nTo remove what was detected, the MRT quick scan will run in removal mode. Do you approve?"),
                Icons.Warning, DialogKind.Warning, L.T("Temizle", "Clean"), L.T("Şimdi değil", "Not now"));
            if (clean)
                await RunSingleFollowUpAsync(card, L.T("MRT temizliği", "MRT removal"));
            else
                _logger.Warning(L.T("[MRT] Kullanıcı temizliği erteledi; tespit edilen tehditlere dokunulmadı.", "[MRT] The user postponed the removal; the detected threats were not touched."));
        }

        // DISM "onarılabilir" dediyse onarım (RestoreHealth) yalnızca açık onayla yapılır.
        if (card.Key == ComponentKeys.Dism && result.HasActionableUpdates)
        {
            var repair = await Dialog.ShowAsync(
                L.T("Windows bileşen deposu onarılsın mı?", "Repair the Windows component store?"),
                L.T("DISM CheckHealth, Windows bileşen deposunda onarılabilir bozulma buldu.\n\n", "DISM CheckHealth found repairable corruption in the Windows component store.\n\n") +
                L.T("Onaylarsanız ", "If you approve, ") + DismManager.RepairCommand + L.T(" çalıştırılır: bozuk bileşenler Windows Update'ten alınan temiz ", " runs: corrupt components are repaired with clean ") +
                L.T("dosyalarla onarılır. İşlem 10-60 dakika sürebilir ve başladıktan sonra yarıda kesilmez. ", "files from Windows Update. It can take 10-60 minutes and is not interrupted once started. ") +
                L.T("Onarımdan sonra bileşen deposu yeniden kontrol edilerek sonuç doğrulanır.", "After the repair the component store is checked again to verify the result."),
                Icons.Dism, DialogKind.Question, L.T("Onar", "Repair"), L.T("Şimdi değil", "Not now"));
            if (repair)
                await RunSingleFollowUpAsync(card, L.T("DISM onarımı", "DISM repair"));
            else
                _logger.Info(L.T("[DISM] Kullanıcı onarımı erteledi; bileşen deposuna dokunulmadı.", "[DISM] The user postponed the repair; the component store was not touched."));
        }

        await ShowResultsAsync(card.Title, L.T("Windows aracının verdiği gerçek sonuç:", "Real result from the Windows tool:"), [card.Key]);
    }

    /// <summary>Bakım kartında onaylanan ek işlemi (MRT temizliği, DISM onarımı) kartın kontrol sonucuyla çalıştırır.</summary>
    private async Task RunSingleFollowUpAsync(ComponentCardViewModel card, string scope)
    {
        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? res = null;
        await RunBusyAsync(updatePhase: true,
            async ct => res = await _orchestrator.RunSingleUpdateAsync(snapshot, card.Key, Reporters(), ct),
            done =>
            {
                StoreUpdates(res);
                var anyError = FinishOperation(scope, res, done, updatePhase: true, single: true, NotifyPolicy.Always);
                return new OperationEnd(anyError, L.T($"{scope} tamamlandı", $"{scope} completed"), L.T($"{scope} başarısız oldu", $"{scope} failed"));
            });
    }

    // ------------------------------------------------------------------ MANUEL (OTOMATİK UYGULANMAYAN) GÜNCELLEMELER

    /// <summary>Kontrol akışlarında bir kez sunulup kullanıcının geçtiği manuel güncellemeler ("anahtar|Id|sürüm").</summary>
    private readonly HashSet<string> _manualOffered = new(StringComparer.OrdinalIgnoreCase);

    private static bool HasManualUpdates(ModuleResult r) =>
        r.Items.Any(i => i.UpdateAvailable && i.Manual != ManualUpdateKind.None);

    /// <summary>
    /// Winget / Microsoft Store kontrolünde bulunan ve otomatik uygulanmayan güncellemeleri (açık hedefleme gerekli,
    /// kurulum teknolojisi farklı) seçilebilir şekilde sunar. Hiçbiri varsayılan olarak seçili değildir; yalnızca kullanıcının
    /// işaretledikleri uygulanır. Uygulandıysa true döner.
    /// </summary>
    /// <param name="onlyNotOffered">Kontrol akışlarında: bu oturumda zaten sunulmuş olanlar tekrar sorulmaz.</param>
    private async Task<bool> OfferManualUpdatesAsync(IEnumerable<ModuleResult> checks, bool onlyNotOffered)
    {
        string KeyOf(ModuleResult r, UpdateItem i) => $"{r.Key}|{i.Id}|{i.NewVersion}";
        var groups = checks
            .Where(r => _orchestrator.Find(r.Key) is IManualUpdateModule)
            .Select(r => (Result: r, Items: r.Items
                .Where(i => i.UpdateAvailable && i.Manual != ManualUpdateKind.None && (!onlyNotOffered || !_manualOffered.Contains(KeyOf(r, i))))
                .ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
        if (groups.Count == 0) return false;

        foreach (var (r, items) in groups)
            foreach (var i in items) _manualOffered.Add(KeyOf(r, i));

        var choices = groups.SelectMany(g => g.Items.Select(i => new ChoiceItemViewModel(
                $"{g.Result.Key}|{i.Id}",
                $"{i.Name}   {i.CurrentVersion} → {i.NewVersion}",
                0,
                i.Manual == ManualUpdateKind.TechnologyMismatch ? L.T("kaldır + yeni sürümü kur", "uninstall + install the new version") : L.T("açık hedeflemeyle güncelle", "update with explicit targeting"),
                isChecked: false)))
            .ToList();

        var ok = await Dialog.ShowAsync(
            L.T("Otomatik uygulanmayan güncellemeler", "Updates that are not applied automatically"),
            L.T("Winget bu güncellemeleri otomatik uygulamaz. Uygulamak istediklerinizi işaretleyin; hiçbiri varsayılan olarak seçili değildir.\n\n", "Winget does not apply these updates automatically. Select the ones you want to apply; none is selected by default.\n\n") +
            L.T("• Açık hedeflemeyle güncelle: uygulama genellikle kendini günceller (ör. Discord açıldığında). Seçerseniz yalnızca bu paket ", "• Update with explicit targeting: the app usually updates itself (e.g. when Discord opens). If you select it, only this package ") +
            L.T("hedeflenerek winget ile güncellenir.\n", "is targeted and updated with winget.\n") +
            L.T("• Kaldır + yeni sürümü kur: mevcut sürüm farklı bir kurulum türüyle (ör. MSI) kurulmuş; winget yerinde yükseltemez ", "• Uninstall + install the new version: the current version was installed with a different installer type (e.g. MSI); winget cannot upgrade it in place ") +
            L.T("(0x8A15008E). Seçerseniz mevcut sürüm KALDIRILIR ve yeni sürüm kurulur. Kaldırma başarısız olursa hiçbir şey değişmez; ", "(0x8A15008E). If you select it, the current version is UNINSTALLED and the new version is installed. If uninstalling fails, nothing changes; ") +
            L.T("kurulum başarısız olursa paket kurulu olmadan kalabilir ve bu açıkça bildirilir.", "if the installation fails, the package may be left uninstalled, and this is reported clearly."),
            Icons.Winget, DialogKind.Question, L.T("Seçilenleri uygula", "Apply selected"), L.T("Şimdi değil", "Not now"),
            choices: choices, choicesTitle: L.T("OTOMATİK UYGULANMAYAN GÜNCELLEMELER", "UPDATES NOT APPLIED AUTOMATICALLY"), choicesAreSizes: false);
        var selected = choices.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        if (!ok || selected.Count == 0)
        {
            _logger.Info(L.T("Manuel güncellemeler uygulanmadı (kullanıcı seçmedi); hiçbir pakete dokunulmadı.", "Manual updates were not applied (the user selected none); no package was touched."));
            return false;
        }
        _logger.Info(L.T("Manuel güncelleme için seçilenler: ", "Selected for manual update: ") + string.Join(", ", choices.Where(c => c.IsChecked).Select(c => c.Label.Replace("   ", " "))));

        var results = new Dictionary<string, ModuleResult>();
        await RunBusyAsync(updatePhase: true,
            async ct =>
            {
                foreach (var (result, _) in groups)
                {
                    var ids = selected.Where(s => s.StartsWith(result.Key + "|", StringComparison.Ordinal))
                        .Select(s => s[(result.Key.Length + 1)..]).ToList();
                    if (ids.Count == 0) continue;
                    var r = await _orchestrator.RunManualUpdatesAsync(result, ids, Reporters(), ct);
                    if (r is not null) results[r.Key] = r;
                }
            },
            done =>
            {
                StoreUpdates(results);
                var anyError = FinishOperation(L.T("Manuel güncellemeler", "Manual updates"), results, done, updatePhase: true,
                    single: results.Count == 1, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, L.T("Manuel güncellemeler tamamlandı", "Manual updates completed"), L.T("Manuel güncellemeler bitti – bazı paketler güncellenemedi", "Manual updates finished – some packages could not be updated"));
            });

        await OfferInUseRetryAsync(results);
        return true;
    }

    // ------------------------------------------------------------------ ÇALIŞAN UYGULAMAYI KAPAT VE YENİDEN DENE

    /// <summary>
    /// Güncellemesi çalışan bir uygulama nedeniyle başarısız olan paketler varsa, engelleyen GERÇEK işlemleri (Windows Restart
    /// Manager tespiti) listeler; kullanıcı onaylarsa bu uygulamaları kapatıp güncellemeyi yeniden dener.
    /// Onay verilmezse hiçbir işlem kapatılmaz. Windows hizmetleri ve sistem işlemleri hiçbir zaman kapatılmaz.
    /// </summary>
    private async Task OfferInUseRetryAsync(IReadOnlyDictionary<string, ModuleResult>? results)
    {
        if (results is null) return;
        var candidates = results.Values
            .Where(r => _orchestrator.Find(r.Key) is IInUseRetryModule)
            .Select(r => (Result: r, Items: r.Items
                .Where(i => i.Outcome is ItemOutcome.Failed or ItemOutcome.Unverified && i.InUse &&
                            i.Manual != ManualUpdateKind.TechnologyMismatch && i.BlockingProcesses.Any(p => p.CanClose))
                .ToList()))
            .Where(x => x.Items.Count > 0)
            .ToList();
        if (candidates.Count == 0) return;

        var bullets = new List<string>();
        foreach (var (_, items) in candidates)
        {
            foreach (var i in items)
            {
                var line = L.T($"{i.Name} ({i.CurrentVersion} → {i.NewVersion}) – kapatılacak: ", $"{i.Name} ({i.CurrentVersion} → {i.NewVersion}) – will be closed: ") +
                           string.Join(", ", i.BlockingProcesses.Where(p => p.CanClose).Select(p => $"{p.Name} (PID {p.ProcessId})"));
                var others = i.BlockingProcesses.Where(p => !p.CanClose).ToList();
                if (others.Count > 0) line += L.T(". Kapatılmayacak: ", ". Will not be closed: ") + string.Join(", ", others.Select(p => p.DisplayText));
                bullets.Add(line);
            }
        }

        var ok = await Dialog.ShowAsync(
            L.T("Çalışan uygulamalar güncellemeyi engelliyor", "Running apps are blocking the update"),
            L.T("Aşağıdaki paketler, çalışan uygulamalar dosyalarını kullandığı için güncellenemedi (Windows Restart Manager ile tespit edildi).\n\n", "The following packages could not be updated because running apps are using their files (detected with Windows Restart Manager).\n\n") +
            L.T("Onaylarsanız bu uygulamalar kapatılır – önce normal kapatma istenir, 10 saniye içinde kapanmazsa Görev Yöneticisi'ndeki ", "If you approve, these apps are closed – a normal close is requested first, and if they do not close within 10 seconds they are ended like ") +
            L.T("\"Görevi sonlandır\" gibi sonlandırılır – ve güncelleme yeniden denenir. Kaydedilmemiş çalışmalar kaybolabilir. ", "\"End task\" in Task Manager – and the update is retried. Unsaved work may be lost. ") +
            L.T("Windows hizmetleri ve sistem işlemleri kapatılmaz.", "Windows services and system processes are never closed."),
            Icons.Warning, DialogKind.Warning, L.T("Kapat ve tekrar dene", "Close and retry"), L.T("Şimdi değil", "Not now"), bullets);
        if (!ok)
        {
            _logger.Info(L.T("Kullanıcı engelleyen uygulamaların kapatılmasını onaylamadı; hiçbir uygulama kapatılmadı.", "The user did not approve closing the blocking apps; no app was closed."));
            return;
        }

        var retryResults = new Dictionary<string, ModuleResult>();
        await RunBusyAsync(updatePhase: true,
            async ct =>
            {
                foreach (var (result, items) in candidates)
                {
                    var approved = items.SelectMany(i => i.BlockingProcesses.Where(p => p.CanClose)).ToList();
                    var r = await _orchestrator.RetryInUseAsync(result, approved, Reporters(), ct);
                    if (r is not null) retryResults[r.Key] = r;
                }
            },
            done =>
            {
                StoreUpdates(retryResults);
                var anyError = FinishOperation(L.T("Yeniden deneme", "Retry"), retryResults, done, updatePhase: true,
                    single: retryResults.Count == 1, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, L.T("Yeniden deneme tamamlandı", "Retry completed"), L.T("Yeniden deneme bitti – bazı paketler yine güncellenemedi", "Retry finished – some packages still could not be updated"));
            });
    }

    // ------------------------------------------------------------------ SONUÇ / YENİDEN BAŞLATMA

    private async Task ShowResultsAsync(string title, string message, IReadOnlyCollection<string>? keys = null)
    {
        var cards = keys is null ? Cards.ToList() : Cards.Where(c => keys.Contains(c.Key)).ToList();
        var rows = cards.Select(c => new ResultRowViewModel
        {
            Title = c.Title,
            Glyph = c.Glyph,
            Status = c.Status,
            Summary = c.Summary,
            Reason = c.Reason
        }).ToList();

        var reboot = cards.Any(c => c.Status == ComponentStatus.RebootRequired || c.LastResult?.RebootRequired == true);
        if (reboot)
            message += L.T("\n\nBazı işlemlerin tamamlanması için yeniden başlatma gerekiyor.", "\n\nA restart is required to complete some operations.");

        var restartRequested = false;
        await Dialog.ShowAsync(title, message, Icons.Check, DialogKind.Result, L.T("Kapat", "Close"),
            results: rows,
            tertiary: reboot ? L.T("Yeniden başlat", "Restart") : null,
            tertiaryAction: reboot ? () => { restartRequested = true; Dialog.Close(false); } : null);

        if (restartRequested)
            await RequestRestartAsync();
    }

    private Task ShowNoSelectionAsync()
    {
        _logger.Warning(L.T("Seçili işlem yok: lütfen en az bir işlem seçin.", "No operation selected: please select at least one."));
        return Dialog.ShowAsync(L.T("Seçim yapılmadı", "Nothing selected"), L.T("Lütfen en az bir işlem seçin.", "Please select at least one operation."),
            Icons.Info, DialogKind.Info, L.T("Tamam", "OK"));
    }

    private async Task RequestRestartAsync()
    {
        var ok = await Dialog.ShowAsync(L.T("Yeniden başlatılsın mı?", "Restart now?"),
            L.T("Bilgisayarınız 60 saniye sonra yeniden başlatılacak. Lütfen açık belgelerinizi kaydedin.\n\n", "Your computer will restart in 60 seconds. Please save your open documents.\n\n") +
            L.T("Vazgeçerseniz yeniden başlatmayı daha sonra kendiniz yapabilirsiniz.", "If you cancel, you can restart later yourself."),
            Icons.Restart, DialogKind.Warning, L.T("60 sn sonra yeniden başlat", "Restart in 60 sec"), L.T("Vazgeç", "Cancel"));
        if (!ok)
        {
            _logger.Info(L.T("Yeniden başlatma kullanıcı tarafından ertelendi.", "The restart was postponed by the user."));
            return;
        }

        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        var r = await ProcessRunner.RunCmdAsync(shutdown,
            ["/r", "/t", "60", "/c", AppInfo.Name + L.T(": Guncellemeleri tamamlamak icin yeniden baslatiliyor. Iptal icin: shutdown /a", ": Restarting to complete updates. To cancel: shutdown /a")],
            TimeSpan.FromSeconds(30), CancellationToken.None);
        if (r.Succeeded)
            _logger.Warning(L.T("Bilgisayar 60 saniye içinde yeniden başlatılacak (iptal için komut satırında: shutdown /a).", "The computer will restart within 60 seconds (to cancel, run in a command prompt: shutdown /a)."));
        else
            _logger.Error(L.T("Yeniden başlatma planlanamadı: ", "The restart could not be scheduled: ") + ProcessRunner.Describe(r, "shutdown"));
    }

    private void Cancel()
    {
        if (_cts is null || _cancelRequested) return;
        _cancelRequested = true;
        _cts.Cancel();
        _logger.Warning(IsUpdatePhase
            ? L.T("İptal istendi: sürmekte olan kurulum güvenli şekilde tamamlanacak, kalan adımlar ve sıradaki paketler atlanacak.", "Cancellation requested: the running installation will finish safely; the remaining steps and queued packages will be skipped.")
            : L.T("İptal istendi: kontrol durduruluyor...", "Cancellation requested: stopping the check..."));
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Geçici Dosyalar kartı işleme dahilse, kontrolde bulunan GERÇEK kategorileri seçilebilir öğe olarak döndürür.</summary>
    private List<ChoiceItemViewModel>? BuildTempChoices(IEnumerable<string> keys)
    {
        if (!keys.Contains(ComponentKeys.TempFiles) ||
            !_checks.TryGetValue(ComponentKeys.TempFiles, out var check) || !check.HasActionableUpdates)
            return null;
        return check.Items
            .Where(i => i.UpdateAvailable)
            .Select(i => new ChoiceItemViewModel(i.Id, i.Name, long.TryParse(i.Tag, out var b) ? b : 0, i.CurrentVersion))
            .ToList();
    }

    /// <summary>Onay penceresindeki kategori seçimlerini kontrol sonucundaki öğelere uygular.</summary>
    private void ApplyTempChoices(List<ChoiceItemViewModel>? choices)
    {
        if (choices is null || !_checks.TryGetValue(ComponentKeys.TempFiles, out var check)) return;
        foreach (var item in check.Items)
        {
            var choice = choices.FirstOrDefault(c => c.Id == item.Id);
            if (choice is not null) item.Selected = choice.IsChecked;
        }
        var chosen = choices.Where(c => c.IsChecked).Select(c => c.Label).ToList();
        _logger.Info(chosen.Count == 0
            ? L.T("Geçici dosyalar: hiçbir kategori seçilmedi; temizlik yapılmayacak.", "Temporary files: no category selected; no cleanup will be done.")
            : L.T("Geçici dosyalar: temizlenecek kategoriler – ", "Temporary files: categories to clean – ") + string.Join(", ", chosen));
    }

    private string BuildBullet(string key, ModuleResult c)
    {
        switch (key)
        {
            case ComponentKeys.Winget:
                var edgeApps = c.Items.Where(i => i.UpdateAvailable && i.AutoUpdatable && EdgeUpdateService.Find(i.Id) is not null)
                    .Select(i => i.Name).ToList();
                return L.T($"Winget: {c.ActionableCount} uygulama güncellenecek.", $"Winget: {c.ActionableCount} app(s) will be updated.") + (edgeApps.Count == 0 ? ""
                    : L.T($" {string.Join(", ", edgeApps)} Microsoft'un kendi güncelleyicisiyle (Microsoft Edge Update) güncellenir; kaldırılmaz.", $" {string.Join(", ", edgeApps)} is updated with Microsoft's own updater (Microsoft Edge Update); it is not uninstalled."));
            case ComponentKeys.WindowsUpdate:
                return L.T($"Windows Update: {c.ActionableCount} güncelleştirme indirilip kurulacak.", $"Windows Update: {c.ActionableCount} update(s) will be downloaded and installed.");
            case ComponentKeys.Store:
                return L.T($"Microsoft Store: {c.ActionableCount} uygulama güncellenecek.", $"Microsoft Store: {c.ActionableCount} app(s) will be updated.");
            case ComponentKeys.Nvidia:
                var n = c.Items.FirstOrDefault(i => i.UpdateAvailable);
                return L.T($"NVIDIA: sürücü {n?.CurrentVersion} → {n?.NewVersion} resmi NVIDIA sunucusundan indirilip kurulacak. Kurulum sırasında ekran birkaç kez kararabilir.", $"NVIDIA: driver {n?.CurrentVersion} → {n?.NewVersion} will be downloaded from the official NVIDIA server and installed. The screen may go dark a few times during installation.");
            case ComponentKeys.Defender:
                return L.T("Microsoft Defender: virüs ve tehdit tanımları güncellenecek.", "Microsoft Defender: virus and threat definitions will be updated.");
            case ComponentKeys.Sfc:
                return L.T("Windows Sistem Dosyası Kontrolü: doğrulamada bozuk dosya bulundu; sfc /scannow ile onarım yapılacak (10-30 dk).", "Windows System File Check: verification found corrupt files; they will be repaired with sfc /scannow (10-30 min).");
            case ComponentKeys.Dism:
                return L.T("Windows Image: bileşen deposu ", "Windows Image: the component store will be repaired with ") + DismManager.RepairCommand + L.T(" ile onarılacak (10-60 dk; onarım dosyaları ", " (10-60 min; repair files may be ") +
                       L.T("Windows Update'ten indirilebilir, başladıktan sonra yarıda kesilmez). Ardından durum yeniden kontrol edilir.", "downloaded from Windows Update; it is not interrupted once started). Then the status is checked again.");
            case ComponentKeys.Mrt:
                return L.T("MRT: tespit edilen kötü amaçlı yazılım, MRT hızlı taramasıyla (temizleme modu) kaldırılacak.", "MRT: the detected malware will be removed with an MRT quick scan (removal mode).");
            case ComponentKeys.RecycleBin:
                return L.T($"Çöp Kutusu: {c.ActionableCount} öğe KALICI olarak silinecek.", $"Recycle Bin: {c.ActionableCount} item(s) will be deleted PERMANENTLY.");
            case ComponentKeys.TempFiles:
                return L.T($"Windows Geçici Dosyalar: seçili kategoriler temizlenecek ({c.Summary}). Silinen geçici dosyalar geri alınamaz; ", $"Windows Temporary Files: the selected categories will be cleaned ({c.Summary}). Deleted temporary files cannot be restored; ") +
                       L.T("temizlikten sonra alan yeniden ölçülür.", "the space is measured again after cleaning.");
            default:
                return $"{_orchestrator.NameOf(key)}: {c.Summary}";
        }
    }

    /// <summary>İşlem sonundaki tek durum geçişinin girdisi: gerçek hata durumu ve duruma göre gösterilecek adım metinleri.</summary>
    private readonly record struct OperationEnd(bool AnyError, string CompletedText, string? FailedText = null, string? CancelledText = null);

    /// <summary>
    /// İşlemi "meşgul" durumunda çalıştırır. İşlem nasıl biterse bitsin (tamamlandı / iptal / hata) TEK bir final durum
    /// geçişi yapılır (<see cref="CompleteOperation"/>). İptal edilmeden tamamlandıysa true döner.
    /// </summary>
    /// <param name="finalize">
    /// Final geçişte, işlem durumu hesaplanmadan ÖNCE çalışır: gerçek sonuçları kaydeder, işlem özetini oluşturur ve hata
    /// durumunu + adım metinlerini döndürür. Parametre: işlem iptal edilmeden tamamlandı mı.
    /// </param>
    private async Task<bool> RunBusyAsync(bool updatePhase, Func<CancellationToken, Task> body, Func<bool, OperationEnd> finalize)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        _cancelRequested = false;
        IsUpdatePhase = updatePhase;
        OperationState = updatePhase ? OperationState.Updating : OperationState.Checking;
        Progress = 0;
        SetBusy(true);
        var watch = Stopwatch.StartNew();
        var completed = false;
        Exception? error = null;
        try
        {
            await body(cts.Token);
            completed = !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            completed = false; // kullanıcı iptali hata değildir: durum "İptal edildi" olur
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _cts = null;
        cts.Dispose();
        error = CompleteOperation(completed, error, watch.Elapsed, finalize);
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return completed;
    }

    /// <summary>
    /// İşlemin TEK final durum geçişi: sonuçlar kaydedilir, işlem durumu (Tamamlandı / Hata / İptal) GERÇEK sonuçlardan
    /// belirlenir, "çalışıyor" durumu kapanır (ilerleme çubuğu parıltısı ve dönen ikon bu anda durur, tik/hata/iptal ikonu
    /// görünür), gerçek tamamlanmada ilerleme %100 olur, adım metni, "Bulunan Güncellemeler" tablosu ve buton durumları
    /// bir kez yeniden hesaplanır. Tümü aynı arayüz işleminde yapıldığından ara durumlar çizilmez.
    /// </summary>
    /// <returns>İşlemi sonlandıran hata (varsa); çağıran yeniden fırlatır.</returns>
    private Exception? CompleteOperation(bool completed, Exception? error, TimeSpan elapsed, Func<bool, OperationEnd> finalize)
    {
        _lastBusyDuration = elapsed;
        var end = new OperationEnd(false, L.T("İşlem tamamlandı", "Operation completed"));
        if (error is null)
        {
            try
            {
                end = finalize(completed);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
        // Winget'in bu işlemde bildirdiği kurulum teknolojisi uyuşmazlıkları kalıcı olarak saklanır (yalnızca değiştiyse yazılır).
        _state.SetWingetTechnologyMismatch(WingetManager.KnownTechnologyMismatches);
        _state.SetWingetNoVersionChange(WingetManager.KnownNoVersionChange);

        var state = error is not null ? OperationState.Failed
            : !completed ? OperationState.Cancelled
            : end.AnyError ? OperationState.Failed
            : OperationState.Completed;

        if (completed && error is null) Progress = 100;
        OperationState = state;
        StepText = state switch
        {
            OperationState.Cancelled => end.CancelledText ?? L.T("İşlem iptal edildi", "Operation cancelled"),
            OperationState.Failed when error is not null => L.T("İşlem beklenmeyen bir hatayla durdu (ayrıntılar günlükte)", "The operation stopped with an unexpected error (details in the log)"),
            OperationState.Failed => end.FailedText ?? end.CompletedText,
            _ => end.CompletedText
        };
        IsUpdatePhase = false;
        SetBusy(false);
        RebuildUpdateRows();
        RaiseSummaryChanged();
        return error;
    }

    /// <summary>"Çalışıyor" durumunu değiştirir; buton durumları çağıranın tek <see cref="RaiseSummaryChanged"/> çağrısıyla güncellenir.</summary>
    private void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;
        _isBusy = busy;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsOperationRunning));
        if (busy) RaiseUpdateAvailabilityChanged();
        OnPropertyChanged(nameof(ShowUpdateOverlay));
        OnPropertyChanged(nameof(ShowOfflineOverlay));
        SpeedTest.OnSystemBusyChanged();
    }

    private void StoreChecks(Dictionary<string, ModuleResult>? results)
    {
        if (results is null) return;
        foreach (var (key, r) in results)
        {
            if (r.Status is ComponentStatus.Skipped or ComponentStatus.Checking) continue;
            _checks[key] = r;
        }
    }

    private void StoreUpdates(Dictionary<string, ModuleResult>? results)
    {
        if (results is null) return;
        foreach (var (key, r) in results)
        {
            if (r.Status == ComponentStatus.Skipped) continue; // dokunulmadı; önceki kontrol geçerli
            _checks.Remove(key); // işlem yapıldı; yeni durum için yeniden kontrol gerekir
            _updatesApplied = true;
        }
    }

    /// <summary>
    /// Orkestratör bildirimleri. Adım metni/yüzdesi ve kartların canlı ilerlemesi <see cref="ThrottledProgress{T}"/> ile
    /// en fazla 100 ms'de bir (yalnızca en son değer) uygulanır; kart sonuçları ise sırasıyla ve anında uygulanır.
    /// İşlem bittikten sonra gelen gecikmiş ara değerler final durumu değiştirmez.
    /// </summary>
    private OrchestratorReporters Reporters()
    {
        var activity = new ThrottledProgress<ModuleProgress>(_dispatcher, ProgressInterval, p => p.Key, p =>
        {
            if (IsBusy) Cards.FirstOrDefault(c => c.Key == p.Key)?.ApplyProgress(p);
        });
        var step = new ThrottledProgress<StepProgress>(_dispatcher, ProgressInterval, _ => "step", p =>
        {
            if (!IsBusy) return;
            StepText = p.Text;
            Progress = p.Percent * _progressScale;
        });
        var state = new Progress<ModuleResult>(r =>
        {
            activity.Discard(r.Key);
            // Tamamlanan her gerçek sonuç kartta gösterilir ve işlem geçmişine kaydedilir.
            var snapshot = CardSnapshot.From(r);
            Cards.FirstOrDefault(c => c.Key == r.Key)?.Apply(r, snapshot);
            if (snapshot is not null) _state.SetCard(snapshot);
        });
        return new OrchestratorReporters(step, state, activity);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (var c in Cards) c.SyncSelected(_selection.IsSelected(c.Key));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(UpdateSelectedText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RebuildUpdateRows()
    {
        var rows = new List<UpdateRowViewModel>();
        foreach (var card in Cards)
        {
            var r = card.LastResult;
            if (r is null) continue;
            foreach (var i in r.Items.OrderByDescending(i => i.UpdateAvailable))
            {
                rows.Add(new UpdateRowViewModel
                {
                    Category = card.Title,
                    Name = i.Name,
                    CurrentVersion = i.CurrentVersion,
                    NewVersion = i.NewVersion,
                    Status = i.StatusText,
                    UpdateAvailable = i.UpdateAvailable
                });
            }
        }
        UpdateRows = new ObservableCollection<UpdateRowViewModel>(rows);
    }
    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(AvailableSummary));
        RaiseUpdateAvailabilityChanged();
    }

    /// <summary>Güncelleme butonlarının etkinliğini gerçek kontrol sonuçlarına göre yeniden hesaplatır.</summary>
    private void RaiseUpdateAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanUpdateAll));
        OnPropertyChanged(nameof(CanRunSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Herhangi bir iş parçacığından gelen günlük satırı kuyruğa alınır; arayüz 150 ms'de bir TEK seferde günceller.
    /// (Dosyaya yazma Logger'da ayrıca ve eksiksiz yapılır; bu yalnızca ekrandaki görünümdür.)
    /// </summary>
    private void OnLogAdded(LogEntry entry)
    {
        _pendingLogs.Enqueue(entry);
        if (Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) != 0) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Interlocked.Exchange(ref _logFlushScheduled, 0);
            return;
        }
        dispatcher.BeginInvoke(_logTimer.Start, DispatcherPriority.Background);
    }

    private void FlushPendingLogs(object? sender, EventArgs e)
    {
        var added = 0;
        while (added < 2000 && _pendingLogs.TryDequeue(out var entry))
        {
            Logs.Add(entry);
            added++;
        }
        var excess = Logs.Count - MaxLogEntries;
        for (var i = 0; i < excess; i++) Logs.RemoveAt(0);
        if (added > 0) LogsAppended?.Invoke(this, EventArgs.Empty);

        if (_pendingLogs.IsEmpty)
        {
            _logTimer.Stop();
            Interlocked.Exchange(ref _logFlushScheduled, 0);
            // Durdurma ile kuyruk kontrolü arasında gelen satır kaçmasın.
            if (!_pendingLogs.IsEmpty && Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) == 0)
                _logTimer.Start();
        }
    }

    // ------------------------------------------------------------------ günlük yönetimi

    private bool PassesLogFilter(LogEntry e) => _logFilter switch
    {
        "info" => e.Level is LogLevel.Info or LogLevel.Output,
        "success" => e.Level == LogLevel.Success,
        "warning" => e.Level == LogLevel.Warning,
        "error" => e.Level == LogLevel.Error,
        _ => true
    };

    /// <summary>Oturum günlük dosyasını varsayılan metin düzenleyicide açar.</summary>
    private async Task OpenLogFileAsync()
    {
        try
        {
            await Task.Run(() => _logger.Flush(TimeSpan.FromSeconds(2)));
            if (!File.Exists(_logger.LogFilePath))
                _logger.Warning(L.T("Log dosyası henüz oluşturulmadı.", "The log file has not been created yet."));
            // Gezgin üzerinden: metin düzenleyici yönetici yetkisi olmadan açılır (".log" ilişkilendirmesi kullanıcı kayıt defterindedir).
            else if (ShellOpen.OpenFile(_logger.LogFilePath) is { } error)
                _logger.Error(L.T("Log dosyası açılamadı: ", "Could not open the log file: ") + error);
        }
        catch (Exception ex)
        {
            _logger.Error(L.T("Log dosyası açılamadı: ", "Could not open the log file: ") + ex.Message);
        }
    }

    private async Task OpenLogFolderAsync()
    {
        try
        {
            await Task.Run(() => _logger.Flush(TimeSpan.FromSeconds(2)));
            // Gezgin Windows klasöründeki tam yoluyla başlatılır (adıyla aranırsa yönetici olarak çalışırken çalışma klasöründeki /
            // PATH'teki aynı adlı bir program öne geçebilirdi).
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (File.Exists(_logger.LogFilePath))
                Process.Start(new ProcessStartInfo(explorer, $"/select,\"{_logger.LogFilePath}\"") { UseShellExecute = false })?.Dispose();
            else if (Directory.Exists(_logger.LogDirectory))
                Process.Start(new ProcessStartInfo(explorer, $"\"{_logger.LogDirectory}\"") { UseShellExecute = false })?.Dispose();
            else
                _logger.Warning(L.T("Log klasörü henüz oluşturulmadı.", "The log folder has not been created yet."));
        }
        catch (Exception ex)
        {
            _logger.Error(L.T("Log klasörü açılamadı: ", "Could not open the log folder: ") + ex.Message);
        }
    }

    /// <summary>Gerçek oturum günlük dosyasını kullanıcının seçtiği konuma kopyalar.</summary>
    private async Task ExportLogsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Günlüğü dışa aktar", "Export log"),
            FileName = $"E-mre-Control-Center-Log-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            Filter = L.T("Metin dosyası (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*", "Text file (*.txt)|*.txt|All files (*.*)|*.*"),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;

        try
        {
            _logger.Info(L.T($"Günlük dışa aktarılıyor: {dialog.FileName}", $"Exporting log: {dialog.FileName}"));
            var target = dialog.FileName;
            var lines = await Task.Run(() =>
            {
                _logger.ExportTo(target);
                return File.ReadLines(target).Count();
            });
            _logger.Success(L.T($"Günlük dışa aktarıldı: {dialog.FileName} ({lines} satır).", $"Log exported: {dialog.FileName} ({lines} lines)."));
        }
        catch (Exception ex)
        {
            _logger.Error(L.T($"Günlük dışa aktarılamadı: {ex.Message}", $"Could not export the log: {ex.Message}"));
            _ = Dialog.ShowAsync(L.T("Dışa aktarılamadı", "Export failed"), L.T("Günlük dosyası kopyalanamadı:\n", "Could not copy the log file:\n") + ex.Message,
                Icons.Warning, DialogKind.Warning, L.T("Tamam", "OK"));
        }
    }

    /// <summary>Yalnızca ekrandaki günlüğü temizler; diskteki günlük dosyası korunur.</summary>
    private void ClearLogs()
    {
        Logs.Clear();
        _logger.Info(L.T($"Ekrandaki günlük temizlendi. Günlük dosyası korunuyor: {_logger.LogFilePath}", $"The on-screen log was cleared. The log file is kept: {_logger.LogFilePath}"));
    }

    // ------------------------------------------------------------------ sistem bilgileri

    private async Task RefreshSystemInfoAsync()
    {
        if (IsSystemInfoLoading) return;
        IsSystemInfoLoading = true;
        SystemInfoStatus = L.T("Sistem bilgileri okunuyor...", "Reading system information...");
        try
        {
            var snapshot = await _systemInfo.CollectAsync();
            SystemInfoFields.Clear();
            SystemInfoPrimaryFields.Clear();
            foreach (var f in snapshot.Fields)
            {
                SystemInfoFields.Add(f);
                if (f.Primary) SystemInfoPrimaryFields.Add(f);
            }
            var missing = snapshot.Fields.Count(f => !f.Available);
            SystemInfoStatus = L.T($"Okunma: {snapshot.CollectedAt:HH:mm}", $"Read at: {snapshot.CollectedAt:HH:mm}") + (missing > 0 ? L.T($" · {missing} alan alınamadı", $" · {missing} field(s) unavailable") : string.Empty);
            RebuildDeviceSections();
        }
        catch (Exception ex)
        {
            SystemInfoStatus = L.T("Sistem bilgileri alınamadı", "System information unavailable");
            _logger.Error(L.T("Sistem bilgileri okunamadı: ", "Could not read system information: ") + ex.Message);
        }
        finally
        {
            IsSystemInfoLoading = false;
        }
    }

    // ------------------------------------------------------------------ sistem sağlık özeti

    /// <summary>
    /// Genel sağlık durumunu kartların GERÇEK durumlarından hesaplar. Çalıştırılmamış kartlar sağlıklı sayılmaz,
    /// hatalar ve bekleyen güncellemeler gizlenmez.
    /// </summary>
    private void RecomputeHealth()
    {
        int ok = 0, pending = 0, errors = 0, notRun = 0, running = 0, unavailable = 0;
        foreach (var c in Cards)
        {
            switch (c.Status)
            {
                case ComponentStatus.UpToDate or ComponentStatus.Updated: ok++; break;
                case ComponentStatus.UpdateAvailable or ComponentStatus.Attention or ComponentStatus.PartiallyUpdated
                    or ComponentStatus.RebootRequired: pending++; break;
                case ComponentStatus.Failed or ComponentStatus.CheckFailed or ComponentStatus.AdminRequired: errors++; break;
                case ComponentStatus.Checking or ComponentStatus.Updating: running++; break;
                case ComponentStatus.Unavailable: unavailable++; break; // bu sistemde çalıştırılamaz; sağlık hesabına girmez
                default: notRun++; break;
            }
        }

        var total = Cards.Count - unavailable;
        if (running > 0)
        {
            HealthStatus = ComponentStatus.Checking;
            HealthHeadline = L.T($"İşlem sürüyor ({running} kart çalışıyor)", $"Operation in progress ({running} card(s) running)");
        }
        else if (total == 0)
        {
            HealthStatus = ComponentStatus.Unavailable;
            // Tüm kartlar yalnızca ApplyRequirements'ta (IsAdmin ayarlandıktan sonra) kullanım dışı yapılabilir.
            HealthHeadline = !IsAdmin
                ? L.T("Yönetici yetkisi yok – tüm işlemler kullanım dışı", "No administrator rights – all operations unavailable")
                : L.T("Tüm işlemler kullanım dışı", "All operations unavailable");
        }
        else if (notRun == total)
        {
            HealthStatus = ComponentStatus.NotChecked;
            HealthHeadline = L.T("Sistem bu oturumda henüz kontrol edilmedi", "The system has not been checked in this session yet");
        }
        else if (errors > 0)
        {
            HealthStatus = ComponentStatus.Failed;
            HealthHeadline = errors == 1 ? L.T("1 işlemde hata var", "1 operation has an error") : L.T($"{errors} işlemde hata var", $"{errors} operations have errors");
        }
        else if (pending > 0)
        {
            HealthStatus = ComponentStatus.UpdateAvailable;
            HealthHeadline = L.T($"{pending} işlem dikkat gerektiriyor", $"{pending} operation(s) need attention");
        }
        else if (notRun > 0)
        {
            HealthStatus = ComponentStatus.NotChecked;
            HealthHeadline = L.T($"Kontrol edilen {ok} işlem sorunsuz · {notRun} işlem çalıştırılmadı", $"{ok} checked operation(s) OK · {notRun} operation(s) not run");
        }
        else
        {
            HealthStatus = ComponentStatus.UpToDate;
            HealthHeadline = L.T("Sistem kontrol edildi – sorun bulunmadı", "System checked – no problems found");
        }

        var parts = new List<string> { L.T($"{ok} sorunsuz", $"{ok} OK"), L.T($"{pending} güncelleme / uyarı", $"{pending} update(s) / warning(s)"), L.T($"{errors} hata", $"{errors} error(s)"), L.T($"{notRun} çalıştırılmadı", $"{notRun} not run") };
        if (running > 0) parts.Add(L.T($"{running} çalışıyor", $"{running} running"));
        if (unavailable > 0) parts.Add(L.T($"{unavailable} kullanım dışı", $"{unavailable} unavailable"));
        HealthCounts = string.Join(" · ", parts);
        RefreshCategories();
    }

    // ------------------------------------------------------------------ son işlem özeti + bildirim

    private enum NotifyPolicy { Never, Always, IfLong }

    private static bool IsErrorResult(ModuleResult r) =>
        r.Status is ComponentStatus.Failed or ComponentStatus.CheckFailed or ComponentStatus.AdminRequired;

    private static bool IsWarningResult(ModuleResult r) =>
        r.Status is ComponentStatus.Attention or ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired ||
        (r.Status == ComponentStatus.UpdateAvailable && r.Key is ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt);

    /// <summary>Kontrolde bulunan "güncelleme" (bakım bulguları, çöp kutusu ve geçici dosyalar hariç).</summary>
    private static bool IsUpdateFinding(ModuleResult r) =>
        r.Status == ComponentStatus.UpdateAvailable &&
        r.Key is not (ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt or ComponentKeys.RecycleBin or ComponentKeys.TempFiles);

    private string ShortName(string key) => Cards.FirstOrDefault(c => c.Key == key)?.ShortTitle ?? _orchestrator.NameOf(key);

    /// <summary>
    /// Tamamlanan işlemin özetini GERÇEK sonuçlardan hesaplar, "Son İşlem" alanını ve işlem geçmişini günceller,
    /// gerekiyorsa Windows bildirimi gönderir. Hata varsa true döner.
    /// </summary>
    private bool FinishOperation(string scope, IReadOnlyDictionary<string, ModuleResult>? results, bool completed,
        bool updatePhase, bool single, NotifyPolicy notify, string? extraSummary = null)
    {
        var all = results?.Values.Where(r => r.Status is not (ComponentStatus.Checking or ComponentStatus.Updating)).ToList() ?? [];
        var anyError = all.Any(IsErrorResult);
        if (all.Count == 0 && completed) return false; // hiçbir modül çalışmadı (ör. işlem gerekmedi)

        var duration = _lastBusyDuration;
        var rec = new OperationRecord
        {
            CompletedAt = DateTime.Now,
            Title = !completed ? L.T($"{scope} iptal edildi.", $"{scope} cancelled.") : anyError ? L.T($"{scope} tamamlandı – hata var.", $"{scope} completed – with errors.") : L.T($"{scope} tamamlandı.", $"{scope} completed."),
            Keys = all.Select(r => r.Key).ToList(),
            DurationMs = (long)duration.TotalMilliseconds,
            Cancelled = !completed,
            Completed = all.Count(r => r.CompletedAt is not null && r.Status != ComponentStatus.Skipped),
            Skipped = all.Count(r => r.Status == ComponentStatus.Skipped),
            Errors = all.Count(IsErrorResult),
            Warnings = all.Count(IsWarningResult),
            Updates = updatePhase
                ? all.Count(r => r.Status == ComponentStatus.Updated)
                : all.Where(IsUpdateFinding).Sum(r => Math.Max(1, r.ActionableCount))
        };

        var durationText = UpdateOrchestrator.FormatDuration(duration);
        if (single && all.Count == 1)
        {
            rec.SummaryText = L.T($"{all[0].Summary} · Süre: {durationText}", $"{all[0].Summary} · Duration: {durationText}");
        }
        else if (updatePhase)
        {
            // Ör: "4 işlem · 2 başarılı · 1 uyarı · 1 hata · Winget: 2 paket güncellenemedi · Süre: 21 sn"
            var processed = all.Count(r => r.Status != ComponentStatus.Skipped);
            var parts = new List<string> { L.T($"{processed} işlem", $"{processed} operation(s)"), L.T($"{rec.Updates} başarılı", $"{rec.Updates} succeeded"), L.T($"{rec.Warnings} uyarı", $"{rec.Warnings} warning(s)"), L.T($"{rec.Errors} hata", $"{rec.Errors} error(s)") };
            if (rec.Skipped > 0) parts.Add(L.T($"{rec.Skipped} atlandı", $"{rec.Skipped} skipped"));
            parts.AddRange(all
                .Where(r => IsErrorResult(r) || r.Status is ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired)
                .Select(r => $"{ShortName(r.Key)}: {r.Summary}"));
            parts.Add(L.T($"Süre: {durationText}", $"Duration: {durationText}"));
            rec.SummaryText = string.Join(" · ", parts);
        }
        else
        {
            // Ör: "10 kontrol tamamlandı · 3 güncelleme bulundu (Winget 2, Microsoft Defender 1) · 1 manuel güncelleme · ..."
            var found = all.Where(IsUpdateFinding).Select(r => $"{ShortName(r.Key)} {Math.Max(1, r.ActionableCount)}").ToList();
            var manual = all.Where(r => r.Key is ComponentKeys.Winget or ComponentKeys.Store)
                .Sum(r => r.Items.Count(i => i.UpdateAvailable && !i.AutoUpdatable));
            var parts = new List<string>
            {
                L.T($"{rec.Completed} kontrol tamamlandı", $"{rec.Completed} check(s) completed"),
                found.Count > 0 ? L.T($"{rec.Updates} güncelleme bulundu ({string.Join(", ", found)})", $"{rec.Updates} update(s) found ({string.Join(", ", found)})") : L.T("güncelleme bulunamadı", "no updates found")
            };
            if (manual > 0) parts.Add(L.T($"{manual} manuel güncelleme (otomatik uygulanmaz)", $"{manual} manual update(s) (not applied automatically)"));
            parts.Add(L.T($"{rec.Warnings} uyarı", $"{rec.Warnings} warning(s)"));
            parts.Add(L.T($"{rec.Errors} hata", $"{rec.Errors} error(s)"));
            if (rec.Skipped > 0) parts.Add(L.T($"{rec.Skipped} atlandı", $"{rec.Skipped} skipped"));
            parts.AddRange(all
                .Where(r => r.Status == ComponentStatus.UpdateAvailable && r.Key is ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt)
                .Select(r => $"{ShortName(r.Key)}: {r.Summary}"));
            var recycle = all.FirstOrDefault(r => r.Key == ComponentKeys.RecycleBin && r.Status == ComponentStatus.UpdateAvailable);
            if (recycle is not null) parts.Add(L.T($"çöp kutusunda {recycle.ActionableCount} öğe", $"{recycle.ActionableCount} item(s) in the Recycle Bin"));
            var temp = all.FirstOrDefault(r => r.Key == ComponentKeys.TempFiles && r.Status == ComponentStatus.UpdateAvailable);
            if (temp is not null) parts.Add(L.T($"Geçici dosyalar: {temp.Summary}", $"Temporary files: {temp.Summary}"));
            if (extraSummary is not null) parts.Add(extraSummary);
            parts.Add(L.T($"Süre: {durationText}", $"Duration: {durationText}"));
            rec.SummaryText = string.Join(" · ", parts);
        }

        _state.AddOperation(rec);
        RecentOperations.Insert(0, rec);
        while (RecentOperations.Count > 50) RecentOperations.RemoveAt(RecentOperations.Count - 1);
        LastOperation = rec;
        if (anyError) _logger.Warning(L.T($"Son işlem: {rec.Title} {rec.SummaryText}", $"Last operation: {rec.Title} {rec.SummaryText}"));
        else _logger.Info(L.T($"Son işlem: {rec.Title} {rec.SummaryText}", $"Last operation: {rec.Title} {rec.SummaryText}"));

        var shouldNotify = completed && notify switch
        {
            NotifyPolicy.Always => true,
            NotifyPolicy.IfLong => duration >= TimeSpan.FromSeconds(30),
            _ => false
        };
        if (shouldNotify) _ = _notifications.ShowAsync(rec.Title, rec.SummaryText);
        return anyError;
    }

    private void OnCommandError(Exception ex)
    {
        _logger.Error(L.T("Beklenmeyen hata: ", "Unexpected error: ") + ex);
        if (!IsBusy) return;
        // RunBusyAsync dışında kalan beklenmeyen bir durumda da "çalışıyor" hâli ve animasyonlar kapatılır.
        OperationState = OperationState.Failed;
        StepText = L.T("İşlem beklenmeyen bir hatayla durdu (ayrıntılar günlükte)", "The operation stopped with an unexpected error (details in the log)");
        SetBusy(false);
        RaiseSummaryChanged();
    }

    public void Dispose()
    {
        StopInternetMonitoring();
        Update.StopPeriodicChecks();
        StopDeviceMonitoring();
        _activeTool?.Deactivate();
        _monitorInstance?.Dispose();
        SpeedTest.Dispose();
        _logger.LogAdded -= OnLogAdded;
        _selection.SelectionChanged -= OnSelectionChanged;
        _logTimer.Stop();
        _logTimer.Tick -= FlushPendingLogs;
        _cts?.Cancel();
        _cts?.Dispose();
        _orchestrator.Dispose();
    }
}

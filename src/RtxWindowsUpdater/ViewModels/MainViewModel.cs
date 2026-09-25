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
    private string _stepText = "Henüz işlem yapılmadı";
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
    private string _systemInfoStatus = "Sistem bilgileri okunuyor...";
    private string _healthHeadline = "Sistem henüz kontrol edilmedi";
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
        AdminRow = new RequirementRowViewModel("Yönetici yetkisi", Icons.Admin);
        Requirements = [WindowsRow, GpuRow, AdminRow];

        // Tüm kartlar tek "Sistem İşlemleri" kategorisinde, aynı kart yapısıyla gösterilir.
        Cards =
        [
            new ComponentCardViewModel(ComponentKeys.WindowsUpdate, "Windows Update", Icons.WindowsUpdate)
            {
                CommandText = "Windows Update Agent API",
                Description = "Windows için kullanılabilir sistem güncellemelerini kontrol eder.",
                InfoText = "Windows Update üzerinden kullanılabilir Windows güncellemelerini kontrol eder. Güncelleme bulunduğunda kullanıcı onayıyla yüklenebilir."
            },
            new ComponentCardViewModel(ComponentKeys.Winget, "Winget", Icons.Winget)
            {
                CommandText = "winget upgrade",
                Description = "Yüklü uygulamalar için kullanılabilir yazılım güncellemelerini kontrol eder.",
                InfoText = "Windows Package Manager (winget) üzerinden sistemde kurulu desteklenen uygulamalar için kullanılabilir güncellemeleri kontrol eder."
            },
            new ComponentCardViewModel(ComponentKeys.Store, "Microsoft Store", Icons.Store)
            {
                CommandText = "winget --source msstore",
                Description = "Microsoft Store uygulamalarındaki kullanılabilir güncellemeleri kontrol eder.",
                InfoText = "Microsoft Store üzerinden yüklenen uygulamalar için kullanılabilir güncellemeleri kontrol eder ve desteklenen güncellemeleri kullanıcı onayıyla başlatabilir."
            },
            new ComponentCardViewModel(ComponentKeys.Nvidia, "NVIDIA Driver", Icons.Nvidia)
            {
                CommandText = "NVIDIA sürücü servisi",
                Description = "NVIDIA RTX ekran kartı sürücüsünün güncel olup olmadığını kontrol eder.",
                InfoText = "Sistemdeki NVIDIA RTX ekran kartını ve mevcut sürücüsünü kontrol eder. Kullanılabilir sürücü güncellemesi varsa kullanıcıya bildirir."
            },
            new ComponentCardViewModel(ComponentKeys.Defender, "Microsoft Defender", Icons.Defender)
            {
                CommandText = "Update-MpSignature",
                Description = "Defender koruma durumunu ve güvenlik tanımlarının güncelliğini kontrol eder.",
                InfoText = "Microsoft Defender'ın koruma durumunu ve güvenlik tanımlarının güncelliğini kontrol eder. Gerekli olduğunda Defender tanımları güncellenebilir."
            },
            new ComponentCardViewModel(ComponentKeys.RecycleBin, "Çöp Kutusu", Icons.RecycleBin)
            {
                CommandText = "Shell Recycle Bin API",
                Description = "Windows Çöp Kutusu'ndaki öğeleri kontrol eder ve kullanıcı onayıyla temizler.",
                InfoText = "Windows Çöp Kutusu'ndaki öğeleri kontrol eder. Kullanıcı onayıyla Çöp Kutusu temizlenebilir."
            },
            new ComponentCardViewModel(ComponentKeys.Sfc, "Windows Sistem Dosyası Kontrolü", Icons.Sfc)
            {
                IsMaintenance = true,
                ShortTitle = "SFC",
                CommandText = "SFC /SCANNOW",
                Description = "Windows sistem dosyalarını tarar ve bozuk veya eksik dosyaları onarmayı dener.",
                InfoText = "Windows sistem dosyalarının bütünlüğünü kontrol eder. Bozuk veya eksik sistem dosyaları tespit edilirse Windows tarafından desteklenen şekilde onarılmaya çalışılır.",
                ActionText = "Tarama Başlat",
                ActionGlyph = Icons.Play
            },
            new ComponentCardViewModel(ComponentKeys.Dism, "Windows Image Sağlık Kontrolü", Icons.Dism)
            {
                IsMaintenance = true,
                ShortTitle = "DISM",
                CommandText = DismManager.DisplayCommand,
                Description = "Windows bileşen deposunun sağlık durumunu kontrol eder; onarılabilir bozulma bulunursa onayınızla onarır.",
                InfoText = "DISM /CheckHealth ile Windows bileşen deposunun sağlık durumunu kontrol eder. Sonuç \"onarılabilir\" ise, " +
                           "yalnızca sizin onayınızla (Güncelle / Onar) DISM /RestoreHealth çalıştırılır ve onarımdan sonra durum " +
                           "yeniden kontrol edilerek doğrulanır. Onayınız olmadan hiçbir onarım yapılmaz.",
                ActionText = "Kontrolü Başlat",
                ActionGlyph = Icons.Search
            },
            new ComponentCardViewModel(ComponentKeys.Mrt, "Microsoft Kötü Amaçlı Yazılım Temizleme Aracı", Icons.Mrt)
            {
                IsMaintenance = true,
                ShortTitle = "MRT",
                CommandText = "MRT — Hızlı Tarama",
                Description = "Windows MRT aracını kullanarak hızlı bir kötü amaçlı yazılım taraması gerçekleştirir.",
                InfoText = "Windows'un yerleşik Microsoft Kötü Amaçlı Yazılım Temizleme Aracıdır. Bu kart MRT'nin hızlı tarama modunu kullanır.",
                ActionText = "Hızlı Taramayı Başlat",
                ActionGlyph = Icons.Play
            },
            new ComponentCardViewModel(ComponentKeys.TempFiles, "Windows Geçici Dosyalar", Icons.TempFiles)
            {
                ShortTitle = "Geçici Dosyalar",
                CommandText = "%TEMP% · %WINDIR%\\Temp · DO önbelleği",
                Description = "Windows'un güvenli şekilde temizlenebilecek geçici dosyalarını kontrol eder ve onayınızla temizler.",
                InfoText = "Ayarlar > Sistem > Depolama > Geçici dosyalar bölümündeki güvenli kategorileri ölçer: kullanıcı ve Windows geçici " +
                           "klasörleri, Teslim En İyileştirme önbelleği, Windows hata raporları ve DirectX gölgelendirici önbelleği. " +
                           "Son 24 saatte değişen ve kullanımdaki dosyalara dokunulmaz; Çöp Kutusu, İndirilenler ve Windows.old hariçtir."
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
        };

        // Uygulama içi güncelleme (zorunlu): yeni sürüm varsa ana ekranın önüne pencere gelir.
        Update = new UpdateViewModel(_logger);
        Update.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateViewModel.IsRequired)) OnPropertyChanged(nameof(ShowUpdateOverlay));
        };

        // Kontrol Merkezi: mevcut kartlar (aynı nesneler) 7 kategori altında gruplanır; yalnızca arayüz düzenidir.
        // Her kategori soldaki alt menüde bölmelere ayrılır (Monster düzeni); aynı bölme türü birden fazla kategoride bulunabilir.
        ComponentCardViewModel CardOf(string key) => Cards.First(c => c.Key == key);
        SectionViewModel Log() => new(SectionKeys.Log, "İşlem Günlüğü", "");
        SectionViewModel Found() => new(SectionKeys.Found, "Bulunan Güncellemeler", "");
        Categories =
        [
            new CategoryViewModel(CategoryKeys.Update, "Güncelleme", "", "Windows ve uygulama güncellemeleri",
                [CardOf(ComponentKeys.WindowsUpdate), CardOf(ComponentKeys.Winget), CardOf(ComponentKeys.Store),
                 CardOf(ComponentKeys.Nvidia), CardOf(ComponentKeys.Defender)],
                [new(SectionKeys.Cards, "Güncellemeler", ""), Found(), Log()]),
            new CategoryViewModel(CategoryKeys.Cleanup, "Temizleme", "", "Geçici dosyalar ve Çöp Kutusu",
                [CardOf(ComponentKeys.TempFiles), CardOf(ComponentKeys.RecycleBin)],
                [new(SectionKeys.Cards, "Temizlik", ""), Log()]),
            new CategoryViewModel(CategoryKeys.Health, "Cihaz Sağlık", "", "Windows sistem sağlık kontrolleri",
                [CardOf(ComponentKeys.Sfc), CardOf(ComponentKeys.Dism), CardOf(ComponentKeys.Mrt)],
                [new(SectionKeys.Cards, "Sağlık Araçları", ""), Log()]),
            new CategoryViewModel(CategoryKeys.SpeedTest, "Hız Testi", "", "İnternet hızı, ping ve paket kaybı", [],
                [new(SectionKeys.SpeedTest, "Hız Testi", ""), new(SectionKeys.SpeedServers, "Sunucu", ""),
                 new(SectionKeys.SpeedHistory, "Sonuçlar", ""),
                 new(SectionKeys.SpeedMethod, "Yöntem", "")]),
            new CategoryViewModel(CategoryKeys.Settings, "Genel Ayarlar", "", "Bildirimler, günlük ve uygulama tercihleri", [],
                [new(SectionKeys.Quick, "Kolay Ayar", ""), new(SectionKeys.Admin, "Yönetici Yetkisi", ""),
                 new(SectionKeys.LogFiles, "Günlük Dosyaları", "")]),
            new CategoryViewModel(CategoryKeys.Summary, "Özet", "", "Sistem sağlığı, son işlem ve geçmiş", [],
                [new(SectionKeys.Health, "Sağlık Özeti", ""), new(SectionKeys.Recent, "Son İşlemler", ""), Found(), Log()]),
            new CategoryViewModel(CategoryKeys.Device, "Cihaz Bilgileri", "", "Donanım, Windows ve sistem durumu", [],
                [new(SectionKeys.DeviceInfo, "Cihaz Bilgileri", ""), new(SectionKeys.DeviceStatus, "Cihaz Durumu", ""),
                 new(SectionKeys.DeviceAbout, "Hakkında", "")])
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

        ContinueCommand = new RelayCommand(GoToDashboard, () => Accepted && IsSupported && HasRtx);
        ContinueWithoutGpuCommand = new RelayCommand(GoToDashboard, () => Accepted && IsSupported && !HasRtx);
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
    private bool CanStartOperation => !IsBusy && SpeedTest?.IsWorking != true;

    public UpdateViewModel Update { get; }

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
            _logger.Info(value ? "Windows bildirimleri açıldı." : "Windows bildirimleri kapatıldı.");
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
        // Sunucu bölmesi (veya Ookla seçiliyken Hız Testi) açılınca Ookla aracı denetlenir; sistemde değişiklik yapmaz.
        if (CurrentSectionKey == SectionKeys.SpeedServers || CurrentSectionKey == SectionKeys.SpeedTest && SpeedTest.IsOoklaProvider)
            _ = SpeedTest.EnsureOoklaAsync();
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
                _ => (ComponentStatus.NotChecked, NotificationsEnabled ? "Bildirimler açık" : "Bildirimler kapalı")
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
    public string PlatformText => HasRtx ? "Windows 11 · NVIDIA RTX" : "Windows 11 · kartsız mod";

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
        OperationState.Checking => "Kontrol devam ediyor...",
        OperationState.Updating => "İşlem devam ediyor...",
        OperationState.Completed => "İşlem tamamlandı",
        OperationState.Failed => "İşlem tamamlandı – hata var",
        OperationState.Cancelled => "İşlem iptal edildi",
        _ => "Hazır"
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
            ? "Tümünü Güncelle ve Temizle"
            : "Tümünü Güncelle";

    public string AvailableSummary
    {
        get
        {
            if (RequirementsChecked && !IsAdmin)
                return "Yönetici yetkisi yok: tüm kartlar kullanım dışı. Kontrol ve güncelleme için uygulamayı yönetici olarak yeniden başlatın.";
            if (SpeedTest?.IsInstalling == true)
                return "Ookla Speedtest aracı kuruluyor. Kontrol ve güncelleme işlemleri kurulum bitince başlatılabilir.";
            if (SpeedTest?.IsRunning == true)
                return "Hız testi sürüyor. Ölçümü etkilememesi için kontrol ve güncelleme işlemleri test bitince başlatılabilir.";
            if (_checks.Count == 0)
                return _updatesApplied
                    ? "İşlemler uygulandı. Yeni durum için yeniden kontrol edin."
                    : "Henüz kontrol yapılmadı. Önce \"Tümünü Kontrol Et\" veya \"Seçilenleri Kontrol Et\" çalıştırılmalı.";
            var n = _checks.Values.Count(c => c.HasActionableUpdates);
            return n == 0 ? "Kontrol edilen kartlarda uygulanacak işlem yok" : $"{n} kartta güncelleme / işlem uygulanmaya hazır";
        }
    }

    // --- seçim ---
    public int SelectedCount => _selection.Count;
    public bool HasSelection => _selection.HasSelection;
    public string SelectionText => $"{_selection.Count} işlem seçildi";

    /// <summary>
    /// Seçilen kartların işlem türüne göre buton metni: yalnızca güncelleme kartları seçiliyse
    /// "Seçilenleri Güncelle", bakım/temizlik kartı da seçiliyse "Seçilenleri Çalıştır".
    /// </summary>
    public string UpdateSelectedText =>
        _selection.SelectedKeys.Any(k => k is ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt
            or ComponentKeys.RecycleBin or ComponentKeys.TempFiles)
            ? "Seçilenleri Çalıştır"
            : "Seçilenleri Güncelle";

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
            return $"Yüklü · {layout.InstallDir} · kaldırmak için Windows Ayarlar → Uygulamalar";
        var installed = new InstallerService(layout, _logger.Info).ReadInstalled();
        return installed?.Version is { } version
            ? $"Taşınabilir kopya çalışıyor · yüklü sürüm {version} ({layout.InstallDir})"
            : "Taşınabilir (yüklü değil) · kurmak için E-mre Control Center Setup dosyasını kullanın";
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
        _logger.Info(AppInfo.Name + " v" + AppInfo.Version + " başlatıldı.");
        RequirementsResult req;
        try
        {
            req = await _requirements.CheckAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Sistem gereksinimleri kontrol edilemedi: " + ex.Message);
            req = new RequirementsResult { Error = ex.Message, OsDescription = "Okunamadı", GpuDescription = "Okunamadı" };
        }

        if (!ApplyRequirements(req)) return;

        // Sistem bilgileri arka planda okunur; arayüz beklemez.
        _ = RefreshSystemInfoAsync();

        // Yeni sürüm denetimi (arka planda; sonuç gelince zorunlu güncelleme penceresi girişten sonra gösterilir).
        if (UpdateViewModel.AutoCheckEnabled) _ = Update.CheckAsync();

        if (_argAccepted)
        {
            Accepted = true;
            GoToDashboard();
            if (_argStartCheck && IsAdmin)
                await StartCheckAsync();
            return;
        }

        if (!IsAdmin)
            await PromptElevationAsync(false);
    }

    private const string NoAdminReason =
        "Yönetici yetkisi yok. Kontrol ve güncelleme için uygulamayı \"Yönetici olarak yeniden başlat\" ile açın (UAC onayı).";

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
        AdminRow.Detail = req.IsAdministrator ? "Yönetici olarak çalışıyor" : "Yönetici olarak çalışmıyor – tüm kartlar kullanım dışı";

        HasRtx = req.HasRtxGpu;
        if (!req.HasRtxGpu)
        {
            NoRtxMessage = "NVIDIA GeForce RTX ekran kartı bulunamadı. Algılanan: " + req.GpuDescription + ".\n" +
                           "Uygulamayı \"Kartsız Devam Et\" ile kullanabilirsiniz: NVIDIA Driver kartı kullanım dışı olur, " +
                           "diğer tüm işlemler normal çalışır.";
            var nvidia = Cards.First(c => c.Key == ComponentKeys.Nvidia);
            nvidia.MarkUnavailable("NVIDIA GeForce RTX ekran kartı bulunamadı. Algılanan: " + req.GpuDescription + ". " +
                                   "NVIDIA sürücü kontrolü ve güncellemesi yapılmaz.");
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

        if (!req.IsSupported)
        {
            // Windows 11 zorunludur: uygun değilse uygulamanın hiçbir işlemi kullanılamaz (giriş engellenir).
            UnsupportedMessage = "Bu uygulama bu sistem için desteklenmiyor.\n" +
                                 "Windows 11 gerekli (algılanan: " + req.OsDescription + "). Uygulamanın hiçbir işlemi kullanılamaz.";
            _logger.Error("Sistem desteklenmiyor (Windows 11 değil); devam edilemez.");
            CommandManager.InvalidateRequerySuggested();
            return false;
        }

        CommandManager.InvalidateRequerySuggested();
        return true;
    }

    private void GoToDashboard()
    {
        if (!Accepted || !IsSupported) return;
        IsDashboard = true;
        if (HasRtx) _logger.Info("Ana ekran açıldı.");
        else _logger.Warning("Ana ekran kartsız açıldı: NVIDIA RTX ekran kartı yok, NVIDIA Driver kartı kullanım dışı.");
        if (!IsAdmin)
            _logger.Warning("Yönetici yetkisi yok: tüm kartlar kullanım dışı. Kontrol ve güncelleme için \"Yönetici olarak yeniden başlat\" kullanılmalı.");
    }

    // ------------------------------------------------------------------ elevation

    private async Task PromptElevationAsync(bool startCheckAfter)
    {
        var ok = await Dialog.ShowAsync(
            "Yönetici izni gerekiyor",
            AppInfo.Name + "; Windows Update, NVIDIA sürücüsü, uygulama ve Defender güncellemelerini kurabilmek, " +
            "SFC / DISM / MRT sistem bakım araçlarını çalıştırabilmek için yönetici yetkisine ihtiyaç duyar.\n\n" +
            "Devam ettiğinizde Windows, \"Bu uygulamanın cihazınızda değişiklik yapmasına izin veriyor musunuz?\" sorusunu soran UAC penceresini açacak. " +
            "Bu izin yalnızca sistem güncellemeleri ve bakım işlemleri için kullanılır. İzin vermezseniz sistemde hiçbir değişiklik yapılmaz.",
            Icons.Shield, DialogKind.Question, "Yönetici olarak başlat", "Şimdi değil");

        if (!ok)
        {
            _logger.Warning("Kullanıcı yönetici olarak yeniden başlatmayı erteledi.");
            return;
        }

        _logger.Info("Windows UAC onayı isteniyor...");
        var args = Accepted || IsDashboard
            ? (startCheckAfter
                ? new[] { AdminPrivilegeManager.ArgAccepted, AdminPrivilegeManager.ArgStartCheck }
                : new[] { AdminPrivilegeManager.ArgAccepted })
            : Array.Empty<string>();

        var (outcome, error) = AdminPrivilegeManager.RelaunchElevated(args);
        switch (outcome)
        {
            case ElevationOutcome.Started:
                _logger.Success("Uygulama yönetici olarak yeniden başlatılıyor.");
                Application.Current.Shutdown();
                break;
            case ElevationOutcome.Declined:
                _logger.Error("Yönetici izni reddedildi. Sistem üzerinde değişiklik yapılmayacak.");
                AdminRow.Detail = "UAC izni reddedildi – tüm kartlar kullanım dışı";
                await Dialog.ShowAsync("Yönetici izni reddedildi",
                    "UAC penceresinde izin verilmedi. Yönetici yetkisi olmadan uygulamaya girebilirsiniz ancak tüm kartlar kullanım dışı " +
                    "olur; hiçbir kontrol veya güncelleme yapılamaz. Sistemde herhangi bir değişiklik yapılmadı.\n\n" +
                    "İstediğiniz zaman \"Yönetici olarak yeniden başlat\" butonuyla tekrar deneyebilirsiniz.",
                    Icons.Warning, DialogKind.Warning, "Tamam");
                break;
            default:
                _logger.Error(error ?? "Yönetici olarak yeniden başlatılamadı.");
                await Dialog.ShowAsync("Yeniden başlatılamadı", error ?? "Bilinmeyen hata.", Icons.Warning, DialogKind.Warning, "Tamam");
                break;
        }
    }

    /// <summary>Yönetici değilse izin ister ve false döner (işlem yapılmaz).</summary>
    private async Task<bool> EnsureElevatedAsync(bool startCheckAfter)
    {
        if (AdminPrivilegeManager.IsElevated) return true;
        _logger.Warning("Bu işlem için yönetici yetkisi gerekiyor.");
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
        var completed = await RunBusyAsync(updatePhase: false,
            async ct => results = await _orchestrator.RunAllChecksAsync(Reporters(), ct),
            done =>
            {
                StoreChecks(results);
                var anyError = FinishOperation("Sistem kontrolü", results, done, updatePhase: false, single: false, NotifyPolicy.Always);
                var ready = _checks.Values.Any(c => c.HasActionableUpdates);
                return new OperationEnd(anyError,
                    ready ? "Kontrol tamamlandı – işlemler hazır" : "Kontrol tamamlandı – uygulanacak işlem yok",
                    ready ? "Kontrol tamamlandı – işlemler hazır, bazı kontroller başarısız" : "Kontrol tamamlandı – bazı kontroller başarısız oldu",
                    "Kontrol iptal edildi");
            });
        if (!completed) return;

        if (!_checks.Values.Any(c => c.HasActionableUpdates))
        {
            // Otomatik uygulanacak bir şey yoksa, varsa manuel güncellemeler (bu oturumda henüz sorulmamışsa) sunulur.
            if (await OfferManualUpdatesAsync(_checks.Values.ToList(), onlyNotOffered: true))
            {
                await ShowResultsAsync("İşlem Tamamlandı", "Aşağıdaki sonuçlar sistemden okunan gerçek durumu gösterir.");
                return;
            }
            await ShowResultsAsync("Kontrol Tamamlandı", OperationState == OperationState.Failed
                ? "Kontroller tamamlandı ancak bazıları başarısız oldu (nedenleri aşağıda). Başarılı kontrollerde uygulanacak bir işlem bulunamadı."
                : "Tüm kontroller gerçek sistem verileriyle tamamlandı. Uygulanacak bir güncelleme veya işlem bulunamadı.");
        }
        else
        {
            _logger.Info("İşlemleri uygulamak için \"" + UpdateAllText + "\" veya \"" + UpdateSelectedText + "\" butonunu kullanın.");
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
                var anyError = FinishOperation("Seçilen kontroller", results, done, updatePhase: false, single: false, NotifyPolicy.Always);
                var ready = keys.Any(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates);
                return new OperationEnd(anyError,
                    ready ? "Seçilen kontroller tamamlandı – işlemler hazır" : "Seçilen kontroller tamamlandı – uygulanacak işlem yok",
                    ready ? "Seçilen kontroller tamamlandı – işlemler hazır, bazı kontroller başarısız" : "Seçilen kontroller tamamlandı – bazıları başarısız oldu",
                    "Kontrol iptal edildi");
            });
        if (!completed) return;

        if (!keys.Any(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates))
        {
            var selectedChecks = keys.Where(_checks.ContainsKey).Select(k => _checks[k]).ToList();
            if (await OfferManualUpdatesAsync(selectedChecks, onlyNotOffered: true))
            {
                await ShowResultsAsync("İşlem Tamamlandı", "Seçilen kartların sistemden okunan gerçek sonuçları:", keys);
                return;
            }
            await ShowResultsAsync("Kontrol Tamamlandı", OperationState == OperationState.Failed
                ? "Seçilen kontrollerin bazıları başarısız oldu (nedenleri aşağıda). Başarılı kontrollerde uygulanacak bir işlem bulunamadı."
                : "Seçilen kartlar gerçek sistem verileriyle kontrol edildi. Uygulanacak bir güncelleme veya işlem bulunamadı.", keys);
        }
        else
        {
            _logger.Info("Seçilen kartlardaki işlemleri uygulamak için \"" + UpdateSelectedText + "\" butonunu kullanın.");
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
            "Güncellemeleri onaylayın",
            "Aşağıdaki işlemler sırayla gerçekleştirilecek. Yalnızca kontrolde işlem gerektirdiği görülen bileşenlere dokunulur. " +
            "Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.",
            Icons.Download, DialogKind.Question, "Onayla ve başlat", "Vazgeç", bullets,
            choices: choices, choicesTitle: choices is null ? null : "TEMİZLENECEK GEÇİCİ DOSYA KATEGORİLERİ");
        if (!ok)
        {
            _logger.Info("Kullanıcı güncellemeyi iptal etti; hiçbir değişiklik yapılmadı.");
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
                var anyError = FinishOperation("Güncelleme işlemleri", results, done, updatePhase: true, single: false, NotifyPolicy.Always);
                return new OperationEnd(anyError, "Güncelleme işlemleri tamamlandı", "Güncelleme işlemleri bitti – bazı işlemler başarısız oldu");
            });

        await OfferInUseRetryAsync(results);
        // Otomatik uygulanmayan güncellemeler (varsa) ayrıca ve seçmeli olarak sunulur.
        await OfferManualUpdatesAsync(snapshot.Values.ToList(), onlyNotOffered: false);
        await ShowResultsAsync("İşlem Tamamlandı", "Aşağıdaki sonuçlar sistemden okunan gerçek durumu gösterir.");
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
            _logger.Warning($"{_orchestrator.NameOf(k)}: bu oturumda kontrol edilmediği için atlandı (önce kontrol edilmeli).");
        // 2) Seçilenlerden gerçekten işlem gerektirenleri belirle.
        var actionable = keys.Where(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates).ToList();
        var notNeeded = keys.Except(actionable)
            .Select(k => $"{_orchestrator.NameOf(k)} ({(_checks.TryGetValue(k, out var c) ? c.Summary : "kontrol edilmedi")})")
            .ToList();

        if (actionable.Count == 0)
        {
            if (await OfferManualUpdatesAsync(keys.Where(_checks.ContainsKey).Select(k => _checks[k]).ToList(), onlyNotOffered: false))
            {
                await ShowResultsAsync("İşlem Tamamlandı", "Seçilen kartların sistemden okunan gerçek sonuçları:", keys);
                return;
            }
            StepText = "Seçilen kartlarda uygulanacak işlem yok";
            _logger.Info("Seçilen kartların hiçbiri işlem gerektirmiyor; hiçbir şey çalıştırılmadı.");
            await ShowResultsAsync("Yapılacak işlem yok",
                "Seçilen kartlarda güncelleme veya işlem gerektiren bir durum bulunamadı. Hiçbir işlem çalıştırılmadı.", keys);
            return;
        }

        var message = "Yalnızca seçtiğiniz kartlardan işlem gerektirenler sırayla çalıştırılacak. Seçilmeyen kartlara dokunulmaz. " +
                      "Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.";
        if (notNeeded.Count > 0)
            message += "\n\nİşlem gerektirmediği için atlanacak: " + string.Join(", ", notNeeded) + ".";

        var selectedChoices = BuildTempChoices(actionable);
        var ok = await Dialog.ShowAsync(
            UpdateSelectedText + " – onay",
            message,
            Icons.Play, DialogKind.Question, "Onayla ve başlat", "Vazgeç",
            actionable.Select(k => BuildBullet(k, _checks[k])),
            choices: selectedChoices, choicesTitle: selectedChoices is null ? null : "TEMİZLENECEK GEÇİCİ DOSYA KATEGORİLERİ");
        if (!ok)
        {
            _logger.Info("Kullanıcı seçilen işlemleri iptal etti; hiçbir değişiklik yapılmadı.");
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
                var anyError = FinishOperation("Seçilen işlemler", results, done, updatePhase: true, single: false, NotifyPolicy.Always);
                return new OperationEnd(anyError, "Seçilen işlemler tamamlandı", "Seçilen işlemler bitti – bazı işlemler başarısız oldu");
            });

        await OfferInUseRetryAsync(results);
        await OfferManualUpdatesAsync(keys.Where(snapshot.ContainsKey).Select(k => snapshot[k]).ToList(), onlyNotOffered: false);
        await ShowResultsAsync("İşlem Tamamlandı", "Seçilen kartların sistemden okunan gerçek sonuçları:", keys);
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
                var anyError = FinishOperation($"{card.ShortTitle} kontrolü", results, done, updatePhase: false, single: true, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, card.Title + ": kontrol tamamlandı", card.Title + ": kontrol başarısız oldu", "Kontrol iptal edildi");
            });
        if (!completed || !_checks.TryGetValue(card.Key, out var check)) return;
        if (!check.HasActionableUpdates)
        {
            // Kartın kendi butonu açık bir kullanıcı isteğidir: otomatik uygulanmayan güncellemeler her seferinde sunulur.
            if (HasManualUpdates(check) && await OfferManualUpdatesAsync([check], onlyNotOffered: false))
                await ShowResultsAsync(card.Title, "Sistemden okunan gerçek sonuç:", [card.Key]);
            return;
        }

        var isRecycle = card.Key == ComponentKeys.RecycleBin;
        var isTemp = card.Key == ComponentKeys.TempFiles;
        var cardChoices = BuildTempChoices([card.Key]);
        var tempMessage = $"Geçici dosyalar temizlenecek.\n\nTahmini temizlenecek alan: {check.Summary.Replace("Temizlenebilir: ", "")}" +
                          (string.IsNullOrWhiteSpace(check.Reason) ? string.Empty : "\n\n" + check.Reason) +
                          "\n\nYalnızca seçili kategoriler temizlenir; kullanımdaki ve son 24 saatte değişen dosyalara dokunulmaz. " +
                          "Temizlikten sonra alan yeniden ölçülür. Devam etmek istiyor musunuz?";
        var ok = await Dialog.ShowAsync(
            isRecycle ? "Çöp kutusu temizlensin mi?" : isTemp ? "Geçici dosyalar temizlensin mi?" : card.Title + ": güncelleme mevcut",
            isRecycle
                ? "Çöp kutusundaki öğeler kalıcı olarak silinecek. Bu işlem geri alınamaz."
                : isTemp
                    ? tempMessage
                    : "Kontrol sonucunda aşağıdaki işlem bulundu. Şimdi uygulamak ister misiniz? " +
                      "Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.",
            isRecycle || isTemp ? Icons.RecycleBin : Icons.Download, DialogKind.Question,
            isRecycle || isTemp ? "Temizle" : "Şimdi güncelle", "Şimdi değil",
            isTemp ? null : [BuildBullet(card.Key, check)],
            choices: cardChoices, choicesTitle: cardChoices is null ? null : "KATEGORİLER");
        if (!ok)
        {
            _logger.Info($"{card.Title}: kullanıcı işlemi erteledi; hiçbir değişiklik yapılmadı.");
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
                var anyError = FinishOperation($"{card.ShortTitle} işlemi", updateResults, done, updatePhase: true, single: true, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, card.Title + ": işlem tamamlandı", card.Title + ": işlem başarısız oldu");
            });

        await OfferInUseRetryAsync(updateResults);
        await OfferManualUpdatesAsync([check], onlyNotOffered: false);
        await ShowResultsAsync(card.Title, "Sistemden okunan gerçek sonuç:", [card.Key]);
    }

    // ------------------------------------------------------------------ BAKIM KARTI BUTONLARI

    private async Task RunMaintenanceAsync(ComponentCardViewModel card)
    {
        if (!CanStartOperation || !await EnsureElevatedAsync(startCheckAfter: false)) return;

        if (card.Key == ComponentKeys.Sfc)
        {
            var ok = await Dialog.ShowAsync(
                "Sistem dosyası taraması",
                "SFC /SCANNOW, Windows sistem dosyalarını tarar ve bozuk dosya bulursa bunları Windows'un kendi bileşen deposundan onarmayı dener.\n\n" +
                "İşlem 10-30 dakika sürebilir. Onarım başladıktan sonra yarıda kesilmez; bu sırada bilgisayarı kapatmayın.",
                Icons.Sfc, DialogKind.Question, "Taramayı başlat", "Vazgeç");
            if (!ok)
            {
                _logger.Info("[SFC] Kullanıcı taramayı başlatmadı.");
                return;
            }
        }

        var scope = card.Key switch
        {
            ComponentKeys.Sfc => "SFC taraması",
            ComponentKeys.Mrt => "MRT hızlı taraması",
            _ => "DISM sağlık kontrolü"
        };
        ModuleResult? result = null;
        await RunBusyAsync(updatePhase: card.Key == ComponentKeys.Sfc,
            async ct => result = await _orchestrator.RunMaintenanceActionAsync(card.Key, Reporters(), ct),
            done =>
            {
                if (result is null) return new OperationEnd(false, card.Title + " tamamlandı");
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
                return new OperationEnd(anyError, card.Title + " tamamlandı", card.Title + ": işlem başarısız oldu");
            });
        if (result is null) return;

        // MRT tehdit bulduysa temizlik yalnızca açık onayla yapılır.
        if (card.Key == ComponentKeys.Mrt && result.HasActionableUpdates)
        {
            var clean = await Dialog.ShowAsync(
                "Kötü amaçlı yazılım tespit edildi",
                "MRT hızlı taraması tehdit tespit etti:\n" + result.Details +
                "\n\nTespit edilenleri kaldırmak için MRT hızlı taraması temizleme modunda çalıştırılacak. Onaylıyor musunuz?",
                Icons.Warning, DialogKind.Warning, "Temizle", "Şimdi değil");
            if (clean)
                await RunSingleFollowUpAsync(card, "MRT temizliği");
            else
                _logger.Warning("[MRT] Kullanıcı temizliği erteledi; tespit edilen tehditlere dokunulmadı.");
        }

        // DISM "onarılabilir" dediyse onarım (RestoreHealth) yalnızca açık onayla yapılır.
        if (card.Key == ComponentKeys.Dism && result.HasActionableUpdates)
        {
            var repair = await Dialog.ShowAsync(
                "Windows bileşen deposu onarılsın mı?",
                "DISM CheckHealth, Windows bileşen deposunda onarılabilir bozulma buldu.\n\n" +
                "Onaylarsanız " + DismManager.RepairCommand + " çalıştırılır: bozuk bileşenler Windows Update'ten alınan temiz " +
                "dosyalarla onarılır. İşlem 10-60 dakika sürebilir ve başladıktan sonra yarıda kesilmez. " +
                "Onarımdan sonra bileşen deposu yeniden kontrol edilerek sonuç doğrulanır.",
                Icons.Dism, DialogKind.Question, "Onar", "Şimdi değil");
            if (repair)
                await RunSingleFollowUpAsync(card, "DISM onarımı");
            else
                _logger.Info("[DISM] Kullanıcı onarımı erteledi; bileşen deposuna dokunulmadı.");
        }

        await ShowResultsAsync(card.Title, "Windows aracının verdiği gerçek sonuç:", [card.Key]);
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
                return new OperationEnd(anyError, $"{scope} tamamlandı", $"{scope} başarısız oldu");
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
                i.Manual == ManualUpdateKind.TechnologyMismatch ? "kaldır + yeni sürümü kur" : "açık hedeflemeyle güncelle",
                isChecked: false)))
            .ToList();

        var ok = await Dialog.ShowAsync(
            "Otomatik uygulanmayan güncellemeler",
            "Winget bu güncellemeleri otomatik uygulamaz. Uygulamak istediklerinizi işaretleyin; hiçbiri varsayılan olarak seçili değildir.\n\n" +
            "• Açık hedeflemeyle güncelle: uygulama genellikle kendini günceller (ör. Discord açıldığında). Seçerseniz yalnızca bu paket " +
            "hedeflenerek winget ile güncellenir.\n" +
            "• Kaldır + yeni sürümü kur: mevcut sürüm farklı bir kurulum türüyle (ör. MSI) kurulmuş; winget yerinde yükseltemez " +
            "(0x8A15008E). Seçerseniz mevcut sürüm KALDIRILIR ve yeni sürüm kurulur. Kaldırma başarısız olursa hiçbir şey değişmez; " +
            "kurulum başarısız olursa paket kurulu olmadan kalabilir ve bu açıkça bildirilir.",
            Icons.Winget, DialogKind.Question, "Seçilenleri uygula", "Şimdi değil",
            choices: choices, choicesTitle: "OTOMATİK UYGULANMAYAN GÜNCELLEMELER", choicesAreSizes: false);
        var selected = choices.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        if (!ok || selected.Count == 0)
        {
            _logger.Info("Manuel güncellemeler uygulanmadı (kullanıcı seçmedi); hiçbir pakete dokunulmadı.");
            return false;
        }
        _logger.Info("Manuel güncelleme için seçilenler: " + string.Join(", ", choices.Where(c => c.IsChecked).Select(c => c.Label.Replace("   ", " "))));

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
                var anyError = FinishOperation("Manuel güncellemeler", results, done, updatePhase: true,
                    single: results.Count == 1, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, "Manuel güncellemeler tamamlandı", "Manuel güncellemeler bitti – bazı paketler güncellenemedi");
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
                var line = $"{i.Name} ({i.CurrentVersion} → {i.NewVersion}) – kapatılacak: " +
                           string.Join(", ", i.BlockingProcesses.Where(p => p.CanClose).Select(p => $"{p.Name} (PID {p.ProcessId})"));
                var others = i.BlockingProcesses.Where(p => !p.CanClose).ToList();
                if (others.Count > 0) line += ". Kapatılmayacak: " + string.Join(", ", others.Select(p => p.DisplayText));
                bullets.Add(line);
            }
        }

        var ok = await Dialog.ShowAsync(
            "Çalışan uygulamalar güncellemeyi engelliyor",
            "Aşağıdaki paketler, çalışan uygulamalar dosyalarını kullandığı için güncellenemedi (Windows Restart Manager ile tespit edildi).\n\n" +
            "Onaylarsanız bu uygulamalar kapatılır – önce normal kapatma istenir, 10 saniye içinde kapanmazsa Görev Yöneticisi'ndeki " +
            "\"Görevi sonlandır\" gibi sonlandırılır – ve güncelleme yeniden denenir. Kaydedilmemiş çalışmalar kaybolabilir. " +
            "Windows hizmetleri ve sistem işlemleri kapatılmaz.",
            Icons.Warning, DialogKind.Warning, "Kapat ve tekrar dene", "Şimdi değil", bullets);
        if (!ok)
        {
            _logger.Info("Kullanıcı engelleyen uygulamaların kapatılmasını onaylamadı; hiçbir uygulama kapatılmadı.");
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
                var anyError = FinishOperation("Yeniden deneme", retryResults, done, updatePhase: true,
                    single: retryResults.Count == 1, NotifyPolicy.IfLong);
                return new OperationEnd(anyError, "Yeniden deneme tamamlandı", "Yeniden deneme bitti – bazı paketler yine güncellenemedi");
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
            message += "\n\nBazı işlemlerin tamamlanması için yeniden başlatma gerekiyor.";

        var restartRequested = false;
        await Dialog.ShowAsync(title, message, Icons.Check, DialogKind.Result, "Kapat",
            results: rows,
            tertiary: reboot ? "Yeniden başlat" : null,
            tertiaryAction: reboot ? () => { restartRequested = true; Dialog.Close(false); } : null);

        if (restartRequested)
            await RequestRestartAsync();
    }

    private Task ShowNoSelectionAsync()
    {
        _logger.Warning("Seçili işlem yok: lütfen en az bir işlem seçin.");
        return Dialog.ShowAsync("Seçim yapılmadı", "Lütfen en az bir işlem seçin.",
            Icons.Info, DialogKind.Info, "Tamam");
    }

    private async Task RequestRestartAsync()
    {
        var ok = await Dialog.ShowAsync("Yeniden başlatılsın mı?",
            "Bilgisayarınız 60 saniye sonra yeniden başlatılacak. Lütfen açık belgelerinizi kaydedin.\n\n" +
            "Vazgeçerseniz yeniden başlatmayı daha sonra kendiniz yapabilirsiniz.",
            Icons.Restart, DialogKind.Warning, "60 sn sonra yeniden başlat", "Vazgeç");
        if (!ok)
        {
            _logger.Info("Yeniden başlatma kullanıcı tarafından ertelendi.");
            return;
        }

        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        var r = await ProcessRunner.RunCmdAsync(shutdown,
            ["/r", "/t", "60", "/c", AppInfo.Name + ": Guncellemeleri tamamlamak icin yeniden baslatiliyor. Iptal icin: shutdown /a"],
            TimeSpan.FromSeconds(30), CancellationToken.None);
        if (r.Succeeded)
            _logger.Warning("Bilgisayar 60 saniye içinde yeniden başlatılacak (iptal için komut satırında: shutdown /a).");
        else
            _logger.Error("Yeniden başlatma planlanamadı: " + ProcessRunner.Describe(r, "shutdown"));
    }

    private void Cancel()
    {
        if (_cts is null || _cancelRequested) return;
        _cancelRequested = true;
        _cts.Cancel();
        _logger.Warning(IsUpdatePhase
            ? "İptal istendi: devam eden işlem güvenli şekilde tamamlanacak, kalan adımlar atlanacak."
            : "İptal istendi: kontrol durduruluyor...");
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
            ? "Geçici dosyalar: hiçbir kategori seçilmedi; temizlik yapılmayacak."
            : "Geçici dosyalar: temizlenecek kategoriler – " + string.Join(", ", chosen));
    }

    private string BuildBullet(string key, ModuleResult c)
    {
        switch (key)
        {
            case ComponentKeys.Winget:
                return $"Winget: {c.ActionableCount} uygulama güncellenecek.";
            case ComponentKeys.WindowsUpdate:
                return $"Windows Update: {c.ActionableCount} güncelleştirme indirilip kurulacak.";
            case ComponentKeys.Store:
                return $"Microsoft Store: {c.ActionableCount} uygulama güncellenecek.";
            case ComponentKeys.Nvidia:
                var n = c.Items.FirstOrDefault(i => i.UpdateAvailable);
                return $"NVIDIA: sürücü {n?.CurrentVersion} → {n?.NewVersion} resmi NVIDIA sunucusundan indirilip kurulacak. Kurulum sırasında ekran birkaç kez kararabilir.";
            case ComponentKeys.Defender:
                return "Microsoft Defender: virüs ve tehdit tanımları güncellenecek.";
            case ComponentKeys.Sfc:
                return "Windows Sistem Dosyası Kontrolü: doğrulamada bozuk dosya bulundu; sfc /scannow ile onarım yapılacak (10-30 dk).";
            case ComponentKeys.Dism:
                return "Windows Image: bileşen deposu " + DismManager.RepairCommand + " ile onarılacak (10-60 dk; onarım dosyaları " +
                       "Windows Update'ten indirilebilir, başladıktan sonra yarıda kesilmez). Ardından durum yeniden kontrol edilir.";
            case ComponentKeys.Mrt:
                return "MRT: tespit edilen kötü amaçlı yazılım, MRT hızlı taramasıyla (temizleme modu) kaldırılacak.";
            case ComponentKeys.RecycleBin:
                return $"Çöp Kutusu: {c.ActionableCount} öğe KALICI olarak silinecek.";
            case ComponentKeys.TempFiles:
                return $"Windows Geçici Dosyalar: seçili kategoriler temizlenecek ({c.Summary}). Silinen geçici dosyalar geri alınamaz; " +
                       "temizlikten sonra alan yeniden ölçülür.";
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
        var end = new OperationEnd(false, "İşlem tamamlandı");
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

        var state = error is not null ? OperationState.Failed
            : !completed ? OperationState.Cancelled
            : end.AnyError ? OperationState.Failed
            : OperationState.Completed;

        if (completed && error is null) Progress = 100;
        OperationState = state;
        StepText = state switch
        {
            OperationState.Cancelled => end.CancelledText ?? "İşlem iptal edildi",
            OperationState.Failed when error is not null => "İşlem beklenmeyen bir hatayla durdu (ayrıntılar günlükte)",
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
            Progress = p.Percent;
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
            if (File.Exists(_logger.LogFilePath))
                Process.Start(new ProcessStartInfo(_logger.LogFilePath) { UseShellExecute = true });
            else
                _logger.Warning("Log dosyası henüz oluşturulmadı.");
        }
        catch (Exception ex)
        {
            _logger.Error("Log dosyası açılamadı: " + ex.Message);
        }
    }

    private async Task OpenLogFolderAsync()
    {
        try
        {
            await Task.Run(() => _logger.Flush(TimeSpan.FromSeconds(2)));
            if (File.Exists(_logger.LogFilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logger.LogFilePath}\"") { UseShellExecute = true });
            else if (Directory.Exists(_logger.LogDirectory))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logger.LogDirectory}\"") { UseShellExecute = true });
            else
                _logger.Warning("Log klasörü henüz oluşturulmadı.");
        }
        catch (Exception ex)
        {
            _logger.Error("Log klasörü açılamadı: " + ex.Message);
        }
    }

    /// <summary>Gerçek oturum günlük dosyasını kullanıcının seçtiği konuma kopyalar.</summary>
    private async Task ExportLogsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Günlüğü dışa aktar",
            FileName = $"E-mre-Control-Center-Log-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            Filter = "Metin dosyası (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;

        try
        {
            _logger.Info($"Günlük dışa aktarılıyor: {dialog.FileName}");
            var target = dialog.FileName;
            var lines = await Task.Run(() =>
            {
                _logger.ExportTo(target);
                return File.ReadLines(target).Count();
            });
            _logger.Success($"Günlük dışa aktarıldı: {dialog.FileName} ({lines} satır).");
        }
        catch (Exception ex)
        {
            _logger.Error($"Günlük dışa aktarılamadı: {ex.Message}");
            _ = Dialog.ShowAsync("Dışa aktarılamadı", "Günlük dosyası kopyalanamadı:\n" + ex.Message,
                Icons.Warning, DialogKind.Warning, "Tamam");
        }
    }

    /// <summary>Yalnızca ekrandaki günlüğü temizler; diskteki günlük dosyası korunur.</summary>
    private void ClearLogs()
    {
        Logs.Clear();
        _logger.Info($"Ekrandaki günlük temizlendi. Günlük dosyası korunuyor: {_logger.LogFilePath}");
    }

    // ------------------------------------------------------------------ sistem bilgileri

    private async Task RefreshSystemInfoAsync()
    {
        if (IsSystemInfoLoading) return;
        IsSystemInfoLoading = true;
        SystemInfoStatus = "Sistem bilgileri okunuyor...";
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
            SystemInfoStatus = $"Okunma: {snapshot.CollectedAt:HH:mm}" + (missing > 0 ? $" · {missing} alan alınamadı" : string.Empty);
            RebuildDeviceSections();
        }
        catch (Exception ex)
        {
            SystemInfoStatus = "Sistem bilgileri alınamadı";
            _logger.Error("Sistem bilgileri okunamadı: " + ex.Message);
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
            HealthHeadline = $"İşlem sürüyor ({running} kart çalışıyor)";
        }
        else if (total == 0)
        {
            HealthStatus = ComponentStatus.Unavailable;
            // Tüm kartlar yalnızca ApplyRequirements'ta (IsAdmin ayarlandıktan sonra) kullanım dışı yapılabilir.
            HealthHeadline = !IsAdmin
                ? "Yönetici yetkisi yok – tüm işlemler kullanım dışı"
                : "Tüm işlemler kullanım dışı";
        }
        else if (notRun == total)
        {
            HealthStatus = ComponentStatus.NotChecked;
            HealthHeadline = "Sistem bu oturumda henüz kontrol edilmedi";
        }
        else if (errors > 0)
        {
            HealthStatus = ComponentStatus.Failed;
            HealthHeadline = errors == 1 ? "1 işlemde hata var" : $"{errors} işlemde hata var";
        }
        else if (pending > 0)
        {
            HealthStatus = ComponentStatus.UpdateAvailable;
            HealthHeadline = $"{pending} işlem dikkat gerektiriyor";
        }
        else if (notRun > 0)
        {
            HealthStatus = ComponentStatus.NotChecked;
            HealthHeadline = $"Kontrol edilen {ok} işlem sorunsuz · {notRun} işlem çalıştırılmadı";
        }
        else
        {
            HealthStatus = ComponentStatus.UpToDate;
            HealthHeadline = "Sistem kontrol edildi – sorun bulunmadı";
        }

        var parts = new List<string> { $"{ok} sorunsuz", $"{pending} güncelleme / uyarı", $"{errors} hata", $"{notRun} çalıştırılmadı" };
        if (running > 0) parts.Add($"{running} çalışıyor");
        if (unavailable > 0) parts.Add($"{unavailable} kullanım dışı");
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
        bool updatePhase, bool single, NotifyPolicy notify)
    {
        var all = results?.Values.Where(r => r.Status is not (ComponentStatus.Checking or ComponentStatus.Updating)).ToList() ?? [];
        var anyError = all.Any(IsErrorResult);
        if (all.Count == 0 && completed) return false; // hiçbir modül çalışmadı (ör. işlem gerekmedi)

        var duration = _lastBusyDuration;
        var rec = new OperationRecord
        {
            CompletedAt = DateTime.Now,
            Title = !completed ? $"{scope} iptal edildi." : anyError ? $"{scope} tamamlandı – hata var." : $"{scope} tamamlandı.",
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
            rec.SummaryText = $"{all[0].Summary} · Süre: {durationText}";
        }
        else if (updatePhase)
        {
            // Ör: "4 işlem · 2 başarılı · 1 uyarı · 1 hata · Winget: 2 paket güncellenemedi · Süre: 21 sn"
            var processed = all.Count(r => r.Status != ComponentStatus.Skipped);
            var parts = new List<string> { $"{processed} işlem", $"{rec.Updates} başarılı", $"{rec.Warnings} uyarı", $"{rec.Errors} hata" };
            if (rec.Skipped > 0) parts.Add($"{rec.Skipped} atlandı");
            parts.AddRange(all
                .Where(r => IsErrorResult(r) || r.Status is ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired)
                .Select(r => $"{ShortName(r.Key)}: {r.Summary}"));
            parts.Add($"Süre: {durationText}");
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
                $"{rec.Completed} kontrol tamamlandı",
                found.Count > 0 ? $"{rec.Updates} güncelleme bulundu ({string.Join(", ", found)})" : "güncelleme bulunamadı"
            };
            if (manual > 0) parts.Add($"{manual} manuel güncelleme (otomatik uygulanmaz)");
            parts.Add($"{rec.Warnings} uyarı");
            parts.Add($"{rec.Errors} hata");
            if (rec.Skipped > 0) parts.Add($"{rec.Skipped} atlandı");
            parts.AddRange(all
                .Where(r => r.Status == ComponentStatus.UpdateAvailable && r.Key is ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt)
                .Select(r => $"{ShortName(r.Key)}: {r.Summary}"));
            var recycle = all.FirstOrDefault(r => r.Key == ComponentKeys.RecycleBin && r.Status == ComponentStatus.UpdateAvailable);
            if (recycle is not null) parts.Add($"çöp kutusunda {recycle.ActionableCount} öğe");
            var temp = all.FirstOrDefault(r => r.Key == ComponentKeys.TempFiles && r.Status == ComponentStatus.UpdateAvailable);
            if (temp is not null) parts.Add($"Geçici dosyalar: {temp.Summary}");
            parts.Add($"Süre: {durationText}");
            rec.SummaryText = string.Join(" · ", parts);
        }

        _state.AddOperation(rec);
        RecentOperations.Insert(0, rec);
        while (RecentOperations.Count > 50) RecentOperations.RemoveAt(RecentOperations.Count - 1);
        LastOperation = rec;
        if (anyError) _logger.Warning($"Son işlem: {rec.Title} {rec.SummaryText}");
        else _logger.Info($"Son işlem: {rec.Title} {rec.SummaryText}");

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
        _logger.Error("Beklenmeyen hata: " + ex);
        if (!IsBusy) return;
        // RunBusyAsync dışında kalan beklenmeyen bir durumda da "çalışıyor" hâli ve animasyonlar kapatılır.
        OperationState = OperationState.Failed;
        StepText = "İşlem beklenmeyen bir hatayla durdu (ayrıntılar günlükte)";
        SetBusy(false);
        RaiseSummaryChanged();
    }

    public void Dispose()
    {
        StopDeviceMonitoring();
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

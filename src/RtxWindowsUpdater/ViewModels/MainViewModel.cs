using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Arayüz durumu ve akışı. Sistem işlemlerini doğrudan yapmaz; servisleri/orkestratörü çağırır.
/// Seçim durumu merkezi olarak <see cref="SelectedOperationsManager"/> içinde tutulur.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
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
    private bool _isBusy;
    private bool _isUpdatePhase;
    private string _stepText = "Hazır";
    private double _progress;
    private bool _cleanRecycleBin = true;
    private bool _cancelRequested;
    private bool _updatesApplied;

    public MainViewModel(Logger logger, bool argAccepted, bool argStartCheck)
    {
        _logger = logger;
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
                CommandText = "SFC /SCANNOW",
                Description = "Windows sistem dosyalarını tarar ve bozuk veya eksik dosyaları onarmayı dener.",
                InfoText = "Windows sistem dosyalarının bütünlüğünü kontrol eder. Bozuk veya eksik sistem dosyaları tespit edilirse Windows tarafından desteklenen şekilde onarılmaya çalışılır.",
                ActionText = "Tarama Başlat",
                ActionGlyph = Icons.Play
            },
            new ComponentCardViewModel(ComponentKeys.Dism, "Windows Image Sağlık Kontrolü", Icons.Dism)
            {
                IsMaintenance = true,
                CommandText = DismManager.DisplayCommand,
                Description = "Windows bileşen deposunun sağlık durumunu kontrol eder.",
                InfoText = "Windows bileşen deposunun sağlık durumunu kontrol eder. CheckHealth yalnızca mevcut sağlık durumunu kontrol eder; kullanıcı istemeden RestoreHealth çalıştırmaz.",
                ActionText = "Kontrolü Başlat",
                ActionGlyph = Icons.Search
            },
            new ComponentCardViewModel(ComponentKeys.Mrt, "Microsoft Kötü Amaçlı Yazılım Temizleme Aracı", Icons.Mrt)
            {
                IsMaintenance = true,
                CommandText = "MRT — Hızlı Tarama",
                Description = "Windows MRT aracını kullanarak hızlı bir kötü amaçlı yazılım taraması gerçekleştirir.",
                InfoText = "Windows'un yerleşik Microsoft Kötü Amaçlı Yazılım Temizleme Aracıdır. Bu kart MRT'nin hızlı tarama modunu kullanır.",
                ActionText = "Hızlı Taramayı Başlat",
                ActionGlyph = Icons.Play
            }
        ];

        foreach (var card in Cards)
        {
            var c = card;
            card.SelectionChangedCallback = (key, selected) => _selection.SetSelected(key, selected);
            card.ActionCommand = new AsyncCommand(
                () => c.IsMaintenance ? RunMaintenanceAsync(c) : RunCardCheckAsync(c),
                () => !IsBusy && IsSupported, OnCommandError);
        }
        _selection.SelectionChanged += OnSelectionChanged;

        ContinueCommand = new RelayCommand(GoToDashboard, () => Accepted && IsSupported);
        ElevateCommand = new AsyncCommand(() => PromptElevationAsync(false), () => !IsAdmin && IsSupported, OnCommandError);
        StartCheckCommand = new AsyncCommand(StartCheckAsync, () => !IsBusy && IsSupported, OnCommandError);
        CheckSelectedCommand = new AsyncCommand(CheckSelectedAsync, () => !IsBusy && IsSupported, OnCommandError);
        UpdateAllCommand = new AsyncCommand(UpdateAllAsync, CanUpdateAll, OnCommandError);
        UpdateSelectedCommand = new AsyncCommand(UpdateSelectedAsync, () => !IsBusy && IsSupported, OnCommandError);
        ClearSelectionCommand = new RelayCommand(() => _selection.Clear(), () => _selection.HasSelection);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && !_cancelRequested);
        OpenLogCommand = new RelayCommand(OpenLogFile);

        _logger.LogAdded += OnLogAdded;
    }

    // ------------------------------------------------------------------ state

    public DialogViewModel Dialog { get; } = new();
    public ObservableCollection<RequirementRowViewModel> Requirements { get; }
    public RequirementRowViewModel WindowsRow { get; }
    public RequirementRowViewModel GpuRow { get; }
    public RequirementRowViewModel AdminRow { get; }

    /// <summary>"Sistem İşlemleri" altındaki 9 kartın tamamı (ekran sırasıyla).</summary>
    public ObservableCollection<ComponentCardViewModel> Cards { get; }

    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<UpdateRowViewModel> UpdateRows { get; } = [];

    public bool IsDashboard
    {
        get => _isDashboard;
        private set { Set(ref _isDashboard, value); OnPropertyChanged(nameof(IsRequirementsPage)); }
    }

    public bool IsRequirementsPage => !IsDashboard;

    public bool RequirementsChecked { get => _requirementsChecked; private set => Set(ref _requirementsChecked, value); }
    public bool IsSupported { get => _isSupported; private set { Set(ref _isSupported, value); OnPropertyChanged(nameof(ShowUnsupported)); } }
    public bool ShowUnsupported => RequirementsChecked && !IsSupported;
    public bool IsAdmin { get => _isAdmin; private set { Set(ref _isAdmin, value); OnPropertyChanged(nameof(ShowElevate)); } }
    public bool ShowElevate => RequirementsChecked && IsSupported && !IsAdmin;
    public bool Accepted { get => _accepted; set => Set(ref _accepted, value); }
    public string UnsupportedMessage { get => _unsupportedMessage; private set => Set(ref _unsupportedMessage, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsUpdatePhase { get => _isUpdatePhase; private set => Set(ref _isUpdatePhase, value); }
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public bool CleanRecycleBin
    {
        get => _cleanRecycleBin;
        set { Set(ref _cleanRecycleBin, value); OnPropertyChanged(nameof(UpdateAllText)); CommandManager.InvalidateRequerySuggested(); }
    }

    public string UpdateAllText =>
        CleanRecycleBin && _checks.TryGetValue(ComponentKeys.RecycleBin, out var rb) && rb.HasActionableUpdates
            ? "Tümünü Güncelle ve Temizle"
            : "Tümünü Güncelle";

    public string AvailableSummary
    {
        get
        {
            if (_checks.Count == 0)
                return _updatesApplied ? "İşlemler uygulandı – dilerseniz yeniden kontrol edin" : "Kontrol henüz yapılmadı";
            var n = _checks.Values.Where(c => c.Key != ComponentKeys.RecycleBin && c.HasActionableUpdates).Sum(c => c.ActionableCount);
            return n == 0 ? "Uygulanabilir güncelleme / işlem yok" : $"{n} güncelleme / işlem uygulanmaya hazır";
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
        _selection.SelectedKeys.Any(k => k is ComponentKeys.Sfc or ComponentKeys.Dism or ComponentKeys.Mrt or ComponentKeys.RecycleBin)
            ? "Seçilenleri Çalıştır"
            : "Seçilenleri Güncelle";

    public string LogFilePath => _logger.LogFilePath;

    public string AppVersion { get; } = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString(3) ?? "1.0.0");

    public ICommand ContinueCommand { get; }
    public ICommand ElevateCommand { get; }
    public ICommand StartCheckCommand { get; }
    public ICommand CheckSelectedCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand UpdateSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenLogCommand { get; }

    // ------------------------------------------------------------------ startup

    public async Task InitializeAsync()
    {
        _logger.Info("RTX Windows Updater başlatıldı.");
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

        WindowsRow.State = req.IsWindows11 ? RequirementState.Ok : RequirementState.Failed;
        WindowsRow.Detail = req.OsDescription;
        GpuRow.State = req.HasRtxGpu ? RequirementState.Ok : RequirementState.Failed;
        GpuRow.Detail = req.GpuDescription;
        IsAdmin = req.IsAdministrator;
        AdminRow.State = req.IsAdministrator ? RequirementState.Ok : RequirementState.Failed;
        AdminRow.Detail = req.IsAdministrator ? "Yönetici olarak çalışıyor" : "Yönetici olarak çalışmıyor – UAC onayı gerekli";

        IsSupported = req.IsSupported;
        RequirementsChecked = true;
        OnPropertyChanged(nameof(ShowUnsupported));
        OnPropertyChanged(nameof(ShowElevate));

        if (!req.IsSupported)
        {
            var reasons = new List<string>();
            if (!req.IsWindows11) reasons.Add("Windows 11 gerekli (algılanan: " + req.OsDescription + ").");
            if (!req.HasRtxGpu) reasons.Add("NVIDIA GeForce RTX serisi ekran kartı gerekli (algılanan: " + req.GpuDescription + ").");
            UnsupportedMessage = "Bu uygulama bu sistem için desteklenmiyor.\n" + string.Join("\n", reasons);
            _logger.Error("Sistem desteklenmiyor; devam edilemez.");
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        CommandManager.InvalidateRequerySuggested();

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

    private void GoToDashboard()
    {
        if (!Accepted || !IsSupported) return;
        IsDashboard = true;
        _logger.Info("Ana ekran açıldı.");
    }

    // ------------------------------------------------------------------ elevation

    private async Task PromptElevationAsync(bool startCheckAfter)
    {
        var ok = await Dialog.ShowAsync(
            "Yönetici izni gerekiyor",
            "RTX Windows Updater; Windows Update, NVIDIA sürücüsü, uygulama ve Defender güncellemelerini kurabilmek, " +
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
                AdminRow.Detail = "UAC izni reddedildi – güncelleme yapılamaz";
                await Dialog.ShowAsync("Yönetici izni reddedildi",
                    "UAC penceresinde izin verilmedi. Uygulama güncellemeleri kontrol edip kuramaz; sistemde herhangi bir değişiklik yapılmadı.\n\n" +
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
        if (IsBusy || !await EnsureElevatedAsync(startCheckAfter: true)) return;

        foreach (var c in Cards) c.Reset();
        _checks.Clear();
        _updatesApplied = false;
        RaiseSummaryChanged();

        Dictionary<string, ModuleResult>? results = null;
        var completed = await RunBusyAsync(updatePhase: false, async ct =>
            results = await _orchestrator.RunAllChecksAsync(Reporters(), ct));
        StoreChecks(results);

        if (!completed)
        {
            StepText = "Kontrol iptal edildi";
            return;
        }

        if (!_checks.Values.Any(c => c.HasActionableUpdates))
        {
            StepText = "Kontrol tamamlandı – uygulanacak işlem yok";
            await ShowResultsAsync("Kontrol Tamamlandı",
                "Tüm kontroller gerçek sistem verileriyle tamamlandı. Uygulanacak bir güncelleme veya işlem bulunamadı.");
        }
        else
        {
            StepText = "Kontrol tamamlandı – işlemler hazır";
            _logger.Info("İşlemleri uygulamak için \"" + UpdateAllText + "\" veya \"" + UpdateSelectedText + "\" butonunu kullanın.");
        }
    }

    // ------------------------------------------------------------------ SEÇİLENLERİ KONTROL ET

    private async Task CheckSelectedAsync()
    {
        if (IsBusy) return;
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
        var completed = await RunBusyAsync(updatePhase: false, async ct =>
            results = await _orchestrator.RunSelectedChecksAsync(keys, Reporters(), ct));
        StoreChecks(results);

        if (!completed)
        {
            StepText = "Kontrol iptal edildi";
            return;
        }

        if (!keys.Any(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates))
        {
            StepText = "Seçilen kontroller tamamlandı – uygulanacak işlem yok";
            await ShowResultsAsync("Kontrol Tamamlandı",
                "Seçilen kartlar gerçek sistem verileriyle kontrol edildi. Uygulanacak bir güncelleme veya işlem bulunamadı.", keys);
        }
        else
        {
            StepText = "Seçilen kontroller tamamlandı – işlemler hazır";
            _logger.Info("Seçilen kartlardaki işlemleri uygulamak için \"" + UpdateSelectedText + "\" butonunu kullanın.");
        }
    }

    // ------------------------------------------------------------------ TÜMÜNÜ GÜNCELLE

    private bool CanUpdateAll()
    {
        if (IsBusy) return false;
        return _checks.Values.Any(c => c.HasActionableUpdates && (c.Key != ComponentKeys.RecycleBin || CleanRecycleBin));
    }

    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll() || !await EnsureElevatedAsync(startCheckAfter: false)) return;

        var bullets = _orchestrator.ModuleOrder
            .Where(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates && (k != ComponentKeys.RecycleBin || CleanRecycleBin))
            .Select(k => BuildBullet(k, _checks[k]))
            .ToList();

        var ok = await Dialog.ShowAsync(
            "Güncellemeleri onaylayın",
            "Aşağıdaki işlemler sırayla gerçekleştirilecek. Yalnızca güncellemesi veya işlemi bulunan bileşenlere dokunulur. " +
            "Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.",
            Icons.Download, DialogKind.Question, "Onayla ve başlat", "Vazgeç", bullets);
        if (!ok)
        {
            _logger.Info("Kullanıcı güncellemeyi iptal etti; hiçbir değişiklik yapılmadı.");
            return;
        }

        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? results = null;
        await RunBusyAsync(updatePhase: true, async ct =>
            results = await _orchestrator.RunAllUpdatesAsync(snapshot, CleanRecycleBin, Reporters(), ct));
        StoreUpdates(results);
        StepText = "İşlem tamamlandı";

        await ShowResultsAsync("İşlem Tamamlandı", "Aşağıdaki sonuçlar sistemden okunan gerçek durumu gösterir.");
    }

    // ------------------------------------------------------------------ SEÇİLENLERİ GÜNCELLE / ÇALIŞTIR

    private async Task UpdateSelectedAsync()
    {
        if (IsBusy) return;
        var keys = _selection.SelectedKeys;
        if (keys.Count == 0)
        {
            await ShowNoSelectionAsync();
            return;
        }
        if (!await EnsureElevatedAsync(startCheckAfter: false)) return;

        // 1) Seçilip henüz kontrol edilmemiş kartların önce gerçek kontrolünü yap.
        var missing = keys.Where(k => !_checks.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            _logger.Info("Henüz kontrol edilmemiş seçili kartlar önce kontrol ediliyor: " +
                         string.Join(", ", missing.Select(_orchestrator.NameOf)));
            foreach (var c in Cards.Where(c => missing.Contains(c.Key))) c.Reset();

            Dictionary<string, ModuleResult>? checkResults = null;
            var completed = await RunBusyAsync(updatePhase: false, async ct =>
                checkResults = await _orchestrator.RunSelectedChecksAsync(missing, Reporters(), ct));
            StoreChecks(checkResults);
            if (!completed)
            {
                StepText = "İşlem iptal edildi";
                return;
            }
        }

        // 2) Seçilenlerden gerçekten işlem gerektirenleri belirle.
        var actionable = keys.Where(k => _checks.TryGetValue(k, out var c) && c.HasActionableUpdates).ToList();
        var notNeeded = keys.Except(actionable)
            .Select(k => $"{_orchestrator.NameOf(k)} ({(_checks.TryGetValue(k, out var c) ? c.Summary : "kontrol edilemedi")})")
            .ToList();

        if (actionable.Count == 0)
        {
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

        var ok = await Dialog.ShowAsync(
            UpdateSelectedText + " – onay",
            message,
            Icons.Play, DialogKind.Question, "Onayla ve başlat", "Vazgeç",
            actionable.Select(k => BuildBullet(k, _checks[k])));
        if (!ok)
        {
            _logger.Info("Kullanıcı seçilen işlemleri iptal etti; hiçbir değişiklik yapılmadı.");
            return;
        }

        // 3) Yalnızca seçilenleri çalıştır.
        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? results = null;
        await RunBusyAsync(updatePhase: true, async ct =>
            results = await _orchestrator.RunSelectedUpdatesAsync(snapshot, keys, Reporters(), ct));
        StoreUpdates(results);
        StepText = "Seçilen işlemler tamamlandı";

        await ShowResultsAsync("İşlem Tamamlandı", "Seçilen kartların sistemden okunan gerçek sonuçları:", keys);
    }

    // ------------------------------------------------------------------ KART "KONTROL ET" BUTONU

    /// <summary>
    /// Güncelleme kartlarının (Windows Update, Winget, Store, NVIDIA, Defender, Çöp Kutusu) kendi butonu:
    /// yalnızca o kartı gerçekten kontrol eder. İşlem gerekiyorsa uygulamak için ayrıca onay istenir.
    /// </summary>
    private async Task RunCardCheckAsync(ComponentCardViewModel card)
    {
        if (IsBusy || !await EnsureElevatedAsync(startCheckAfter: false)) return;

        card.Reset();
        _checks.Remove(card.Key);
        RaiseSummaryChanged();

        Dictionary<string, ModuleResult>? results = null;
        var completed = await RunBusyAsync(updatePhase: false, async ct =>
            results = await _orchestrator.RunSingleCheckAsync(card.Key, Reporters(), ct));
        StoreChecks(results);
        StepText = completed ? card.Title + ": kontrol tamamlandı" : "Kontrol iptal edildi";
        if (!completed || !_checks.TryGetValue(card.Key, out var check) || !check.HasActionableUpdates) return;

        var isRecycle = card.Key == ComponentKeys.RecycleBin;
        var ok = await Dialog.ShowAsync(
            isRecycle ? "Çöp kutusu temizlensin mi?" : card.Title + ": güncelleme mevcut",
            (isRecycle
                ? "Çöp kutusundaki öğeler kalıcı olarak silinecek. Bu işlem geri alınamaz."
                : "Kontrol sonucunda aşağıdaki işlem bulundu. Şimdi uygulamak ister misiniz? " +
                  "Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz."),
            isRecycle ? Icons.RecycleBin : Icons.Download, DialogKind.Question,
            isRecycle ? "Temizle" : "Şimdi güncelle", "Şimdi değil",
            [BuildBullet(card.Key, check)]);
        if (!ok)
        {
            _logger.Info($"{card.Title}: kullanıcı işlemi erteledi; hiçbir değişiklik yapılmadı.");
            return;
        }

        var snapshot = new Dictionary<string, ModuleResult>(_checks);
        Dictionary<string, ModuleResult>? updateResults = null;
        await RunBusyAsync(updatePhase: true, async ct =>
            updateResults = await _orchestrator.RunSingleUpdateAsync(snapshot, card.Key, Reporters(), ct));
        StoreUpdates(updateResults);
        StepText = card.Title + ": işlem tamamlandı";

        await ShowResultsAsync(card.Title, "Sistemden okunan gerçek sonuç:", [card.Key]);
    }

    // ------------------------------------------------------------------ BAKIM KARTI BUTONLARI

    private async Task RunMaintenanceAsync(ComponentCardViewModel card)
    {
        if (IsBusy || !await EnsureElevatedAsync(startCheckAfter: false)) return;

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

        ModuleResult? result = null;
        await RunBusyAsync(updatePhase: card.Key == ComponentKeys.Sfc, async ct =>
            result = await _orchestrator.RunMaintenanceActionAsync(card.Key, Reporters(), ct));

        if (result is null) return;
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
        RaiseSummaryChanged();

        // MRT tehdit bulduysa temizlik yalnızca açık onayla yapılır.
        if (card.Key == ComponentKeys.Mrt && result.HasActionableUpdates)
        {
            var clean = await Dialog.ShowAsync(
                "Kötü amaçlı yazılım tespit edildi",
                "MRT hızlı taraması tehdit tespit etti:\n" + result.Details +
                "\n\nTespit edilenleri kaldırmak için MRT hızlı taraması temizleme modunda çalıştırılacak. Onaylıyor musunuz?",
                Icons.Warning, DialogKind.Warning, "Temizle", "Şimdi değil");
            if (clean)
            {
                var snapshot = new Dictionary<string, ModuleResult>(_checks);
                Dictionary<string, ModuleResult>? res = null;
                await RunBusyAsync(updatePhase: true, async ct =>
                    res = await _orchestrator.RunSingleUpdateAsync(snapshot, ComponentKeys.Mrt, Reporters(), ct));
                StoreUpdates(res);
            }
            else
            {
                _logger.Warning("[MRT] Kullanıcı temizliği erteledi; tespit edilen tehditlere dokunulmadı.");
            }
        }

        StepText = card.Title + " tamamlandı";
        await ShowResultsAsync(card.Title, "Windows aracının verdiği gerçek sonuç:", [card.Key]);
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
        var r = await ProcessRunner.RunAsync(shutdown,
            "/r /t 60 /c \"RTX Windows Updater: Guncellemeleri tamamlamak icin yeniden baslatiliyor. Iptal icin: shutdown /a\"",
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
            case ComponentKeys.Mrt:
                return "MRT: tespit edilen kötü amaçlı yazılım, MRT hızlı taramasıyla (temizleme modu) kaldırılacak.";
            case ComponentKeys.RecycleBin:
                return $"Çöp Kutusu: {c.ActionableCount} öğe KALICI olarak silinecek.";
            default:
                return $"{_orchestrator.NameOf(key)}: {c.Summary}";
        }
    }

    /// <summary>İşlemi "meşgul" durumunda çalıştırır; iptal edilmeden tamamlandıysa true döner.</summary>
    private async Task<bool> RunBusyAsync(bool updatePhase, Func<CancellationToken, Task> body)
    {
        _cts = new CancellationTokenSource();
        _cancelRequested = false;
        IsUpdatePhase = updatePhase;
        IsBusy = true;
        try
        {
            await body(_cts.Token);
            return !_cts.IsCancellationRequested;
        }
        finally
        {
            IsBusy = false;
            IsUpdatePhase = false;
            _cts.Dispose();
            _cts = null;
            RebuildUpdateRows();
            RaiseSummaryChanged();
        }
    }

    private void StoreChecks(Dictionary<string, ModuleResult>? results)
    {
        if (results is null) return;
        foreach (var (key, r) in results)
        {
            if (r.Status is ComponentStatus.Skipped or ComponentStatus.Checking) continue;
            _checks[key] = r;
        }
        RaiseSummaryChanged();
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
        RaiseSummaryChanged();
    }

    private OrchestratorReporters Reporters() => new(
        new Progress<StepProgress>(p =>
        {
            StepText = p.Text;
            Progress = p.Percent;
        }),
        new Progress<ModuleResult>(r => Cards.FirstOrDefault(c => c.Key == r.Key)?.Apply(r)),
        new Progress<ModuleProgress>(p => Cards.FirstOrDefault(c => c.Key == p.Key)?.ApplyProgress(p)));

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
        UpdateRows.Clear();
        foreach (var card in Cards)
        {
            var r = card.LastResult;
            if (r is null) continue;
            foreach (var i in r.Items.OrderByDescending(i => i.UpdateAvailable))
            {
                UpdateRows.Add(new UpdateRowViewModel
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
    }

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(AvailableSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnLogAdded(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            Logs.Add(entry);
            while (Logs.Count > MaxLogEntries) Logs.RemoveAt(0);
        });
    }

    private void OpenLogFile()
    {
        try
        {
            if (File.Exists(_logger.LogFilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logger.LogFilePath}\"") { UseShellExecute = true });
            else
                _logger.Warning("Log dosyası henüz oluşturulmadı.");
        }
        catch (Exception ex)
        {
            _logger.Error("Log klasörü açılamadı: " + ex.Message);
        }
    }

    private void OnCommandError(Exception ex)
    {
        _logger.Error("Beklenmeyen hata: " + ex.Message);
        IsBusy = false;
    }

    public void Dispose()
    {
        _logger.LogAdded -= OnLogAdded;
        _selection.SelectionChanged -= OnSelectionChanged;
        _cts?.Cancel();
        _orchestrator.Dispose();
    }
}

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
        public const string Info = "";
        public const string Shield = "";
        public const string Warning = "";
        public const string Check = "";
        public const string Restart = "";
        public const string Download = "";
    }

    private const int MaxLogEntries = 5000;

    private readonly Logger _logger;
    private readonly SystemRequirementsChecker _requirements;
    private readonly UpdateOrchestrator _orchestrator;
    private readonly bool _argAccepted;
    private readonly bool _argStartCheck;

    private CancellationTokenSource? _cts;
    private Dictionary<string, ModuleResult> _checks = new();

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
    private bool _hasCheckResults;
    private bool _cancelRequested;
    private bool _updatesApplied;

    public MainViewModel(Logger logger, bool argAccepted, bool argStartCheck)
    {
        _logger = logger;
        _argAccepted = argAccepted;
        _argStartCheck = argStartCheck;
        _requirements = new SystemRequirementsChecker(logger);
        _orchestrator = new UpdateOrchestrator(logger);

        WindowsRow = new RequirementRowViewModel("Windows 11", Icons.Windows);
        GpuRow = new RequirementRowViewModel("NVIDIA RTX GPU", Icons.Gpu);
        AdminRow = new RequirementRowViewModel("Yönetici yetkisi", Icons.Admin);
        Requirements = [WindowsRow, GpuRow, AdminRow];

        Cards =
        [
            new ComponentCardViewModel(ComponentKeys.WindowsUpdate, "Windows Update", Icons.WindowsUpdate),
            new ComponentCardViewModel(ComponentKeys.Winget, "Winget", Icons.Winget),
            new ComponentCardViewModel(ComponentKeys.Store, "Microsoft Store", Icons.Store),
            new ComponentCardViewModel(ComponentKeys.Nvidia, "NVIDIA Driver", Icons.Nvidia),
            new ComponentCardViewModel(ComponentKeys.Defender, "Microsoft Defender", Icons.Defender),
            new ComponentCardViewModel(ComponentKeys.RecycleBin, "Çöp Kutusu", Icons.RecycleBin)
        ];

        ContinueCommand = new RelayCommand(GoToDashboard, () => Accepted && IsSupported);
        ElevateCommand = new AsyncCommand(() => PromptElevationAsync(false), () => !IsAdmin && IsSupported, OnCommandError);
        StartCheckCommand = new AsyncCommand(StartCheckAsync, () => !IsBusy && IsSupported, OnCommandError);
        UpdateAllCommand = new AsyncCommand(UpdateAllAsync, CanUpdateAll, OnCommandError);
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
    public bool HasCheckResults { get => _hasCheckResults; private set => Set(ref _hasCheckResults, value); }

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
            if (!HasCheckResults)
                return _updatesApplied ? "Güncellemeler uygulandı – dilerseniz yeniden kontrol edin" : "Kontrol henüz yapılmadı";
            var n = _checks.Values.Where(c => c.Key != ComponentKeys.RecycleBin && c.HasActionableUpdates).Sum(c => c.ActionableCount);
            return n == 0 ? "Uygulanabilir güncelleme yok" : $"{n} güncelleme uygulanmaya hazır";
        }
    }

    public string LogFilePath => _logger.LogFilePath;

    public string AppVersion { get; } = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString(3) ?? "1.0.0");

    public ICommand ContinueCommand { get; }
    public ICommand ElevateCommand { get; }
    public ICommand StartCheckCommand { get; }
    public ICommand UpdateAllCommand { get; }
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
            "RTX Windows Updater; Windows Update, NVIDIA sürücüsü, uygulama ve Defender güncellemelerini kurabilmek için yönetici yetkisine ihtiyaç duyar.\n\n" +
            "Devam ettiğinizde Windows, \"Bu uygulamanın cihazınızda değişiklik yapmasına izin veriyor musunuz?\" sorusunu soran UAC penceresini açacak. " +
            "Bu izin yalnızca sistem güncellemeleri için kullanılır. İzin vermezseniz sistemde hiçbir değişiklik yapılmaz.",
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

    // ------------------------------------------------------------------ check

    private async Task StartCheckAsync()
    {
        if (IsBusy) return;
        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Warning("Güncelleme kontrolü için yönetici yetkisi gerekiyor.");
            await PromptElevationAsync(true);
            return;
        }

        foreach (var c in Cards) c.Reset();
        UpdateRows.Clear();
        _checks = new Dictionary<string, ModuleResult>();
        HasCheckResults = false;
        _updatesApplied = false;
        RaiseSummaryChanged();

        _cts = new CancellationTokenSource();
        _cancelRequested = false;
        IsUpdatePhase = false;
        IsBusy = true;
        try
        {
            _checks = await _orchestrator.RunChecksAsync(StepReporter(), ModuleReporter(), _cts.Token);
            HasCheckResults = true;
            RebuildUpdateRows();
            RaiseSummaryChanged();

            var actionable = _checks.Values.Where(c => c.HasActionableUpdates).ToList();
            if (_cts.IsCancellationRequested)
            {
                StepText = "Kontrol iptal edildi";
            }
            else if (actionable.Count == 0)
            {
                StepText = "Kontrol tamamlandı – uygulanacak güncelleme yok";
                await ShowResultsAsync("Kontrol Tamamlandı",
                    "Tüm kontroller gerçek sistem verileriyle tamamlandı. Uygulanacak bir güncelleme bulunamadı.");
            }
            else
            {
                StepText = "Kontrol tamamlandı – güncellemeler hazır";
                _logger.Info("Güncellemeleri kurmak için \"" + UpdateAllText + "\" butonuna basın.");
            }
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    // ------------------------------------------------------------------ update

    private bool CanUpdateAll()
    {
        if (IsBusy || !HasCheckResults) return false;
        return _checks.Values.Any(c => c.HasActionableUpdates && (c.Key != ComponentKeys.RecycleBin || CleanRecycleBin));
    }

    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll()) return;

        var bullets = new List<string>();
        foreach (var card in Cards)
        {
            if (!_checks.TryGetValue(card.Key, out var c) || !c.HasActionableUpdates) continue;
            switch (card.Key)
            {
                case ComponentKeys.Winget:
                    bullets.Add($"Winget: {c.ActionableCount} uygulama güncellenecek.");
                    break;
                case ComponentKeys.WindowsUpdate:
                    bullets.Add($"Windows Update: {c.ActionableCount} güncelleştirme indirilip kurulacak.");
                    break;
                case ComponentKeys.Store:
                    bullets.Add($"Microsoft Store: {c.ActionableCount} uygulama güncellenecek.");
                    break;
                case ComponentKeys.Nvidia:
                    var n = c.Items.FirstOrDefault(i => i.UpdateAvailable);
                    bullets.Add($"NVIDIA: sürücü {n?.CurrentVersion} → {n?.NewVersion} resmi NVIDIA sunucusundan indirilip kurulacak. Kurulum sırasında ekran birkaç kez kararabilir.");
                    break;
                case ComponentKeys.Defender:
                    bullets.Add("Microsoft Defender: virüs ve tehdit tanımları güncellenecek.");
                    break;
                case ComponentKeys.RecycleBin when CleanRecycleBin:
                    bullets.Add($"Çöp Kutusu: {c.ActionableCount} öğe KALICI olarak silinecek.");
                    break;
            }
        }

        var ok = await Dialog.ShowAsync(
            "Güncellemeleri onaylayın",
            "Aşağıdaki işlemler sırayla gerçekleştirilecek. Yalnızca güncellemesi bulunan bileşenlere dokunulur. " +
            "Bilgisayarınız sizin onayınız olmadan yeniden başlatılmaz.",
            Icons.Download, DialogKind.Question, "Onayla ve başlat", "Vazgeç", bullets);
        if (!ok)
        {
            _logger.Info("Kullanıcı güncellemeyi iptal etti; hiçbir değişiklik yapılmadı.");
            return;
        }

        _cts = new CancellationTokenSource();
        _cancelRequested = false;
        IsUpdatePhase = true;
        IsBusy = true;
        try
        {
            var results = await _orchestrator.RunUpdatesAsync(_checks, CleanRecycleBin, StepReporter(), ModuleReporter(), _cts.Token);
            _checks = results;
            RebuildUpdateRows();
            HasCheckResults = false; // yeni bir kontrol yapılmadan tekrar "Tümünü Güncelle" yapılmaz
            _updatesApplied = true;
            RaiseSummaryChanged();
            StepText = "İşlem tamamlandı";

            await ShowResultsAsync("İşlem Tamamlandı",
                "Aşağıdaki sonuçlar sistemden okunan gerçek durumu gösterir.");
        }
        finally
        {
            IsBusy = false;
            IsUpdatePhase = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task ShowResultsAsync(string title, string message)
    {
        var rows = Cards.Select(c => new ResultRowViewModel
        {
            Title = c.Title,
            Glyph = c.Glyph,
            Status = c.Status,
            Summary = c.Summary,
            Reason = c.Reason
        }).ToList();

        var reboot = _checks.Values.Any(r => r.RebootRequired || r.Status == ComponentStatus.RebootRequired);
        if (reboot)
            message += "\n\nBazı güncellemelerin tamamlanması için yeniden başlatma gerekiyor.";

        var restartRequested = false;
        await Dialog.ShowAsync(title, message, Icons.Check, DialogKind.Result, "Kapat",
            results: rows,
            tertiary: reboot ? "Yeniden başlat" : null,
            tertiaryAction: reboot ? () => { restartRequested = true; Dialog.Close(false); } : null);

        if (restartRequested)
            await RequestRestartAsync();
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
            ? "İptal istendi: devam eden kurulum güvenli şekilde tamamlanacak, kalan adımlar atlanacak."
            : "İptal istendi: kontrol durduruluyor...");
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ helpers

    private IProgress<StepProgress> StepReporter() => new Progress<StepProgress>(p =>
    {
        StepText = p.Text;
        Progress = p.Percent;
    });

    private IProgress<ModuleResult> ModuleReporter() => new Progress<ModuleResult>(r =>
    {
        Cards.FirstOrDefault(c => c.Key == r.Key)?.Apply(r);
    });

    private void RebuildUpdateRows()
    {
        UpdateRows.Clear();
        foreach (var card in Cards)
        {
            if (!_checks.TryGetValue(card.Key, out var r)) continue;
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
        _cts?.Cancel();
        _orchestrator.Dispose();
    }
}

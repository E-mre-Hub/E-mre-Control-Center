using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

public enum SetupStepState { Pending, Running, Done, Failed }

/// <summary>Kurulum / kaldırma adım satırı. Durum ve ayrıntı yalnızca servisin gerçek sonucundan gelir.</summary>
public sealed class SetupStepViewModel(string key) : ObservableObject
{
    private SetupStepState _state;
    private string _detail = string.Empty;

    public string Key => key;
    public string Title => InstallSteps.Titles.GetValueOrDefault(key, key);

    public SetupStepState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(Glyph));
        }
    }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public string Glyph => State switch
    {
        SetupStepState.Done => "",
        SetupStepState.Failed => "",
        SetupStepState.Running => "",
        _ => ""
    };
}

/// <summary>Çalışan uygulamanın kapatılması için pencere içi onay (kurulum ve kaldırmada ortak).</summary>
public sealed class RunningAppPrompt : ObservableObject
{
    private TaskCompletionSource<bool>? _answer;
    private bool _isOpen;
    private string _text = string.Empty;

    public RunningAppPrompt()
    {
        ConfirmCommand = new RelayCommand(() => Answer(true));
        CancelCommand = new RelayCommand(() => Answer(false));
    }

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public string Text { get => _text; private set => Set(ref _text, value); }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public Task<bool> AskAsync(string text)
    {
        Text = text;
        _answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        return _answer.Task;
    }

    private void Answer(bool value)
    {
        IsOpen = false;
        _answer?.TrySetResult(value);
    }
}

public enum SetupPage { Welcome, Working, Done, Failed }

/// <summary>
/// Kurulum ekranı: karşılama → (UAC) → adımlar → "Bizi tercih ettiğiniz için teşekkürler" / hata. Yönetici değilse "Yükle" uygulamayı
/// Windows UAC ile yeniden başlatır (<see cref="LaunchModes.ArgInstall"/>); yetki atlatılmaz, reddedilirse hiçbir şey değişmez.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private static readonly string[] StepKeys =
        [InstallSteps.Running, InstallSteps.Copy, InstallSteps.Verify, InstallSteps.StartMenu, InstallSteps.Desktop, InstallSteps.Registry];

    private readonly InstallerService _installer;
    private readonly SetupLog _log;
    private readonly string _sourceExe;
    private readonly bool _isElevated;
    private readonly Func<string[], (ElevationOutcome Outcome, string? Error)> _relaunchElevated;
    private readonly Func<string, string?> _launch;
    private readonly bool _autoFinish;

    private SetupPage _page;
    private bool _desktopShortcut;
    private bool _launchAfter = true;
    private string? _elevationError;
    private double _progress;
    private string _statusText = string.Empty;
    private string _failText = string.Empty;
    private string? _rollbackText;
    private string? _launchError;

    public SetupViewModel(InstallerService installer, SetupLog log, string sourceExe, bool isElevated, bool? desktopShortcut,
        Func<string[], (ElevationOutcome, string?)> relaunchElevated, Func<string, string?>? launch = null, bool autoFinish = false)
    {
        _installer = installer;
        _log = log;
        _sourceExe = sourceExe;
        _isElevated = isElevated;
        _relaunchElevated = relaunchElevated;
        _launch = launch ?? LaunchInstalled;
        _autoFinish = autoFinish;
        Installed = installer.ReadInstalled();
        _desktopShortcut = desktopShortcut ?? (Installed is null || Installed.HasDesktopShortcut);
        Steps = new ObservableCollection<SetupStepViewModel>(StepKeys.Select(k => new SetupStepViewModel(k)));

        InstallCommand = new AsyncCommand(InstallClickedAsync, () => Page == SetupPage.Welcome, OnError);
        RetryCommand = new AsyncCommand(StartInstallAsync, () => Page == SetupPage.Failed, OnError);
        FinishCommand = new RelayCommand(Finish);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(), () => !IsWorking);
        OpenLegalLinkCommand = new RelayCommand(p =>
        {
            // Yalnızca izin listesindeki yasal belge adresleri; tarayıcı yönetici yetkisi olmadan açılır.
            if (p is not string url || !AppInfo.LegalLinks.Contains(url)) return;
            if (ShellOpen.OpenUrl(url) is { } error) _log.Info(L.T($"Bağlantı açılamadı ({url}): {error}", $"Could not open the link ({url}): {error}"));
        });
    }

    public event Action? RequestClose;

    public ObservableCollection<SetupStepViewModel> Steps { get; }
    public RunningAppPrompt RunningPrompt { get; } = new();
    public InstalledInfo? Installed { get; }
    public string Version => AppInfo.Version;
    public string InstallDir => _installer.Layout.InstallDir;
    public string LogPath => _log.FilePath;

    public ICommand InstallCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenLegalLinkCommand { get; }

    public SetupPage Page
    {
        get => _page;
        private set
        {
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsWelcome));
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsWelcome => Page == SetupPage.Welcome;
    public bool IsWorking => Page == SetupPage.Working;
    public bool IsDone => Page == SetupPage.Done;
    public bool IsFailed => Page == SetupPage.Failed;

    public bool DesktopShortcut { get => _desktopShortcut; set => Set(ref _desktopShortcut, value); }
    public bool LaunchAfter { get => _launchAfter; set => Set(ref _launchAfter, value); }
    public string? ElevationError { get => _elevationError; private set => Set(ref _elevationError, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string FailText { get => _failText; private set => Set(ref _failText, value); }
    public string? RollbackText { get => _rollbackText; private set => Set(ref _rollbackText, value); }
    public string? LaunchError { get => _launchError; private set => Set(ref _launchError, value); }

    private int VersionCompare => Installed?.Version is { } v && System.Version.TryParse(v, out var iv) && System.Version.TryParse(Version, out var cv)
        ? cv.CompareTo(iv)
        : 1;

    public string ActionText => Installed is null ? L.T("Yükle", "Install") : VersionCompare switch { > 0 => L.T("Güncelle", "Update"), 0 => L.T("Yeniden Yükle", "Reinstall"), _ => L.T("Bu Sürümü Yükle", "Install This Version") };

    /// <summary>Karşılama ekranındaki kurulum durumu (Uninstall kaydından okunan gerçek sürüm).</summary>
    public string InstallStateText => Installed switch
    {
        null => L.T($"Kurulum yeri: {InstallDir}", $"Install location: {InstallDir}"),
        { Version: null } => L.T($"Önceki kurulumdan kalan dosya bulundu; yeniden yüklenecek ({InstallDir}).", $"Files left from a previous installation were found; it will be reinstalled ({InstallDir})."),
        _ => VersionCompare switch
        {
            > 0 => L.T($"Yüklü sürüm {Installed.Version} → {Version} sürümüne güncellenecek. Ayarlarınız ve geçmişiniz korunur.", $"The installed version {Installed.Version} will be updated to version {Version}. Your settings and history are kept."),
            0 => L.T($"Bu sürüm ({Version}) zaten yüklü; yeniden yüklenecek (onarım). Ayarlarınız ve geçmişiniz korunur.", $"This version ({Version}) is already installed; it will be reinstalled (repair). Your settings and history are kept."),
            _ => L.T($"Daha yeni bir sürüm yüklü ({Installed.Version}); {Version} sürümüne geri dönülecek.", $"A newer version is installed ({Installed.Version}); it will be rolled back to version {Version}.")
        }
    };

    /// <summary>Yeni kurulumda teşekkür + hoş geldin; güncellemede (önceki sürüm vardı) güncelleme metni.</summary>
    public bool IsUpdate => Installed?.Version is { } old && old != Version;
    public string DoneTitle => IsUpdate ? L.T("Güncelleme tamamlandı!", "Update completed!") : L.T("Bizi tercih ettiğiniz için teşekkürler!", "Thank you for choosing us!");
    public string DoneSubtitle => IsUpdate ? L.T($"E-mre Control Center {Version} sürümü hazır.", $"E-mre Control Center version {Version} is ready.") : L.T("Aramıza hoş geldin.", "Welcome aboard.");

    public string DoneDetail => Installed?.Version is { } old && old != Version
        ? L.T($"{old} → {Version} sürümüne güncellendi · {InstallDir}", $"Updated from {old} to version {Version} · {InstallDir}")
        : L.T($"Sürüm {Version} kuruldu · {InstallDir}", $"Version {Version} installed · {InstallDir}");

    public string DoneShortcutText => DesktopShortcut
        ? L.T("Başlat menüsünde ve masaüstünde \"E-mre Control Center\" kısayolu var.", "There is an \"E-mre Control Center\" shortcut in the Start menu and on the desktop.")
        : L.T("Başlat menüsünde \"E-mre Control Center\" kısayolu var.", "There is an \"E-mre Control Center\" shortcut in the Start menu.");

    /// <summary>Karşılama ekranı "Yükle": yönetici değilse UAC ile yeniden başlatır, yöneticiyse kurulumu başlatır.</summary>
    private async Task InstallClickedAsync()
    {
        ElevationError = null;
        if (_isElevated)
        {
            await StartInstallAsync();
            return;
        }
        var args = new[] { LaunchModes.ArgInstall, DesktopShortcut ? LaunchModes.ArgDesktop : LaunchModes.ArgNoDesktop };
        var (outcome, error) = _relaunchElevated(args);
        _log.Info(L.T($"Yönetici olarak yeniden başlatma: {outcome}{(error is null ? "" : " – " + error)}", $"Restart as administrator: {outcome}{(error is null ? "" : " – " + error)}"));
        switch (outcome)
        {
            case ElevationOutcome.Started:
                RequestClose?.Invoke();
                break;
            case ElevationOutcome.Declined:
                ElevationError = L.T("Yönetici izni verilmedi; hiçbir değişiklik yapılmadı. Program Files klasörüne kurulum için izin gerekir.", "Administrator permission was not granted; no changes were made. Installing to the Program Files folder requires permission.");
                break;
            default:
                ElevationError = error ?? L.T("Kurulum yönetici olarak başlatılamadı.", "Setup could not be started as administrator.");
                break;
        }
    }

    /// <summary>Kurulum adımları (yönetici örneğinde). Beklenmeyen hata ekranda "Kurulum tamamlanamadı" olarak gösterilir.</summary>
    public async Task StartInstallAsync()
    {
        try
        {
            await RunInstallAsync();
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    /// <summary>Çalışan uygulama varsa önce kullanıcıya sorulur; zorla kapatılmaz.</summary>
    private async Task RunInstallAsync()
    {
        foreach (var s in Steps)
        {
            s.State = SetupStepState.Pending;
            s.Detail = string.Empty;
        }
        Progress = 0;
        RollbackText = null;
        Page = SetupPage.Working;
        StatusText = L.T("Kuruluyor…", "Installing…");

        var running = _installer.FindRunning();
        if (running.Count > 0)
        {
            try
            {
                var ok = await RunningPrompt.AskAsync(
                    L.T("E-mre Control Center şu anda açık. Kurulumun devam etmesi için uygulamanın kapatılması gerekiyor. ", "E-mre Control Center is currently open. The app must be closed for setup to continue. ") +
                    L.T("Uygulamada bir işlem sürüyorsa önce onun bitmesini bekleyin.", "If an operation is running in the app, wait for it to finish first."));
                if (!ok)
                {
                    _log.Info(L.T("Kurulum iptal edildi: kullanıcı açık uygulamanın kapatılmasını istemedi.", "Setup cancelled: the user did not want the open app to be closed."));
                    Page = SetupPage.Welcome;
                    return;
                }
                StatusText = L.T("Uygulama kapatılıyor…", "Closing the app…");
                if (!await _installer.CloseRunningAsync(running, TimeSpan.FromSeconds(10)))
                {
                    Fail(L.T("E-mre Control Center kapanmadı (uygulamada bir işlem sürüyor veya kapatma onayı bekliyor olabilir). ", "E-mre Control Center did not close (an operation may be running in the app or it may be waiting for close confirmation). ") +
                         L.T("Uygulamayı kendiniz kapatıp \"Tekrar dene\"ye basın. Hiçbir değişiklik yapılmadı.", "Close the app yourself and press \"Try again\". No changes were made."));
                    return;
                }
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }
        }

        var progress = new Progress<InstallProgress>(OnProgress);
        var report = await _installer.InstallAsync(_sourceExe, DesktopShortcut, progress);
        Apply(report);
        if (report.Success)
        {
            Progress = 1;
            Page = SetupPage.Done;
            OnPropertyChanged(nameof(DoneShortcutText));
            // Uygulama içi güncelleme: yeni sürüm kendiliğinden açılır (başlatılamazsa neden bu sayfada yazar).
            if (_autoFinish) Finish();
        }
        else
        {
            var failure = report.FirstFailure;
            Fail(failure is null ? L.T("Kurulum tamamlanamadı.", "Setup could not be completed.") : $"{failure.Title}: {failure.Detail}");
            RollbackText = report.Rollback;
        }
    }

    private void OnProgress(InstallProgress p)
    {
        var index = Array.IndexOf(StepKeys, p.Step);
        if (index < 0) return;
        for (var i = 0; i < index; i++)
            if (Steps[i].State == SetupStepState.Running) Steps[i].State = SetupStepState.Done;
        var step = Steps[index];
        step.State = SetupStepState.Running;
        if (p.Detail is not null) step.Detail = p.Detail;
        // İlerleme = tamamlanan adımlar + (varsa) kopyalamadaki gerçek bayt oranı.
        Progress = (index + (p.Fraction ?? 0)) / StepKeys.Length;
        StatusText = step.Title + "…";
    }

    private void Apply(InstallReport report)
    {
        foreach (var r in report.Steps)
        {
            var step = Steps.FirstOrDefault(s => s.Key == r.Key);
            if (step is null) continue;
            step.State = r.Success ? SetupStepState.Done : SetupStepState.Failed;
            step.Detail = r.Detail;
        }
        foreach (var s in Steps.Where(s => s.State == SetupStepState.Running)) s.State = SetupStepState.Pending;
    }

    private void Fail(string text)
    {
        FailText = text;
        StatusText = L.T("Kurulum tamamlanamadı", "Setup could not be completed");
        Page = SetupPage.Failed;
        _log.Info(L.T("Kurulum başarısız: ", "Setup failed: ") + text);
    }

    private void Finish()
    {
        LaunchError = null;
        if (LaunchAfter)
        {
            var error = _launch(_installer.Layout.ExePath);
            if (error is not null)
            {
                LaunchError = L.T("Uygulama başlatılamadı: ", "The app could not be started: ") + error + L.T(" Başlat menüsünden açabilirsiniz.", " You can open it from the Start menu.");
                LaunchAfter = false;
                return;
            }
        }
        RequestClose?.Invoke();
    }

    /// <summary>
    /// Kurulan uygulamanın açılış argümanı: uygulama içi güncellemeden sonra yeni sürüm doğrudan Kontrol Merkezi'nde açılır
    /// (gereksinim ekranı yeniden sorulmaz; Windows 11 denetimi yine yapılır). Yeni kurulumda ilk açılış gereksinim ekranıyla olur.
    /// </summary>
    internal string LaunchArguments => (!_autoFinish
        ? string.Empty
        : Installed?.Version is { } old && System.Version.TryParse(old, out var previous)
            // Önceki sürüm (Uninstall kaydından) yeni sürüme iletilir: açılışta "Güncelleme tamamlandı: vX → vY" gösterilir.
            ? $"{AdminPrivilegeManager.ArgAccepted} {LaunchModes.ArgUpdatedFrom} {previous.ToString(3)} "
            : AdminPrivilegeManager.ArgAccepted + " ")
        // v2.0.0: kurulumda seçilen dil ve tema; uygulama ilk açılıştaki "Dil ve görünüm" ekranında bunları önerir.
        + string.Join(' ', LaunchModes.PreferenceArgs());

    private string? LaunchInstalled(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, LaunchArguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            _log.Info(L.T($"Kurulan uygulama başlatıldı: {exe} {LaunchArguments}", $"Installed app started: {exe} {LaunchArguments}").TrimEnd());
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log.Info(L.T("Kurulan uygulama başlatılamadı: ", "The installed app could not be started: ") + ex.Message);
            return ex.Message;
        }
    }

    private void OnError(Exception ex)
    {
        _log.Info(L.T($"Beklenmeyen kurulum hatası: {ex}", $"Unexpected setup error: {ex}"));
        Fail(L.T("Beklenmeyen hata: ", "Unexpected error: ") + ex.Message);
    }
}

public sealed class FeedbackReasonViewModel(string text) : ObservableObject
{
    private bool _isChecked;
    public string Text => text;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public enum UninstallPage { Form, Working, Done, NotInstalled }

/// <summary>
/// Kaldırma ekranı (Windows Ayarlar → Uygulamalar → Kaldır): "Bir dahaki sefere görüşmek üzere" + isteğe bağlı geri bildirim
/// (nedenler + mesaj → Google Formu) + "Gönder ve Kaldır" / "İptal". Geri bildirim gönderilemezse kaldırma yine yapılır ve sonuç
/// ekranda gerçek nedeniyle yazar (Tekrar gönder). Kullanıcı verileri yalnızca kutu işaretlenirse silinir.
/// </summary>
public sealed class UninstallViewModel : ObservableObject
{
    public const int MaxMessageLength = 500;

    private readonly InstallerService _installer;
    private readonly FeedbackService? _feedback;
    private readonly SetupLog _log;
    private readonly string? _currentExe;

    private UninstallPage _page;
    private string _message = string.Empty;
    private bool _deleteUserData;
    private string _statusText = string.Empty;
    private string _resultTitle = string.Empty;
    private string? _feedbackResult;
    private bool _feedbackFailed;
    private string? _notesText;
    private List<string> _sentReasons = [];
    private string _sentMessage = string.Empty;

    public UninstallViewModel(InstallerService installer, FeedbackService? feedback, SetupLog log, string? currentExe)
    {
        _installer = installer;
        _feedback = feedback;
        _log = log;
        _currentExe = currentExe;
        Reasons = new ObservableCollection<FeedbackReasonViewModel>(
            new[] { L.T("Sevmedim", "I didn't like it"), L.T("Kasıyor / yavaş çalışıyor", "It lags / runs slowly"), L.T("Hata veriyor", "It shows errors"), L.T("Artık ihtiyacım yok", "I don't need it anymore"), L.T("Diğer", "Other") }
                .Select(t => new FeedbackReasonViewModel(t)));
        foreach (var r in Reasons) r.PropertyChanged += (_, _) => OnFeedbackChanged();
        Installed = installer.ReadInstalled();
        _page = Installed is null ? UninstallPage.NotInstalled : UninstallPage.Form;

        UninstallCommand = new AsyncCommand(UninstallAsync, () => Page == UninstallPage.Form, OnError);
        RetryFeedbackCommand = new AsyncCommand(RetryFeedbackAsync, () => FeedbackFailed);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(), () => !IsWorking);
    }

    public event Action? RequestClose;

    public ObservableCollection<FeedbackReasonViewModel> Reasons { get; }
    public ObservableCollection<SetupStepViewModel> Steps { get; } = [];
    public RunningAppPrompt RunningPrompt { get; } = new();
    public InstalledInfo? Installed { get; }
    public string InstallDir => _installer.Layout.InstallDir;
    public string UserDataText => string.Join(", ", _installer.Layout.UserDataDirs.Where(System.IO.Directory.Exists).DefaultIfEmpty(_installer.Layout.UserDataDirs[0]));
    public string LogPath => _log.FilePath;

    public ICommand UninstallCommand { get; }
    public ICommand RetryFeedbackCommand { get; }
    public ICommand CloseCommand { get; }

    public UninstallPage Page
    {
        get => _page;
        private set
        {
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsForm));
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsNotInstalled));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsForm => Page == UninstallPage.Form;
    public bool IsWorking => Page == UninstallPage.Working;
    public bool IsDone => Page == UninstallPage.Done;
    public bool IsNotInstalled => Page == UninstallPage.NotInstalled;

    /// <summary>Geri bildirim gönderimi yapılandırılmış mı (Google Formu). Değilse nedenler / mesaj alanı gösterilmez.</summary>
    public bool FeedbackEnabled => _feedback is not null;

    public string Message
    {
        get => _message;
        set
        {
            var text = value ?? string.Empty;
            if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];
            if (!Set(ref _message, text)) return;
            OnPropertyChanged(nameof(MessageCounter));
            OnFeedbackChanged();
        }
    }

    public string MessageCounter => $"{Message.Length} / {MaxMessageLength}";
    public bool DeleteUserData { get => _deleteUserData; set => Set(ref _deleteUserData, value); }
    public bool HasFeedback => Reasons.Any(r => r.IsChecked) || Message.Trim().Length > 0;
    public string PrimaryText => FeedbackEnabled && HasFeedback ? L.T("Gönder ve Kaldır", "Send and Uninstall") : L.T("Kaldır", "Uninstall");
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ResultTitle { get => _resultTitle; private set => Set(ref _resultTitle, value); }
    public string? FeedbackResult { get => _feedbackResult; private set => Set(ref _feedbackResult, value); }
    public bool FeedbackFailed { get => _feedbackFailed; private set => Set(ref _feedbackFailed, value); }
    public string? NotesText { get => _notesText; private set => Set(ref _notesText, value); }

    private void OnFeedbackChanged()
    {
        OnPropertyChanged(nameof(HasFeedback));
        OnPropertyChanged(nameof(PrimaryText));
    }

    private async Task UninstallAsync()
    {
        var sendFeedback = FeedbackEnabled && HasFeedback;
        Steps.Clear();
        if (sendFeedback) Steps.Add(new SetupStepViewModel(InstallSteps.Feedback));
        foreach (var key in new[] { InstallSteps.Shortcuts, InstallSteps.Files, InstallSteps.Unregister, InstallSteps.Cache })
            Steps.Add(new SetupStepViewModel(key));
        if (DeleteUserData) Steps.Add(new SetupStepViewModel(InstallSteps.UserData));
        Page = UninstallPage.Working;
        StatusText = L.T("Kaldırılıyor…", "Uninstalling…");
        _log.Info(L.T($"Kaldırma onaylandı: geri bildirim {(sendFeedback ? "gönderilecek" : "yok")}, kullanıcı verileri {(DeleteUserData ? "silinecek" : "korunacak")}.", $"Uninstall confirmed: feedback {(sendFeedback ? "will be sent" : "none")}, user data will be {(DeleteUserData ? "deleted" : "kept")}."));

        // 1) Açık uygulama: önce sorulur (iptal edilirse geri bildirim de gönderilmemiş olur).
        var running = _installer.FindRunning();
        if (running.Count > 0)
        {
            try
            {
                var ok = await RunningPrompt.AskAsync(
                    L.T("E-mre Control Center şu anda açık. Kaldırmak için uygulamanın kapatılması gerekiyor. ", "E-mre Control Center is currently open. The app must be closed to uninstall it. ") +
                    L.T("Uygulamada bir işlem sürüyorsa önce onun bitmesini bekleyin.", "If an operation is running in the app, wait for it to finish first."));
                if (!ok)
                {
                    _log.Info(L.T("Kaldırma iptal edildi: kullanıcı açık uygulamanın kapatılmasını istemedi.", "Uninstall cancelled: the user did not want the open app to be closed."));
                    Page = UninstallPage.Form;
                    return;
                }
                StatusText = L.T("Uygulama kapatılıyor…", "Closing the app…");
                if (!await _installer.CloseRunningAsync(running, TimeSpan.FromSeconds(10)))
                {
                    Steps.Clear();
                    ResultTitle = L.T("Kaldırma yapılmadı", "Uninstall not performed");
                    NotesText = L.T("E-mre Control Center kapanmadı (uygulamada bir işlem sürüyor veya kapatma onayı bekliyor olabilir). ", "E-mre Control Center did not close (an operation may be running in the app or it may be waiting for close confirmation). ") +
                                L.T("Uygulamayı kendiniz kapatıp kaldırmayı yeniden başlatın. Hiçbir şey silinmedi.", "Close the app yourself and start the uninstall again. Nothing was deleted.");
                    Page = UninstallPage.Done;
                    return;
                }
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }
        }

        // 2) Geri bildirim (gönderilemezse kaldırma yine yapılır; sonuç ekranda yazar).
        if (sendFeedback)
        {
            _sentReasons = Reasons.Where(r => r.IsChecked).Select(r => r.Text).ToList();
            _sentMessage = Message;
            var step = Steps[0];
            step.State = SetupStepState.Running;
            StatusText = L.T("Geri bildirim gönderiliyor…", "Sending feedback…");
            var result = await _feedback!.SendAsync(_sentReasons, _sentMessage);
            step.State = result.Success ? SetupStepState.Done : SetupStepState.Failed;
            step.Detail = result.Message;
            FeedbackResult = result.Message;
            FeedbackFailed = !result.Success;
        }

        // 3) Kaldırma
        var report = await _installer.UninstallAsync(DeleteUserData, _currentExe, new Progress<InstallProgress>(p =>
        {
            foreach (var s in Steps.Where(s => s.State == SetupStepState.Running && s.Key != InstallSteps.Feedback)) s.State = SetupStepState.Done;
            var step = Steps.FirstOrDefault(s => s.Key == p.Step);
            if (step is null) return;
            step.State = SetupStepState.Running;
            StatusText = step.Title + "…";
        }));
        foreach (var r in report.Steps)
        {
            var step = Steps.FirstOrDefault(s => s.Key == r.Key);
            if (step is null) continue;
            step.State = r.Success ? SetupStepState.Done : SetupStepState.Failed;
            step.Detail = r.Detail;
        }

        var removed = report.Steps.Any(s => s.Key == InstallSteps.Files && s.Success);
        ResultTitle = report.Success ? L.T("E-mre Control Center kaldırıldı", "E-mre Control Center has been uninstalled")
            : removed ? L.T("E-mre Control Center kaldırıldı (bazı öğeler silinemedi)", "E-mre Control Center has been uninstalled (some items could not be deleted)")
            : L.T("Kaldırma tamamlanamadı", "Uninstall could not be completed");
        var notes = new List<string>();
        if (report.PendingReboot.Count > 0)
            notes.Add(L.T($"Kullanımdaki {report.PendingReboot.Count} öğe bilgisayar yeniden başlatılınca Windows tarafından silinecek.", $"{report.PendingReboot.Count} item(s) in use will be deleted by Windows when the computer restarts."));
        if (report.Kept.Count > 0)
            notes.Add(L.T("Bu uygulamaya ait olmadığı için bırakılanlar: ", "Kept because they do not belong to this app: ") + string.Join(", ", report.Kept) + ".");
        if (report.Failed.Count > 0)
            notes.Add(L.T("Silinemeyenler: ", "Could not be deleted: ") + string.Join("; ", report.Failed) + ".");
        if (!DeleteUserData)
            notes.Add(L.T($"Ayarlarınız, geçmişiniz ve günlükleriniz korundu ({_installer.Layout.UserDataDirs[0]}).", $"Your settings, history and logs have been kept ({_installer.Layout.UserDataDirs[0]})."));
        NotesText = string.Join("\n", notes);
        StatusText = ResultTitle;
        Page = UninstallPage.Done;
    }

    private async Task RetryFeedbackAsync()
    {
        if (_feedback is null) return;
        FeedbackResult = L.T("Geri bildirim yeniden gönderiliyor…", "Sending feedback again…");
        var result = await _feedback.SendAsync(_sentReasons, _sentMessage);
        FeedbackResult = result.Message;
        FeedbackFailed = !result.Success;
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnError(Exception ex)
    {
        _log.Info(L.T($"Beklenmeyen kaldırma hatası: {ex}", $"Unexpected uninstall error: {ex}"));
        ResultTitle = L.T("Kaldırma tamamlanamadı", "Uninstall could not be completed");
        NotesText = L.T("Beklenmeyen hata: ", "Unexpected error: ") + ex.Message;
        Page = UninstallPage.Done;
    }
}

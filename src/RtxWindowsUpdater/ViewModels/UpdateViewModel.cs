using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

public enum UpdateStage { Idle, Checking, UpToDate, CheckFailed, Available, Downloading, Launching, Failed }

/// <summary>
/// Zorunlu uygulama içi güncelleme (KULLANICI KARARI 2026-09-25): yeni sürüm yayımlandıysa ana ekranın önüne "Yeni sürüm yayınlandı"
/// penceresi gelir; seçenek Güncelle (veya uygulamayı kapatmak). Güncelle → kurulum dosyası indirilir ve doğrulanır (boyut, SHA-256,
/// ürün adı, sürüm) → kurulum başlatılır, uygulama kapanır; kurulum bitince yeni sürüm açılır. Denetlenemezse (internet yok, GitHub
/// yanıt vermedi) uygulama normal açılır ve nedeni Hakkında'da yazar; denetlenemeyen güncelleme varmış gibi gösterilmez.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    /// <summary>Test düzenekleri yansımayla kapatır (gerçek GitHub'a istek gitmesin). Kullanıcının değiştirebileceği bir ayar değildir.</summary>
    internal static bool AutoCheckEnabled = true;

    private readonly Logger _logger;
    private UpdateService _service;
    private Func<string, (bool Started, string? Error)> _launchSetup;
    private Action _shutdown;

    private UpdateStage _stage;
    private UpdateInfo? _available;
    private string _statusText = string.Empty;
    private double _progress;
    private string _progressText = string.Empty;
    private string? _errorText;
    private string? _checkMessage;
    private DateTime? _checkedAt;

    public UpdateViewModel(Logger logger)
    {
        _logger = logger;
        _service = new UpdateService(UpdateService.DefaultLatestReleaseUrl, logger.Info);
        _launchSetup = LaunchSetup;
        _shutdown = () => Application.Current?.Shutdown();
        UpdateCommand = new AsyncCommand(UpdateAsync, () => CanUpdate, ex => Fail("Beklenmeyen hata: " + ex.Message));
        CloseAppCommand = new RelayCommand(() => _shutdown(), () => !IsWorking);
        CheckCommand = new AsyncCommand(CheckAsync, () => !IsWorking && Stage != UpdateStage.Checking, ex => _logger.Warning("Güncelleme denetimi hatası: " + ex.Message));
    }

    public ICommand UpdateCommand { get; }
    public ICommand CloseAppCommand { get; }
    public ICommand CheckCommand { get; }

    public UpdateStage Stage
    {
        get => _stage;
        private set
        {
            if (!Set(ref _stage, value)) return;
            foreach (var name in new[] { nameof(IsRequired), nameof(IsWorking), nameof(CanUpdate), nameof(IsFailed), nameof(AboutText), nameof(ButtonText) })
                OnPropertyChanged(name);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public UpdateInfo? Available
    {
        get => _available;
        private set
        {
            if (!Set(ref _available, value)) return;
            foreach (var name in new[] { nameof(NewVersionText), nameof(NotesText), nameof(HasNotes), nameof(DetailText) })
                OnPropertyChanged(name);
        }
    }

    /// <summary>Yeni sürüm var: pencere gösterilir (indirme / başlatma / hata aşamalarında da açık kalır).</summary>
    public bool IsRequired => Available is not null && Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Launching or UpdateStage.Failed;
    public bool IsWorking => Stage is UpdateStage.Downloading or UpdateStage.Launching;
    public bool IsFailed => Stage == UpdateStage.Failed;
    public bool CanUpdate => Available is not null && !IsWorking;

    public string CurrentVersionText => "v" + AppInfo.Version;
    public string NewVersionText => Available is null ? string.Empty : "v" + Available.Version.ToString(3);
    public string NotesText => Available?.Notes ?? string.Empty;
    public bool HasNotes => NotesText.Length > 0;
    public string ButtonText => IsFailed ? "Tekrar dene" : "Güncelle";

    public string DetailText => Available is null
        ? string.Empty
        : $"Yüklü sürüm {CurrentVersionText} · yeni sürüm {NewVersionText}" +
          (Available.PublishedAt is { } at ? $" · yayın {at.ToLocalTime():dd.MM.yyyy HH:mm}" : "") +
          $" · {Available.SetupSize / 1048576.0:0.0} MB";

    /// <summary>Taşınabilir kopyada çalışıyorsa: güncelleme uygulamayı Program Files'a kurar (bu kopya eski kalır).</summary>
    public string? PortableNote => string.Equals(Environment.ProcessPath, InstallLayout.Machine.ExePath, StringComparison.OrdinalIgnoreCase)
        ? null
        : $"Bu taşınabilir bir kopya: güncelleme uygulamayı {InstallLayout.Machine.InstallDir} klasörüne kurar; bundan sonra Başlat menüsünden açın.";

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string? ErrorText { get => _errorText; private set => Set(ref _errorText, value); }

    /// <summary>Hakkında → Güncelleme satırı (gerçek son denetim sonucu).</summary>
    public string AboutText => Stage switch
    {
        UpdateStage.Idle => "Henüz denetlenmedi",
        UpdateStage.Checking => "Denetleniyor…",
        UpdateStage.UpToDate => $"{_checkMessage} · son denetim {_checkedAt:HH:mm}",
        UpdateStage.CheckFailed => $"Denetlenemedi ({_checkedAt:HH:mm}): {_checkMessage}",
        _ => $"Yeni sürüm yayımlandı: {NewVersionText}"
    };

    /// <summary>Açılışta arka planda bir kez çağrılır (gereksinimler geçtikten sonra).</summary>
    public async Task CheckAsync()
    {
        if (IsWorking || Stage == UpdateStage.Checking) return;
        Stage = UpdateStage.Checking;
        _ = Task.Run(CleanupDownloads);
        var result = await _service.CheckAsync(new Version(AppInfo.Version));
        _checkedAt = DateTime.Now;
        _checkMessage = result.Message;
        if (!result.Success)
        {
            _logger.Warning("Güncelleme denetlenemedi: " + result.Message);
            Available = null;
            Stage = UpdateStage.CheckFailed;
            return;
        }
        if (result.Update is null)
        {
            Available = null;
            Stage = UpdateStage.UpToDate;
            return;
        }
        Available = result.Update;
        StatusText = "Yeni sürüm yayınlandı";
        _logger.Info($"Yeni sürüm yayımlandı: {result.Update.Tag} (yüklü v{AppInfo.Version}). Güncelleme penceresi gösteriliyor.");
        Stage = UpdateStage.Available;
    }

    private async Task UpdateAsync()
    {
        var info = Available!;
        ErrorText = null;
        Progress = 0;
        ProgressText = string.Empty;
        StatusText = "İndiriliyor…";
        Stage = UpdateStage.Downloading;
        _logger.Info($"Güncelleme başlatıldı: v{AppInfo.Version} → {info.Tag}.");

        // Yönetici olarak çalışırken yalnızca Yöneticiler + SYSTEM erişimli klasöre indirilir (doğrulandıktan sonra değiştirilemesin).
        var elevated = AdminPrivilegeManager.IsElevated;
        var folder = elevated
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), $"{UpdateService.DownloadFolderName} {Guid.NewGuid():N}")
            : Path.Combine(Path.GetTempPath(), UpdateService.DownloadFolderName, Guid.NewGuid().ToString("N"));
        string setup;
        try
        {
            var last = -1.0;
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                var fraction = (double)p.Done / p.Total;
                if (fraction - last < 0.005 && p.Done != p.Total) return;
                last = fraction;
                Progress = fraction;
                ProgressText = $"{p.Done / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB";
            });
            setup = await _service.DownloadAsync(info, folder, elevated, progress);
        }
        catch (TaskCanceledException)
        {
            Fail("İndirme zaman aşımına uğradı; internet bağlantınızı kontrol edip yeniden deneyin.");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Fail("Güncelleme indirilemedi: " + ex.Message);
            return;
        }

        StatusText = "Doğrulandı (SHA-256, sürüm). Kurulum başlatılıyor…";
        Stage = UpdateStage.Launching;
        var (started, error) = _launchSetup(setup);
        if (!started)
        {
            Fail(error ?? "Kurulum başlatılamadı.");
            return;
        }
        _logger.Info($"Güncelleme kurulumu başlatıldı ({info.Tag}); uygulama kapanıyor, kurulum bitince yeni sürüm açılacak.");
        _shutdown();
    }

    private void Fail(string message)
    {
        ErrorText = message;
        StatusText = "Güncelleme yapılamadı";
        _logger.Warning("Güncelleme yapılamadı: " + message);
        Stage = UpdateStage.Failed;
    }

    private void CleanupDownloads()
    {
        // %TEMP%\E-mre Control Center Güncelleme (yönetici olmadan indirilenler) ve C:\ProgramData\E-mre Control Center Güncelleme <guid>
        var roots = new List<string> { Path.GetTempPath() };
        if (AdminPrivilegeManager.IsElevated) roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        try
        {
            _service.CleanupDownloads(roots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Eski güncelleme dosyaları temizlenemedi: " + ex.Message);
        }
    }

    /// <summary>
    /// Kurulumu güncelleme kipinde başlatır (bu işlemin kapanmasını bekler, bitince yeni sürümü açar). Uygulama yönetici olarak
    /// çalışıyorsa yetki devralınır; değilse Windows UAC sorar (atlatılmaz, reddedilirse güncelleme yapılmaz).
    /// </summary>
    private static (bool Started, string? Error) LaunchSetup(string setup)
    {
        var args = $"{LaunchModes.ArgInstall} {LaunchModes.ArgUpdate} {LaunchModes.ArgWaitPid} {Environment.ProcessId}";
        var psi = new ProcessStartInfo(setup, args) { WorkingDirectory = Path.GetDirectoryName(setup)! };
        if (AdminPrivilegeManager.IsElevated)
        {
            psi.UseShellExecute = false;
        }
        else
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }
        try
        {
            Process.Start(psi)?.Dispose();
            return (true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Güncelleme için yönetici izni verilmedi; güncelleme yapılmadı.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (false, "Kurulum başlatılamadı: " + ex.Message);
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Threading;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services;
using RtxWindowsUpdater.ViewModels;
using RtxWindowsUpdater.Views;

namespace RtxWindowsUpdater;

public partial class App : Application
{
    // İç kimlik bilinçli olarak ilk adla aynı kaldı: eski sürümler (RTX Windows Updater, E-mre Hub) ile aynı anda çalışamaz.
    private const string SingleInstanceMutexName = @"Local\RTXWindowsUpdater.SingleInstance";

    // Kurulum ve kaldırma ekranı bir kez açılır (uygulamanın kilidinden ayrı: kurulum açıkken çalışan uygulama kapatılabilsin).
    private const string SetupMutexName = @"Local\EmreControlCenter.Setup";

    private Logger? _logger;
    private MainViewModel? _viewModel;
    private TrayController? _tray;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mode = LaunchModes.Detect(e.Args, Environment.ProcessPath);
        if (mode != LaunchMode.App)
        {
            StartSetup(mode, e.Args);
            return;
        }

        // Aynı anda iki örneğin güncelleme yapmasını engelle. Yönetici olarak yeniden başlatılan örnek,
        // önceki örneğin kapanmasını birkaç saniye bekler.
        var relaunched = e.Args.Contains(AdminPrivilegeManager.ArgElevated, StringComparer.OrdinalIgnoreCase);
        if (!TryAcquireSingleInstance(SingleInstanceMutexName, relaunched ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
        {
            // Uygulama zaten çalışıyor (penceresi bildirim alanına gizlenmiş olabilir): o örneğin penceresi öne getirilir.
            if (!relaunched && AppSignals.Post(AppSignals.ShowMessage))
            {
                Shutdown();
                return;
            }
            MessageBox.Show(AppInfo.Name + " zaten çalışıyor.", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _logger = new Logger();

        // Hiçbir beklenmeyen hata uygulamayı sessizce kapatmasın; kaydedilsin ve kullanıcıya gösterilsin.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.Error("Arka plan görevinde beklenmeyen hata: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.Error("Kritik hata: " + (args.ExceptionObject as Exception)?.Message);

        var accepted = e.Args.Contains(AdminPrivilegeManager.ArgAccepted, StringComparer.OrdinalIgnoreCase);
        var startCheck = e.Args.Contains(AdminPrivilegeManager.ArgStartCheck, StringComparer.OrdinalIgnoreCase);

        var state = new AppStateStore(_logger);
        var notifications = new NotificationService(_logger);
        notifications.Register(ExtractNotificationIcon());

        _viewModel = new MainViewModel(_logger, accepted, startCheck, state, notifications);
        var updatedFrom = Array.FindIndex(e.Args, a => a.Equals(LaunchModes.ArgUpdatedFrom, StringComparison.OrdinalIgnoreCase));
        if (updatedFrom >= 0 && updatedFrom + 1 < e.Args.Length) _viewModel.UpdatedFrom = e.Args[updatedFrom + 1];
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        // Bildirim alanı: pencere kapatılınca uygulama arka planda sürer; simge çift tıklamayla açılır, sağ tıkla kısayollar.
        _tray = new TrayController(window, _viewModel, _logger);
        window.Tray = _tray;
        SessionEnding += (_, _) => AppLifetime.MarkExiting(); // Windows kapanırken pencere gizlenmez, uygulama kapanır
        window.Show();
    }

    /// <summary>
    /// Kurulum (dosya adında "Setup" / <see cref="LaunchModes.ArgInstall"/>) veya kaldırma (<see cref="LaunchModes.ArgUninstall"/>) ekranı.
    /// Normal uygulama başlatılmaz, uygulamanın günlük klasörüne yazılmaz (günlük: %TEMP%\E-mre Control Center Kurulum.log).
    /// </summary>
    private void StartSetup(LaunchMode mode, string[] args)
    {
        var relaunched = args.Contains(AdminPrivilegeManager.ArgElevated, StringComparer.OrdinalIgnoreCase);
        if (!TryAcquireSingleInstance(SetupMutexName, relaunched ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
        {
            MessageBox.Show("Kurulum veya kaldırma penceresi zaten açık.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Uygulamanın dosya yolu belirlenemedi.");
        var log = new SetupLog();
        log.Info($"=== {mode} · {AppInfo.Name} {AppInfo.Version} · yönetici: {(AdminPrivilegeManager.IsElevated ? "evet" : "hayır")} · {exe}");
        DispatcherUnhandledException += (_, ev) =>
        {
            log.Info("Beklenmeyen arayüz hatası: " + ev.Exception);
            MessageBox.Show("Beklenmeyen bir hata oluştu:\n\n" + ev.Exception.Message + "\n\nAyrıntılar: " + log.FilePath,
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            ev.Handled = true;
        };
        var installer = new InstallerService(InstallLayout.Machine, log.Info);

        if (mode == LaunchMode.Uninstall)
        {
            // Çalışma klasörü program klasörü olursa Windows o klasörü silemez.
            Environment.CurrentDirectory = Path.GetTempPath();

            // Program Files ve HKLM kaydı yönetici yetkisiyle silinir: Windows kaldırma komutunu normal yetkiyle çalıştırdıysa UAC ile
            // yeniden başlatılır (atlatılmaz); izin verilmezse hiçbir şey değişmez.
            if (!AdminPrivilegeManager.IsElevated)
            {
                var (outcome, error) = AdminPrivilegeManager.RelaunchElevated(LaunchModes.ArgUninstall);
                log.Info($"Kaldırma için yönetici olarak yeniden başlatma: {outcome}{(error is null ? "" : " – " + error)}");
                if (outcome != ElevationOutcome.Started)
                {
                    MessageBox.Show(outcome == ElevationOutcome.Declined
                            ? "Kaldırma için yönetici izni gerekiyor. İzin verilmediği için hiçbir şey değiştirilmedi."
                            : error ?? "Kaldırma yönetici olarak başlatılamadı.",
                        AppInfo.Name + " Kaldırma", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Shutdown();
                return;
            }

            // Kurulu EXE'den açıldıysa kaldırma korumalı geçici kopyadan yapılır: kurulu EXE kullanımda kalmaz ve hemen silinebilir.
            if (installer.IsInstalledExe(exe))
            {
                try
                {
                    installer.StartStagedCopy(installer.PrepareStagedCopy(exe)).Dispose();
                    Shutdown();
                    return;
                }
                catch (Exception ex)
                {
                    log.Info("Geçici kopya hazırlanamadı; kaldırma kurulu EXE'den yapılacak (EXE yeniden başlatmada silinir): " + ex.Message);
                }
            }

            // Geçici kopya: kendisini başlatan işlemin (kurulu EXE) kapanmasını bekler.
            WaitForLauncher(args, log);

            var form = FeedbackForm.Configured;
            var uninstall = new UninstallViewModel(installer, form is null ? null : new FeedbackService(form, log.Info), log, exe);
            var uninstallWindow = new UninstallWindow(uninstall);
            MainWindow = uninstallWindow;
            uninstallWindow.Show();
            return;
        }

        bool? desktop = args.Contains(LaunchModes.ArgNoDesktop, StringComparer.OrdinalIgnoreCase) ? false
            : args.Contains(LaunchModes.ArgDesktop, StringComparer.OrdinalIgnoreCase) ? true
            : null;
        var elevated = AdminPrivilegeManager.IsElevated;

        // Uygulama içi güncelleme: kurulum, kendisini başlatan eski sürümün kapanmasını bekler; bitince yeni sürümü kendiliğinden açar.
        var update = mode == LaunchMode.Install && args.Contains(LaunchModes.ArgUpdate, StringComparer.OrdinalIgnoreCase);
        WaitForLauncher(args, log);

        var setup = new SetupViewModel(installer, log, exe, elevated, desktop, a => AdminPrivilegeManager.RelaunchElevated(a), autoFinish: update);
        var setupWindow = new SetupWindow(setup, autoStart: mode == LaunchMode.Install && elevated);
        MainWindow = setupWindow;
        setupWindow.Show();
    }

    /// <summary><see cref="LaunchModes.ArgWaitPid"/> verildiyse o işlemin (kurulu uygulama / kaldırıcı) kapanmasını en fazla 10 sn bekler.</summary>
    private static void WaitForLauncher(string[] args, SetupLog log)
    {
        var index = Array.FindIndex(args, a => a.Equals(LaunchModes.ArgWaitPid, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid)) return;
        try
        {
            using var launcher = Process.GetProcessById(pid);
            var exited = launcher.WaitForExit(10000);
            log.Info($"Başlatan işlem (PID {pid}) {(exited ? "kapandı" : "10 sn içinde kapanmadı")}.");
        }
        catch (ArgumentException)
        {
            // zaten kapanmış
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Beklenmeyen arayüz hatası: " + e.Exception.Message);
        MessageBox.Show(
            "Beklenmeyen bir hata oluştu ve kaydedildi:\n\n" + e.Exception.Message +
            "\n\nUygulama çalışmaya devam edecek. Ayrıntılar için işlem günlüğüne bakın.",
            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Windows bildirimlerinde gösterilecek logoyu (E-mreLogo) PNG olarak kullanıcı klasörüne yazar.</summary>
    private string? ExtractNotificationIcon()
    {
        try
        {
            var dir = AppInfo.DataDirectory;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "notification-icon.png");
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/E-mreLogo.jpg"));
            if (info is null) return null;
            using (var src = info.Stream)
            {
                var frame = BitmapFrame.Create(src, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(frame);
                using var fs = File.Create(target);
                encoder.Save(fs);
            }
            return target;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Bildirim ikonu hazırlanamadı: {ex.Message}");
            return null;
        }
    }

    private bool TryAcquireSingleInstance(string name, TimeSpan wait)
    {
        try
        {
            _instanceMutex = new Mutex(false, name);
            try
            {
                return _instanceMutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                return true; // önceki örnek beklenmedik şekilde kapanmış; sahiplik bize geçti
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex yönetici olarak çalışan başka bir örneğe ait.
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _viewModel?.Dispose();
        _logger?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { /* sahip değilsek sorun değil */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

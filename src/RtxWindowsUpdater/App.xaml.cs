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
    private const string SingleInstanceMutexName = @"Local\RTXWindowsUpdater.SingleInstance";

    private Logger? _logger;
    private MainViewModel? _viewModel;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Aynı anda iki örneğin güncelleme yapmasını engelle. Yönetici olarak yeniden başlatılan örnek,
        // önceki örneğin kapanmasını birkaç saniye bekler.
        var relaunched = e.Args.Contains(AdminPrivilegeManager.ArgElevated, StringComparer.OrdinalIgnoreCase);
        if (!TryAcquireSingleInstance(relaunched ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
        {
            MessageBox.Show("RTX Windows Updater zaten çalışıyor.", "RTX Windows Updater",
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
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Beklenmeyen arayüz hatası: " + e.Exception.Message);
        MessageBox.Show(
            "Beklenmeyen bir hata oluştu ve kaydedildi:\n\n" + e.Exception.Message +
            "\n\nUygulama çalışmaya devam edecek. Ayrıntılar için işlem günlüğüne bakın.",
            "RTX Windows Updater", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Windows bildirimlerinde gösterilecek logoyu (E-mreLogo) PNG olarak kullanıcı klasörüne yazar.</summary>
    private string? ExtractNotificationIcon()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTX Windows Updater");
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

    private bool TryAcquireSingleInstance(TimeSpan wait)
    {
        try
        {
            _instanceMutex = new Mutex(false, SingleInstanceMutexName);
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
        _viewModel?.Dispose();
        _logger?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { /* sahip değilsek sorun değil */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Yalnızca görsel davranışlar: özel başlık çubuğu, sayfa/iletişim kutusu animasyonları,
/// günlüğün otomatik kaydırılması ve işlem sürerken kapatma onayı. İş mantığı ViewModel'dedir.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        ((INotifyCollectionChanged)vm.Logs).CollectionChanged += OnLogsChanged;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Dialog.PropertyChanged += OnDialogChanged;

        SourceInitialized += (_, _) => ApplyWindowFrame();
        StateChanged += (_, _) => UpdateMaximizeState();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayPageIn(RequirementsPage);
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Başlatma sırasında hata: " + ex.Message, "RTX Windows Updater",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDashboard) && _vm.IsDashboard)
            PlayPageIn(DashboardPage);
    }

    private void OnDialogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogViewModel.IsOpen) || !_vm.Dialog.IsOpen) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        DialogCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        var scale = (ScaleTransform)DialogCard.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
    }

    private void PlayPageIn(FrameworkElement page)
    {
        if (TryFindResource("Sb.PageIn") is Storyboard sb)
            sb.Begin(page);
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _vm.Logs.Count == 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm.Logs.Count > 0)
                LogList.ScrollIntoView(_vm.Logs[^1]);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || !_vm.IsBusy) return;

        e.Cancel = true;
        var close = await _vm.Dialog.ShowAsync(
            "İşlem devam ediyor",
            "Şu anda bir kontrol veya güncelleme işlemi sürüyor. Uygulamayı kapatmak, devam eden kurulumu yarıda bırakabilir.\n\nYine de kapatmak istiyor musunuz?",
            MainViewModel.Icons.Warning, DialogKind.Warning, "Kapat", "Devam et");
        if (close)
        {
            _forceClose = true;
            Close();
        }
    }

    // ----------------------------------------------------------- title bar

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeState()
    {
        // WindowChrome ile büyütülmüş pencere ekran kenarından taşar; kenar boşluğu ile telafi et.
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Önceki boyut" : "Büyüt";
    }

    // ----------------------------------------------------------- DWM (Windows 11 yuvarlak köşe + koyu çerçeve)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    private void ApplyWindowFrame()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            var border = 0x00422A1A; // COLORREF (0x00BBGGRR) → #1A2A42
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch
        {
            // Görsel iyileştirme; başarısız olursa varsayılan çerçeve kullanılır.
        }
    }
}

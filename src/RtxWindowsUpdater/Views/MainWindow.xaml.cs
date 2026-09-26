using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RtxWindowsUpdater.Core;
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

        // Günlük satırları ViewModel'de toplu (150 ms) eklenir; her toplu eklemede TEK kez kaydırılır.
        vm.LogsAppended += OnLogsAppended;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Dialog.PropertyChanged += OnDialogChanged;
        vm.Detail.PropertyChanged += OnDetailChanged;
        PreviewKeyDown += OnPreviewKeyDown;

        SourceInitialized += (_, _) =>
        {
            WindowFrame.Apply(this);
            FitToWorkArea();
        };
        StateChanged += (_, _) => UpdateMaximizeState();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>
    /// Varsayılan boyut (1340x900) küçük ekranda (ör. 1366x768, görev çubuğuyla ~720 px) ekrandan taşmasın: pencere çalışma alanına
    /// sığacak kadar küçültülür ve ortalanır. Büyük ekranda değişiklik yapılmaz.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        if (width == Width && height == Height) return;
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = width;
        Height = height;
        Left = area.Left + (area.Width - width) / 2;
        Top = area.Top + (area.Height - height) / 2;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Pencere kapandıktan sonra ViewModel olayları pencereyi canlı tutmasın.
        _vm.LogsAppended -= OnLogsAppended;
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.Dialog.PropertyChanged -= OnDialogChanged;
        _vm.Detail.PropertyChanged -= OnDetailChanged;
        PreviewKeyDown -= OnPreviewKeyDown;
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
            MessageBox.Show(this, "Başlatma sırasında hata: " + ex.Message, AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDashboard) && _vm.IsDashboard)
        {
            PlayPageIn(DashboardPage);
            // Ana ekran her zaman en üstten açılsın.
            Dispatcher.BeginInvoke(ResetDashboardScroll, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentCategory) && _vm.IsDashboard)
        {
            // Kontrol Merkezi ↔ kategori geçişi: kısa, tek seferlik belirme (sürekli animasyon yok).
            PlayPageIn(_vm.IsHome ? HomeView : CategoryView);
            Dispatcher.BeginInvoke(ResetDashboardScroll, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentSection) && _vm.IsDashboard && !_vm.IsHome)
        {
            // Bölme değişince içerik en üstten açılır (sol menü yerinde kalır); İşlem Günlüğü ise en son satırdan.
            Dispatcher.BeginInvoke(ResetSectionScroll, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (_vm.CurrentSectionKey == SectionKeys.Log)
                Dispatcher.BeginInvoke(ScrollLogToEnd, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private void ResetDashboardScroll()
    {
        HomeScroll.ScrollToTop();
        LeftScroll.ScrollToTop();
        ResetSectionScroll();
    }

    private void ResetSectionScroll()
    {
        CardsScroll.ScrollToTop();
        SummaryScroll.ScrollToTop();
        QuickScroll.ScrollToTop();
        AdminScroll.ScrollToTop();
        LogFilesScroll.ScrollToTop();
        DeviceScroll.ScrollToTop();
        DeviceStatusScroll.ScrollToTop();
        DeviceAboutScroll.ScrollToTop();
        SpeedTestScroll.ScrollToTop();
        SpeedMethodScroll.ScrollToTop();
        SpeedServerScroll.ScrollToTop();
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

    private void OnDetailChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetailViewModel.IsOpen) || !_vm.Detail.IsOpen) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DetailDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var move = (TranslateTransform)DetailDrawer.RenderTransform;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(48, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    private void DetailBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.Detail.IsOpen = false;

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Zorunlu güncelleme penceresi açıkken kısayollar arkadaki sayfada çalışmaz.
        if (_vm.ShowUpdateOverlay) return;
        var onHome = _vm.IsDashboard && _vm.IsHome && !_vm.Dialog.IsOpen && !_vm.Detail.IsOpen;
        // Ctrl+F: ana sayfada arama kutusuna odaklan
        if (onHome && e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            HomeSearchBox.Focus();
            HomeSearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        // Aşağı ok (arama kutusundayken): ilk sonuca geç; sonuçlar arasında Tab / ok tuşlarıyla dolaşılır
        if (onHome && e.Key == System.Windows.Input.Key.Down && HomeSearchBox.IsKeyboardFocusWithin && _vm.HasSearchResults)
        {
            _vm.IsSearchOpen = true;
            if (HomeSearchResults.ItemContainerGenerator.ContainerFromIndex(0) is DependencyObject first && FindChild<System.Windows.Controls.Button>(first) is { } button)
            {
                button.Focus();
                e.Handled = true;
            }
            return;
        }

        // Esc: önce iletişim kutusu (varsa kendi İptal butonu), sonra Detaylı Sonuç paneli kapanır, ana sayfada arama temizlenir,
        // sonra Ana Sayfa'ya dönülür.
        if (e.Key != System.Windows.Input.Key.Escape || _vm.Dialog.IsOpen) return;
        if (onHome && _vm.SearchText.Length > 0)
        {
            _vm.SearchText = string.Empty;
            HomeSearchBox.Focus();
            e.Handled = true;
            return;
        }
        if (_vm.Detail.IsOpen)
        {
            _vm.Detail.IsOpen = false;
            e.Handled = true;
        }
        else if (_vm.IsDashboard && !_vm.IsHome && _vm.GoHomeCommand.CanExecute(null))
        {
            _vm.GoHomeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void PlayPageIn(FrameworkElement page)
    {
        if (TryFindResource("Sb.PageIn") is Storyboard sb)
            sb.Begin(page);
    }

    private bool _scrollPending;

    private void OnLogsAppended(object? sender, EventArgs e)
    {
        // Olay UI iş parçacığında (DispatcherTimer) gelir; art arda gelen toplu eklemeler tek kaydırmada birleştirilir.
        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollPending = false;
            ScrollLogToEnd();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ScrollLogToEnd()
    {
        // Filtre uygulanmış görünümdeki son öğeye kaydır (filtre dışı kalan öğeye kaydırılmaz). Bölme gizliyken atlanır.
        if (LogList.IsVisible && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
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
}

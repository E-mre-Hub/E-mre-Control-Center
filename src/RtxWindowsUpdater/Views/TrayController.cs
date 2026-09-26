using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Bildirim alanı davranışı: pencere kapatılınca uygulama arka planda çalışmaya devam eder (simge "^" gizli simgelerde görünür);
/// çift tıklama pencereyi kaldığı yerden açar, sağ tık kısayol menüsünü gösterir (Aç, Ana Sayfa, Tümünü Kontrol Et, Tek Tıkla Tanıla,
/// Hız Testi, Performans, İşlem Geçmişi, Bildirimler, Çıkış). Menüden başlatılan her işlem önce pencereyi açar; onay gerektirenler
/// her zamanki onay penceresini gösterir.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly MainWindow _window;
    private readonly MainViewModel _vm;
    private readonly Logger _logger;
    private readonly TrayIcon _icon;
    private readonly DispatcherTimer _tooltipTimer;

    public TrayController(MainWindow window, MainViewModel vm, Logger logger)
    {
        _window = window;
        _vm = vm;
        _logger = logger;
        _icon = new TrayIcon(TooltipText());
        _icon.OpenRequested += () => _window.ShowFromTray();
        _icon.ShowSignal += () =>
        {
            _logger.Info("Uygulama yeniden başlatıldı: çalışan pencere öne getirildi.");
            _window.ShowFromTray();
        };
        _icon.ExitSignal += () =>
        {
            _logger.Info("Kurulum / kaldırma uygulamanın kapanmasını istedi.");
            _window.RequestExit();
        };
        _icon.MenuRequested += p => ShowMenu(p);
        IsAvailable = _icon.Show();
        if (IsAvailable) _logger.Info("Bildirim alanı simgesi eklendi.");
        else _logger.Warning("Bildirim alanı simgesi eklenemedi; pencere kapatılınca uygulama kapanır.");

        _tooltipTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _tooltipTimer.Tick += (_, _) =>
        {
            _tooltipTimer.Stop();
            _icon.SetTooltip(TooltipText());
        };
        _vm.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Simge gerçekten eklendi (eklenemediyse pencere kapatma düğmesi uygulamayı kapatır; arka planda görünmez kalmaz).</summary>
    public bool IsAvailable { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.StepText) or nameof(MainViewModel.OperationState))
            _tooltipTimer.Start();
    }

    private string TooltipText() =>
        _vm.IsBusy ? $"{AppInfo.Name}\n{_vm.StepText}" : $"{AppInfo.Name}\nArka planda çalışıyor · açmak için çift tıklayın";

    /// <summary>Pencere ilk kez bildirim alanına gizlendiğinde bir kez bilgi verir (bildirimler kapalıysa gösterilmez).</summary>
    public void NotifyHidden()
    {
        if (_vm.TrayHintShown) return;
        _vm.MarkTrayHintShown();
        if (!_vm.NotificationsEnabled) return;
        _icon.ShowBalloon($"{AppInfo.Name} arka planda çalışıyor",
            "Yeniden açmak için bildirim alanındaki simgeye çift tıklayın; kısayollar ve Çıkış için sağ tıklayın.");
    }

    // ------------------------------------------------------------------ menü

    /// <summary>Sağ tık menüsü (her açılışta güncel durumla yeniden kurulur).</summary>
    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("Tray.Menu") };
        var busy = _vm.IsBusy;
        menu.Items.Add(Item("", $"{AppInfo.Name}'ı aç", busy ? "İşlem sürüyor" : null, true, () => _window.ShowFromTray(), bold: true));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("", "Ana Sayfa", null, true, () => Open(() => _vm.GoHomeCommand.Execute(null))));
        var canCheck = _vm.StartCheckCommand.CanExecute(null);
        menu.Items.Add(Item("", "Tümünü Kontrol Et", busy ? "Sürüyor" : !_vm.IsAdmin ? "Yönetici gerekli" : null, canCheck,
            () => Open(() => { if (_vm.StartCheckCommand.CanExecute(null)) _vm.StartCheckCommand.Execute(null); })));
        menu.Items.Add(Item("", "Tek Tıkla Tanıla", null, true, () => Open(() =>
        {
            _vm.OpenSection(CategoryKeys.SystemTools, SectionKeys.Diagnose);
            if (_vm.Tools.Diagnose.StartCommand.CanExecute(null)) _vm.Tools.Diagnose.StartCommand.Execute(null);
        })));
        menu.Items.Add(Item("", "Hız Testi", _vm.SpeedTest.IsRunning ? "Sürüyor" : null, true,
            () => Open(() => _vm.OpenSection(CategoryKeys.SpeedTest, SectionKeys.SpeedTest))));
        menu.Items.Add(Item("", "Performans", null, true, () => Open(() => _vm.OpenSection(CategoryKeys.Device, SectionKeys.DeviceStatus))));
        menu.Items.Add(Item("", "İşlem Geçmişi", null, true, () => Open(() => _vm.OpenSection(CategoryKeys.Summary, SectionKeys.Recent))));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("", "Bildirimler", _vm.NotificationsEnabled ? "Açık" : "Kapalı", true,
            () => _vm.NotificationsEnabled = !_vm.NotificationsEnabled));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("", "Çıkış", null, true, () => _window.RequestExit()));
        return menu;
    }

    private void ShowMenu(Point devicePoint)
    {
        var menu = BuildMenu();
        var dip = _icon.ToDip(devicePoint);
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = dip.X;
        menu.VerticalOffset = dip.Y;
        // Menü dışına tıklanınca kapanabilsin diye menü penceresi öne alınır (bildirim alanı menülerinin bilinen gereği).
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    /// <summary>Önce pencereyi açar, ardından eylemi uygular (onay pencereleri görünür olsun).</summary>
    private void Open(Action action)
    {
        _window.ShowFromTray();
        _window.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
    }

    private static MenuItem Item(string glyph, string text, string? detail, bool enabled, Action action, bool bold = false)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            Style = (Style)Application.Current.FindResource("T.Icon"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)Application.Current.FindResource("B.NeonBright")
        };
        var item = new MenuItem
        {
            Header = text,
            Icon = icon,
            InputGestureText = detail ?? string.Empty,
            IsEnabled = enabled,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Style = (Style)Application.Current.FindResource("Tray.MenuItem")
        };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => action();
        return item;
    }

    private static Separator Separator() => new() { Style = (Style)Application.Current.FindResource("Tray.Separator") };

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _tooltipTimer.Stop();
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

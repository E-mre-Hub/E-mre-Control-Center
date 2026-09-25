using System.ComponentModel;
using System.Windows;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

/// <summary>Kurulum penceresi. Kurulum sürerken kapatılamaz (yarım kurulum bırakılmaz); iş mantığı <see cref="SetupViewModel"/>'dedir.</summary>
public partial class SetupWindow : Window
{
    private readonly SetupViewModel _vm;

    /// <param name="autoStart">Yönetici olarak yeniden başlatılan örnek: karşılama atlanır, kurulum hemen başlar.</param>
    public SetupWindow(SetupViewModel vm, bool autoStart)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
        SourceInitialized += (_, _) => WindowFrame.Apply(this);
        Closing += OnClosing;
        if (autoStart) Loaded += async (_, _) => await vm.StartInstallAsync();
    }

    private void OnRequestClose()
    {
        _vm.RequestClose -= OnRequestClose;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_vm.IsWorking) e.Cancel = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
}

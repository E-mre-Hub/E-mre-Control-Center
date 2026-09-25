using System.ComponentModel;
using System.Windows;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

/// <summary>Kaldırma penceresi. Kaldırma sürerken kapatılamaz; iş mantığı <see cref="UninstallViewModel"/>'dedir.</summary>
public partial class UninstallWindow : Window
{
    private readonly UninstallViewModel _vm;

    public UninstallWindow(UninstallViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
        SourceInitialized += (_, _) => WindowFrame.Apply(this);
        Closing += OnClosing;
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

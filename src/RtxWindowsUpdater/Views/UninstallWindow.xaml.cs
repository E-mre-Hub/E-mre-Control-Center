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
        UpdateThemeButton();
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

    /// <summary>Başlık çubuğundaki tema düğmesi (v2.0.0): koyu / açık tema anında değişir; seçim kurulan uygulamaya iletilir.</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton()
    {
        ThemeButton.Content = ThemeManager.ToggleGlyph;
        ThemeButton.ToolTip = ThemeManager.ToggleToolTip;
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, ThemeManager.ToggleToolTip);
    }
}

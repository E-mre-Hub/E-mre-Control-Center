using System.Windows;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

/// <summary>İlk açılış penceresi: dil ve görünüm seçimi. "Devam Et" → DialogResult true; kapatılırsa false (uygulama açılmaz).</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow(WelcomeViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Confirmed += () => DialogResult = true;
        SourceInitialized += (_, _) => WindowFrame.Apply(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

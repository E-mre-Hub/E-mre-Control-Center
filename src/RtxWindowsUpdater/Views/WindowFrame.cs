using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Windows 11 penceresi: temaya uygun çerçeve (koyu / açık), yuvarlak köşe ve kenarlık rengi (DWM). Ana pencere, kurulum ve kaldırma
/// ortak kullanır; tema değişince <see cref="ThemeManager"/> açık pencereler için yeniden uygular.
/// </summary>
internal static class WindowFrame
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return; // pencere henüz oluşmadı (SourceInitialized'da yeniden çağrılır)
            var dark = ThemeManager.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            // COLORREF (0x00BBGGRR): koyu #1A2A42, açık #C9D4E3
            var border = ThemeManager.IsLight ? 0x00E3D4C9 : 0x00422A1A;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch
        {
            // Görsel iyileştirme; başarısız olursa varsayılan çerçeve kullanılır.
        }
    }
}

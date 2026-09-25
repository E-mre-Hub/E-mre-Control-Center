using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RtxWindowsUpdater.Views;

/// <summary>Windows 11 penceresi: koyu çerçeve, yuvarlak köşe ve lacivert kenarlık (DWM). Ana pencere, kurulum ve kaldırma ortak kullanır.</summary>
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

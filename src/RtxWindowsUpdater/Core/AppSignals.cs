using System.Runtime.InteropServices;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Çalışan uygulama örneğiyle iletişim: uygulama bildirim alanındayken (pencere gizli) ikinci kez açılırsa mevcut örneğe "göster",
/// kurulum / kaldırma ise "kapan" iletisi gönderir. İletiler uygulamanın bildirim alanı simgesine ait gizli pencereye gider
/// (başlığı <see cref="WindowTitle"/>); yönetici olarak çalışan örnek bu iki iletiyi normal yetkili işlemlerden kabul eder
/// (ChangeWindowMessageFilterEx; yalnızca bu iletiler, başka hiçbir şey).
/// </summary>
public static class AppSignals
{
    public const string WindowTitle = "E-mre Control Center · Bildirim Alanı";

    /// <summary>Pencereyi göster (ikinci örnek başlatıldığında).</summary>
    public static uint ShowMessage { get; } = RegisterWindowMessage("EmreControlCenter.Show");

    /// <summary>Uygulamayı normal şekilde kapat (kurulum / kaldırma; işlem sürüyorsa uygulama kendi onayını sorar).</summary>
    public static uint ExitMessage { get; } = RegisterWindowMessage("EmreControlCenter.Exit");

    /// <summary>
    /// İletiyi çalışan örneğin gizli penceresine gönderir (<paramref name="processId"/> verilirse yalnızca o işleminkine).
    /// Pencere bulunup ileti kuyruğa alındıysa true (eski sürümlerde bu pencere yoktur → false).
    /// </summary>
    public static bool Post(uint message, int? processId = null)
    {
        var sent = false;
        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, null, WindowTitle)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || processId is { } only && pid != only) continue;
            // Göster iletisinde hedef örnek pencereyi öne getirebilsin (ön plan hakkı başlatan bu işlemdedir).
            if (message == ShowMessage) AllowSetForegroundWindow(pid);
            if (PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero)) sent = true;
        }
        return sent;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

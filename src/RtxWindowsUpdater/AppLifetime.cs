using System.Windows;

namespace RtxWindowsUpdater;

/// <summary>
/// Uygulamanın gerçekten kapanıp kapanmadığı. Pencere kapatma düğmesi uygulamayı bildirim alanına gizler; yalnızca bildirim alanındaki
/// "Çıkış", kurulum / güncelleme, yönetici olarak yeniden başlatma ve Windows oturumunun kapanması uygulamayı sonlandırır.
/// </summary>
public static class AppLifetime
{
    public static bool IsExiting { get; private set; }

    /// <summary>Windows oturumu kapanıyor vb.: kapanma iptal edilmez, pencere gizlenmez.</summary>
    public static void MarkExiting() => IsExiting = true;

    /// <summary>Uygulamayı kapatır (pencereler kapanır, bildirim alanı simgesi kaldırılır).</summary>
    public static void Exit()
    {
        IsExiting = true;
        Application.Current?.Shutdown();
    }
}

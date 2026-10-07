using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater;

/// <summary>
/// Uygulamanın gerçekten kapanıp kapanmadığı. Pencere kapatma düğmesi uygulamayı bildirim alanına gizler; yalnızca bildirim alanındaki
/// "Çıkış", kurulum / güncelleme, yönetici olarak yeniden başlatma, dil değişikliği (yeniden başlatma) ve Windows oturumunun kapanması
/// uygulamayı sonlandırır.
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

    /// <summary>
    /// Uygulamayı yeniden başlatır (dil değişikliği). Yeni örnek aynı yetkiyle açılır (yönetici olarak çalışıyorsa yetki devralınır, UAC
    /// yeniden sorulmaz; değilse normal yetkiyle) ve bu örneğin kapanmasını bekler. Başlatılamazsa false ve gerçek hata döner.
    /// </summary>
    public static (bool Started, string? Error) Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return (false, L.T("Uygulamanın dosya yolu belirlenemedi.", "The application's file path could not be determined."));
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add(AdminPrivilegeManager.ArgAccepted);
        psi.ArgumentList.Add(LaunchModes.ArgRestarted);
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
        Exit();
        return (true, null);
    }
}

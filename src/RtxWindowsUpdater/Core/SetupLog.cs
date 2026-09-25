using System.IO;
using System.Text;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Kurulum / kaldırma günlüğü: %TEMP%\E-mre Control Center Kurulum.log (tek dosya, sona eklenir, 1 MB'ı geçince yenilenir).
/// Uygulamanın veri klasörüne yazılmaz: kaldırma sırasında o klasör isteğe bağlı olarak silinir ve yeniden oluşmamalıdır.
/// </summary>
public sealed class SetupLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly object _gate = new();

    public SetupLog(string? path = null)
    {
        FilePath = path ?? Path.Combine(Path.GetTempPath(), AppInfo.Name + " Kurulum.log");
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eski günlük silinemedi; sona eklemeye devam edilir.
        }
    }

    public string FilePath { get; }

    public void Info(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Günlük yazılamazsa kurulum durmaz; sonuçlar yine ekranda gösterilir.
            }
        }
    }
}

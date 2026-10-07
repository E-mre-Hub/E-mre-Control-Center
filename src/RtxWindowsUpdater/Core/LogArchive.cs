using System.IO;
using System.Text.RegularExpressions;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Oturum günlüklerinin arşivi: uygulama her açılışta yeni bir günlük dosyası oluşturur ve eskileri kendiliğinden silinmez.
/// Yalnızca bu uygulamanın günlük klasöründeki "session-YYYYMMDD-HHMMSS.log" dosyaları sayılır / silinir; açık oturumun günlüğü
/// asla silinmez. Silme yalnızca kullanıcı isteyince (onaylı düğme) veya kullanıcı açtığı "açılışta otomatik sil" ayarıyla yapılır.
/// </summary>
public static class LogArchive
{
    public const int RetentionDays = 30;

    private static readonly Regex SessionFile = new(@"^session-\d{8}-\d{6}\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="Count">Günlük dosyası sayısı (açık oturum dahil).</param>
    /// <param name="OldCount">Saklama süresinden eski (silinebilir) dosya sayısı.</param>
    public sealed record Summary(int Count, long Bytes, DateTime? Oldest, int OldCount, long OldBytes);

    public static Summary Read(string directory, string currentFile, DateTime cutoff)
    {
        var files = SessionFiles(directory);
        var old = files.Where(f => IsDeletable(f, currentFile, cutoff)).ToList();
        return new Summary(files.Count, files.Sum(SafeLength), files.Count == 0 ? null : files.Min(f => f.LastWriteTime),
            old.Count, old.Sum(SafeLength));
    }

    /// <summary>Saklama süresinden eski günlükleri siler (açık oturumun günlüğü hariç). Gerçek sonuç: silinen / silinemeyen.</summary>
    public static (int Deleted, long Bytes, int Failed) DeleteOld(string directory, string currentFile, DateTime cutoff)
    {
        int deleted = 0, failed = 0;
        long bytes = 0;
        foreach (var f in SessionFiles(directory).Where(f => IsDeletable(f, currentFile, cutoff)))
        {
            var length = SafeLength(f);
            try
            {
                f.Delete();
                deleted++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        return (deleted, bytes, failed);
    }

    public static DateTime DefaultCutoff() => DateTime.Now.AddDays(-RetentionDays);

    private static List<FileInfo> SessionFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return new DirectoryInfo(directory).EnumerateFiles("session-*.log", SearchOption.TopDirectoryOnly)
                .Where(f => SessionFile.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsDeletable(FileInfo f, string currentFile, DateTime cutoff) =>
        !string.Equals(f.FullName, Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase) && f.LastWriteTime < cutoff;

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RtxWindowsUpdater.Core;

/// <summary>Bir dosyanın şu anda silinip silinemeyeceği (dosyaya dokunmadan sorulur).</summary>
public enum FileDeleteState
{
    /// <summary>Silme için gereken erişim alınabiliyor (başka işlem silmeyi engellemiyor).</summary>
    Deletable,
    /// <summary>Başka bir işlem dosyayı silinmesine izin vermeden açık tutuyor (paylaşım ihlali).</summary>
    InUse,
    /// <summary>Silme erişimi reddedildi (dosya izinleri).</summary>
    AccessDenied,
    /// <summary>Dosya artık yok.</summary>
    Missing,
    /// <summary>Başka bir nedenle açılamadı.</summary>
    Unknown
}

/// <param name="NewestUtc">Oluşturma, değiştirme ve NTFS değişim (ChangeTime) zamanlarının en yenisi.</param>
/// <param name="Error">Açılamadıysa Win32 hata kodu.</param>
public readonly record struct FileProbeResult(FileDeleteState State, DateTime NewestUtc, int Error);

/// <summary>
/// Silmeden önce dosyanın durumunu Windows'a sorar:
///  - Silme erişimi: dosya, <c>DeleteFile</c>'ın istediği erişimle (DELETE) ve tam paylaşımla açılmaya çalışılır, hemen kapatılır.
///    Paylaşım ihlali = başka bir işlem dosyayı silinmesine izin vermeden açık tutuyor; dosya silinemez ("kullanımda").
///    Tam paylaşımla açıldığı için dosyayı kullanan uygulamalar engellenmez; dosyanın içeriği ve tarihleri değişmez.
///  - Klasöre geliş zamanı: NTFS ChangeTime, dosya taşındığında / yeniden adlandırıldığında da güncellenir. Uygulamalar
///    (ör. OneDrive) dosyaları eski tarihleriyle Temp'e TAŞIYABİLİR; yalnızca "değiştirilme tarihi"ne bakmak böyle yeni
///    gelmiş dosyaları "eski" sanar (2026-09-26: OneDrive'ın açık tuttuğu wct*.tmp – tarih 25.09, ChangeTime 26.09 19:52).
/// </summary>
public static class FileProbe
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileBasicInfo info, uint size);

    /// <summary>Dosyanın silinebilirliğini ve klasöre geliş zamanını okur. Dosyayı değiştirmez, silmez.</summary>
    /// <param name="fallbackNewestUtc">Zamanlar okunamazsa kullanılacak değer (FileInfo'dan oluşturma / değiştirme).</param>
    public static FileProbeResult Probe(string path, DateTime fallbackNewestUtc)
    {
        using (var h = CreateFileW(path, Delete | FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                   FlagOpenReparsePoint, IntPtr.Zero))
        {
            if (!h.IsInvalid)
                return new(FileDeleteState.Deletable, Newest(h) ?? fallbackNewestUtc, 0);

            var error = Marshal.GetLastWin32Error();
            var state = error switch
            {
                ErrorSharingViolation or ErrorLockViolation => FileDeleteState.InUse,
                ErrorAccessDenied => FileDeleteState.AccessDenied,
                ErrorFileNotFound or ErrorPathNotFound => FileDeleteState.Missing,
                _ => FileDeleteState.Unknown
            };
            if (state == FileDeleteState.Missing) return new(state, fallbackNewestUtc, error);

            // Silme erişimi alınamadı; yalnızca öznitelik okuma erişimiyle zamanları okumayı dene.
            using var attr = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                FlagOpenReparsePoint, IntPtr.Zero);
            return new(state, (attr.IsInvalid ? null : Newest(attr)) ?? fallbackNewestUtc, error);
        }
    }

    private static DateTime? Newest(SafeFileHandle h)
    {
        if (!GetFileInformationByHandleEx(h, 0 /* FileBasicInfo */, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()))
            return null;
        var newest = Math.Max(info.CreationTime, Math.Max(info.LastWriteTime, info.ChangeTime));
        return newest > 0 ? DateTime.FromFileTimeUtc(newest) : null;
    }

    /// <summary>Dosyaları kullanan uygulamaların adları (Windows Restart Manager; yalnızca tespit, hiçbir işlem kapatılmaz).</summary>
    public static IReadOnlyList<string> AppsUsing(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return [];
        try
        {
            return RestartManager.GetProcessesUsing(files.Take(200).ToList())
                .Where(p => p.ProcessId > 0)
                .Select(p => string.IsNullOrWhiteSpace(p.AppName) ? $"PID {p.ProcessId}" : p.AppName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

public sealed record FileEntry(string Path, long Size, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string SizeText => Formats.Bytes(Size);
    public string ModifiedText => Modified.ToString("dd.MM.yyyy");
}

public sealed record FolderEntry(string Path, long Size, int Files)
{
    public string SizeText => Formats.Bytes(Size);
}

public sealed record TypeEntry(string Extension, long Size, int Count, double Percent)
{
    public string SizeText => Formats.Bytes(Size);
}

public sealed record ScanProgress(int Files, long Bytes, string Folder);

public sealed record AnalysisResult(
    string Root,
    long TotalSize,
    int FileCount,
    int FolderCount,
    int Skipped,
    IReadOnlyList<FileEntry> LargestFiles,
    IReadOnlyList<FolderEntry> LargestFolders,
    IReadOnlyList<TypeEntry> Types,
    VolumeInfo? Volume,
    TimeSpan Duration,
    bool Cancelled);

/// <summary>
/// Depolama analizi: kullanıcının seçtiği klasörü / sürücüyü gerçekten tarar (dosya sayısı ve bayt gerçek; yüzde uydurulmaz). Bağlantılar
/// (junction / symlink) izlenmez, erişilemeyen klasörler atlanır ve sayılır. Hiçbir dosyayı kendiliğinden silmez: yalnızca kullanıcının
/// seçip onayladığı tek dosya Windows'un kendi onay penceresiyle Geri Dönüşüm Kutusu'na gönderilir (kalıcı silinecekse Windows söyler).
/// </summary>
public sealed class LargeFileAnalyzer(Logger logger)
{
    private const int TopFiles = 100;
    private const int TopFolders = 40;
    private const int TopTypes = 20;

    public Task<AnalysisResult> AnalyzeAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Analyze(root, progress, ct), CancellationToken.None);

    private AnalysisResult Analyze(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        root = Path.GetFullPath(root);
        var files = new PriorityQueue<FileEntry, long>();
        var folderSizes = new Dictionary<string, (long Size, int Files)>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, (long Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int fileCount = 0, folderCount = 0, skipped = 0;
        var cancelled = false;
        var lastReport = Stopwatch.StartNew();
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 };

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            var dir = stack.Pop();
            folderCount++;
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                skipped++;
                continue;
            }
            long dirBytes = 0;
            var dirFiles = 0;
            foreach (var e in entries)
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // bağlantı: hedefi başka yerde, izlenmez / sayılmaz
                if (e is DirectoryInfo sub)
                {
                    stack.Push(sub.FullName);
                    continue;
                }
                if (e is not FileInfo f) continue;
                long size;
                try { size = f.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
                total += size;
                fileCount++;
                dirBytes += size;
                dirFiles++;
                files.Enqueue(new FileEntry(f.FullName, size, f.LastWriteTime), size);
                if (files.Count > TopFiles) files.Dequeue();
                var ext = string.IsNullOrEmpty(f.Extension) ? "(uzantısız)" : f.Extension.ToLowerInvariant();
                types[ext] = types.TryGetValue(ext, out var t) ? (t.Size + size, t.Count + 1) : (size, 1);
            }
            // Klasör boyutu: bu klasördeki dosyalar kökten bu klasöre kadar tüm üst klasörlere eklenir.
            if (dirFiles > 0)
            {
                for (var p = dir; p is not null && p.Length >= root.Length; p = Path.GetDirectoryName(p))
                {
                    folderSizes[p] = folderSizes.TryGetValue(p, out var s) ? (s.Size + dirBytes, s.Files + dirFiles) : (dirBytes, dirFiles);
                    if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            if (lastReport.ElapsedMilliseconds >= 200)
            {
                lastReport.Restart();
                progress?.Report(new ScanProgress(fileCount, total, dir));
            }
        }
        progress?.Report(new ScanProgress(fileCount, total, root));

        var largest = new List<FileEntry>();
        while (files.Count > 0) largest.Add(files.Dequeue());
        largest.Reverse();
        var folders = folderSizes.Where(kv => !string.Equals(kv.Key, root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value.Size).Take(TopFolders)
            .Select(kv => new FolderEntry(kv.Key, kv.Value.Size, kv.Value.Files)).ToList();
        var typeList = types.OrderByDescending(kv => kv.Value.Size).Take(TopTypes)
            .Select(kv => new TypeEntry(kv.Key, kv.Value.Size, kv.Value.Count, total > 0 ? kv.Value.Size * 100.0 / total : 0)).ToList();
        var volume = StorageHealthService.ReadVolumes()
            .FirstOrDefault(v => root.StartsWith(v.Letter, StringComparison.OrdinalIgnoreCase));
        logger.Info($"Depolama analizi {(cancelled ? "iptal edildi" : "tamamlandı")}: {root} – {fileCount:N0} dosya, {Formats.Bytes(total)}, " +
                    $"{skipped} erişilemeyen öğe, {watch.Elapsed.TotalSeconds:0.0} sn.");
        return new AnalysisResult(root, total, fileCount, folderCount, skipped, largest, folders, typeList, volume, watch.Elapsed, cancelled);
    }

    // ------------------------------------------------------------------ Geri Dönüşüm Kutusu'na gönderme

    /// <summary>Silinmesi engellenen konumlar (Windows, programlar, ortak veri, bu uygulamanın kurulumu).</summary>
    public static string? ProtectedReason(string path)
    {
        var full = Path.GetFullPath(path);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        ];
        foreach (var r in roots.Where(r => !string.IsNullOrEmpty(r)))
            if (full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return $"{r} altındaki dosyalar korunur (sistem / program dosyası).";
        try
        {
            if (File.GetAttributes(full).HasFlag(FileAttributes.System)) return "Sistem dosyası olarak işaretli; korunur.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Dosyaya erişilemiyor: " + ex.Message;
        }
        return null;
    }

    /// <summary>
    /// Tek dosyayı Geri Dönüşüm Kutusu'na gönderir. Windows kendi onay penceresini gösterir (dosya kalıcı silinecekse bunu Windows yazar);
    /// sonuç dosyanın gerçekten kaldırılıp kaldırılmadığına göre verilir.
    /// </summary>
    public (bool Success, string Message) SendToRecycleBin(string path, IntPtr owner)
    {
        if (!File.Exists(path)) return (false, "Dosya bulunamadı: " + path);
        if (ProtectedReason(path) is { } reason) return (false, reason);
        var op = new ShFileOpStruct
        {
            hwnd = owner,
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            fFlags = FofAllowUndo | FofWantNukeWarning
        };
        var code = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return (false, "İşlem iptal edildi; dosya silinmedi.");
        if (code != 0) return (false, $"Windows dosyayı taşıyamadı (kod 0x{code:X}).");
        if (File.Exists(path)) return (false, "Dosya hâlâ yerinde (kullanımda olabilir).");
        logger.Info("Geri Dönüşüm Kutusu'na gönderildi: " + path);
        return (true, "Dosya Geri Dönüşüm Kutusu'na gönderildi (oradan geri alınabilir).");
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}

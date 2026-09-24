using System.IO;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows Geçici Dosyalar – Ayarlar → Sistem → Depolama → Geçici dosyalar bölümündeki GÜVENLİ kategorilere karşılık
/// gelen gerçek sistem konumları. Ayarlar ekranı okunmaz; bilinen klasörler ve Windows'un resmi mekanizmaları kullanılır.
///
/// Kategoriler:
///  - Kullanıcı geçici dosyaları        : %TEMP%                                 (.NET tek dosya çıkarma klasörü hariç)
///  - Windows geçici dosyaları          : %WINDIR%\Temp
///  - Teslim En İyileştirme önbelleği   : Windows'un resmi cmdlet'leri (Get-DeliveryOptimizationStatus / Get-DOConfig /
///                                        Delete-DeliveryOptimizationCache) + önbellek klasörünün diskteki gerçek boyutu
///  - Windows hata raporlama dosyaları  : %ProgramData%\Microsoft\Windows\WER\ReportArchive, ReportQueue
///  - DirectX gölgelendirici önbelleği  : %LOCALAPPDATA%\D3DSCache
///
/// Her kategori için ayrı ayrı tutulur: ÖLÇÜLEN (bulunan toplam), TEMİZLENEBİLİR (bu işlemde gerçekten silinebilecek),
/// KORUNAN (silinmeyecek: son 24 saatte değişen, salt okunur/sistem, Windows'un sabitlediği veya etkin kullandığı önbellek),
/// temizlikten sonra TEMİZLENEN, KALAN ve KULLANIMDA OLDUĞU İÇİN ATLANAN. "Temizlenebilir" asla korunan alanı içermez.
///
/// Teslim En İyileştirme: sabitlenmiş (IsPinned – Windows Update/Store'un bekleyen işleri için tutulan) ve etkin indirilen
/// dosyalar temizlenebilir sayılmaz ve silinmez (-IncludePinnedFiles KULLANILMAZ). Bir temizlikte Windows'un silmediği
/// dosyalar oturum boyunca "korunan" sayılır; aynı alan tekrar "temizlenebilir" diye gösterilmez.
///
/// Dokunulmayanlar: Çöp Kutusu (ayrı kart), İndirilenler ve diğer kişisel klasörler, Windows.old, sürücü paketleri,
/// Windows Update bileşen deposu (DISM gerektirir), küçük resim önbelleği (Gezgin tarafından kilitli).
/// Güvenlik kuralları: yalnızca bu kök klasörlerin İÇİ; bağlantı noktaları izlenmez; kök klasörler ve son 24 saatte
/// oluşturulmuş klasörler silinmez; kullanımdaki (kilitli) dosyalar atlanır.
/// Temizlikten sonra TÜM kategoriler dosya sisteminden yeniden ölçülür; silme yönteminin kendi bildirimi sonuç sayılmaz.
/// </summary>
public sealed class TemporaryFilesManager(Logger logger) : IUpdateModule, IProgressReportingModule
{
    public string Key => ComponentKeys.TempFiles;
    public string DisplayName => "Windows Geçici Dosyalar";

    public event Action<ModuleProgress>? ProgressChanged;

    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    /// <summary>Bu oturumda Delete-DeliveryOptimizationCache sonrası Windows'un silmediği önbellek dosyaları (FileId).</summary>
    private static readonly HashSet<string> RetainedDoFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RetainedLock = new();

    private sealed record Category(string Id, string Label, bool IsDeliveryOptimization, string[] Roots, string[] Excluded);

    private static IReadOnlyList<Category> Categories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var userTemp = Path.GetTempPath();
        return
        [
            new("user-temp", "Kullanıcı geçici dosyaları", false, [userTemp],
                // .NET tek dosya uygulamalarının (bu uygulama dahil) çıkarılmış çalışma dosyaları silinmez.
                [Path.Combine(userTemp, ".net")]),
            new("windows-temp", "Windows geçici dosyaları", false, [Path.Combine(windows, "Temp")], []),
            new("delivery-optimization", "Teslim En İyileştirme (Delivery Optimization) önbelleği", true, [], []),
            new("wer", "Windows hata raporlama dosyaları", false,
                [Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                 Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")], []),
            new("d3d-shader-cache", "DirectX gölgelendirici önbelleği", false, [Path.Combine(local, "D3DSCache")], [])
        ];
    }

    /// <summary>Bir kategorinin gerçek ölçümü.</summary>
    /// <param name="Measured">Bulunan toplam (diskte).</param>
    /// <param name="Cleanable">Bu işlemde silinebilecek (korunanlar hariç).</param>
    /// <param name="Protected">Silinmeyecek alan.</param>
    /// <param name="ProtectedWhy">Korunma nedenleri (kısa).</param>
    /// <param name="CleanableIds">Teslim En İyileştirme: temizlenebilir sayılan önbellek dosyaları.</param>
    private sealed record Measurement(
        long Measured, int MeasuredFiles, long Cleanable, int CleanableFiles, long Protected, string ProtectedWhy,
        string? Error, IReadOnlyList<string> CleanableIds)
    {
        public static Measurement Failed(string error) => new(0, 0, 0, 0, 0, string.Empty, error, []);
    }

    // ------------------------------------------------------------------ kontrol

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info("Windows geçici dosyaları ölçülüyor (güvenli kategoriler; korunan dosyalar ayrı hesaplanır)...");
        var categories = Categories();
        var items = new List<UpdateItem>();
        long measured = 0, cleanable = 0, protectedBytes = 0;
        var errors = new List<string>();
        var protectedWhy = new List<string>();

        for (var i = 0; i < categories.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = categories[i];
            Report($"{c.Label} ölçülüyor...", 100.0 * i / categories.Count);
            var m = await MeasureAsync(c, ct);
            if (m.Error is not null)
            {
                errors.Add($"{c.Label}: {m.Error}");
                items.Add(new UpdateItem
                {
                    Name = c.Label, Id = c.Id, CurrentVersion = "—", NewVersion = "—",
                    StatusText = "Okunamadı: " + m.Error, Tag = "0"
                });
                logger.Warning($"  {c.Label}: okunamadı – {m.Error}");
                ExecutionTrace.Note($"{c.Label}: okunamadı – {m.Error}");
                continue;
            }

            measured += m.Measured;
            cleanable += m.Cleanable;
            protectedBytes += m.Protected;
            if (m.Protected > 0) protectedWhy.Add($"{c.Label}: {FormatSize(m.Protected)} ({m.ProtectedWhy})");

            var status = m.Cleanable > 0
                ? (m.Protected > 0 ? $"Temizlenebilir · korunan {FormatSize(m.Protected)} ({m.ProtectedWhy})" : "Temizlenebilir")
                : m.Measured > 0 ? $"Temizlenebilir dosya yok · korunan {FormatSize(m.Protected)} ({m.ProtectedWhy})" : "Temizlenecek dosya yok";
            items.Add(new UpdateItem
            {
                Name = c.Label,
                Id = c.Id,
                CurrentVersion = FormatSize(m.Cleanable),
                NewVersion = $"Ölçülen {FormatSize(m.Measured)}",
                UpdateAvailable = m.Cleanable > 0,
                AutoUpdatable = m.Cleanable > 0,
                StatusText = status,
                Tag = m.Cleanable.ToString()
            });

            var line = $"{c.Label}: ölçülen {FormatSize(m.Measured)} ({m.MeasuredFiles} dosya) · temizlenebilir {FormatSize(m.Cleanable)} ({m.CleanableFiles} dosya)" +
                       (m.Protected > 0 ? $" · korunan {FormatSize(m.Protected)} ({m.ProtectedWhy})" : string.Empty);
            logger.Info("  " + line);
            ExecutionTrace.Note(line);
        }

        if (errors.Count == categories.Count)
        {
            const string reason = "Geçici dosya konumlarının hiçbiri okunamadı.";
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var actionable = items.Count(x => x.UpdateAvailable);
        if (actionable > 0)
            logger.Warning($"Geçici dosyalar: ölçülen {FormatSize(measured)} · temizlenebilir {FormatSize(cleanable)}" +
                           (protectedBytes > 0 ? $" · korunan (temizlenmez) {FormatSize(protectedBytes)}" : string.Empty) + ".");
        else
            logger.Success($"Temizlenecek geçici dosya bulunamadı (ölçülen {FormatSize(measured)}, tamamı korunuyor veya boş).");

        var details = $"Ölçülen: {FormatSize(measured)}\nTemizlenebilir: {FormatSize(cleanable)}";
        if (protectedBytes > 0) details += $"\nKorunan (temizlenmez): {FormatSize(protectedBytes)}";

        var reasons = new List<string>();
        if (protectedBytes > 0)
            reasons.Add($"Ek olarak {FormatSize(protectedBytes)} korunuyor ve temizlenmez – " + string.Join("; ", protectedWhy) + ".");
        if (errors.Count > 0)
            reasons.Add($"{errors.Count} kategori okunamadı: " + string.Join("; ", errors) + ".");

        return new ModuleResult
        {
            Key = Key,
            Status = actionable > 0 ? ComponentStatus.UpdateAvailable : ComponentStatus.UpToDate,
            Summary = actionable > 0 ? $"Temizlenebilir: {FormatSize(cleanable)}" : "Temizlenecek geçici dosya bulunamadı",
            Details = details,
            Reason = reasons.Count > 0 ? string.Join("\n", reasons) : null,
            Items = items,
            ActionableCount = actionable
        };
    }

    // ------------------------------------------------------------------ temizlik

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var selected = check.Items.Where(i => i.UpdateAvailable && i.Selected).Select(i => i.Id).ToHashSet();
        if (selected.Count == 0)
        {
            logger.Info("Geçici dosyalar: temizlenecek kategori seçilmedi; hiçbir dosyaya dokunulmadı.");
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Skipped,
                Summary = "Kategori seçilmedi – temizlenmedi",
                Reason = "Temizlik için hiçbir kategori seçilmedi."
            };
        }

        var categories = Categories();

        // 1) Temizlikten hemen önce GERÇEK ölçüm (kontrolden bu yana değişmiş olabilir).
        var before = new Dictionary<string, Measurement>();
        for (var i = 0; i < categories.Count; i++)
        {
            Report($"{categories[i].Label} ölçülüyor...", 5.0 * i / categories.Count);
            before[categories[i].Id] = await MeasureAsync(categories[i], CancellationToken.None);
        }

        // 2) Yalnızca seçilen kategorileri temizle.
        var skipped = new Dictionary<string, (int Files, long Bytes, string? Error)>();
        var toClean = categories.Where(c => selected.Contains(c.Id)).ToList();
        for (var i = 0; i < toClean.Count; i++)
        {
            var c = toClean[i];
            Report($"{c.Label} temizleniyor...", 5 + 85.0 * i / toClean.Count);
            logger.Info($"Temizleniyor: {c.Label}...");
            skipped[c.Id] = c.IsDeliveryOptimization
                ? await CleanDeliveryOptimizationAsync()
                : await Task.Run(() => CleanFolders(c));
        }

        // 3) Temizlikten sonra TÜM kategoriler dosya sisteminden yeniden ölçülür (gerçek yeniden kontrol).
        Report("Temizlik sonrası yeniden ölçülüyor...", 92);
        logger.Info("Temizlik sonrası geçici dosyalar yeniden ölçülüyor...");
        var after = new Dictionary<string, Measurement>();
        foreach (var c in categories)
            after[c.Id] = await MeasureAsync(c, CancellationToken.None);

        // Teslim En İyileştirme: Windows'un silmediği "temizlenebilir" dosyalar bir daha temizlenebilir gösterilmez.
        var doBefore = before["delivery-optimization"];
        var doAfter = after["delivery-optimization"];
        if (selected.Contains("delivery-optimization") && doBefore.Error is null && doAfter.Error is null)
        {
            var retained = doBefore.CleanableIds.Intersect(doAfter.CleanableIds, StringComparer.OrdinalIgnoreCase).ToList();
            if (retained.Count > 0)
            {
                lock (RetainedLock) RetainedDoFiles.UnionWith(retained);
                logger.Warning($"Teslim En İyileştirme: Windows {retained.Count} önbellek dosyasını silmedi; bu dosyalar oturum boyunca korunan sayılacak.");
            }
        }

        // 4) Kategori bazında gerçek sonuç.
        var items = new List<UpdateItem>();
        var problems = new List<string>();
        long cleanableBefore = 0, cleaned = 0, remainingCleanable = 0, skippedBytes = 0;
        long measuredBefore = 0, measuredAfter = 0, protectedAfter = 0;
        var errorCount = 0;
        foreach (var c in categories)
        {
            var b = before[c.Id];
            var a = after[c.Id];
            if (b.Error is null) measuredBefore += b.Measured;
            if (a.Error is null)
            {
                measuredAfter += a.Measured;
                protectedAfter += a.Protected;
            }

            if (!selected.Contains(c.Id))
            {
                items.Add(new UpdateItem
                {
                    Name = c.Label, Id = c.Id,
                    CurrentVersion = b.Error is null ? $"Önce {FormatSize(b.Measured)}" : "—",
                    NewVersion = a.Error is null ? $"Sonra {FormatSize(a.Measured)}" : "—",
                    StatusText = "Seçilmedi – temizlenmedi"
                });
                continue;
            }

            skipped.TryGetValue(c.Id, out var sk);
            if (b.Error is not null || a.Error is not null)
            {
                errorCount++;
                var err = b.Error ?? a.Error!;
                problems.Add($"{c.Label}: ölçülemedi – {err}");
                items.Add(new UpdateItem
                {
                    Name = c.Label, Id = c.Id, CurrentVersion = "—", NewVersion = "—",
                    StatusText = "Ölçülemedi: " + err, Outcome = ItemOutcome.Failed, OutcomeText = "Ölçülemedi"
                });
                continue;
            }

            // Klasörler: temizlenen = silinebilir (24 saatten eski) dosyaların önce/sonra farkı – işlem sırasında
            // oluşan yeni dosyalar sonucu bozmaz. Teslim En İyileştirme: diskteki önbelleğin önce/sonra farkı.
            var cleanedHere = c.IsDeliveryOptimization
                ? Math.Max(0, b.Measured - a.Measured)
                : Math.Max(0, b.Cleanable - a.Cleanable);
            cleanableBefore += b.Cleanable;
            cleaned += cleanedHere;
            remainingCleanable += a.Cleanable;
            skippedBytes += sk.Bytes;

            var parts = new List<string>
            {
                $"Temizlenen {FormatSize(cleanedHere)}",
                $"Önce {FormatSize(b.Measured)} · Sonra {FormatSize(a.Measured)}"
            };
            if (a.Cleanable > 0) parts.Add($"Kalan temizlenebilir {FormatSize(a.Cleanable)}");
            if (sk.Files > 0) parts.Add($"{sk.Files} dosya kullanımda/erişilemez olduğu için atlandı ({FormatSize(sk.Bytes)})");
            if (a.Protected > 0) parts.Add($"Korunan {FormatSize(a.Protected)} ({a.ProtectedWhy})");
            if (sk.Error is not null)
            {
                parts.Add("Hata: " + sk.Error);
                problems.Add($"{c.Label}: {sk.Error}");
            }
            else if (a.Cleanable > 0)
            {
                problems.Add($"{c.Label}: {FormatSize(a.Cleanable)} temizlenemedi (dosyalar kullanımda veya Windows silmedi).");
            }

            var outcome = cleanedHere > 0 && a.Cleanable == 0 && sk.Error is null ? ItemOutcome.Updated : ItemOutcome.Failed;
            items.Add(new UpdateItem
            {
                Name = c.Label,
                Id = c.Id,
                CurrentVersion = $"Önce {FormatSize(b.Measured)}",
                NewVersion = $"Sonra {FormatSize(a.Measured)}",
                StatusText = string.Join(" · ", parts),
                Tag = cleanedHere.ToString(),
                Outcome = outcome,
                OutcomeText = outcome == ItemOutcome.Updated ? "Temizlendi"
                    : cleanedHere > 0 ? "Kısmen temizlendi" : "Temizlenemedi"
            });
            var line = $"{c.Label}: {string.Join(" · ", parts)}";
            logger.Info("  " + line);
            ExecutionTrace.Note(line);
        }

        logger.Info($"Temizlik öncesi: {FormatSize(measuredBefore)} · Temizlik sonrası: {FormatSize(measuredAfter)} · " +
                    $"Gerçekten temizlenen: {FormatSize(cleaned)}");

        var detailText =
            $"Ölçülen (önce): {FormatSize(measuredBefore)}\n" +
            $"Bu işlemde temizlenebilen: {FormatSize(cleanableBefore)}\n" +
            $"Temizlenen: {FormatSize(cleaned)}\n" +
            $"Kalan (sonra ölçülen): {FormatSize(measuredAfter)}";
        if (remainingCleanable > 0) detailText += $"\nKullanımda / atlanan: {FormatSize(remainingCleanable)}";
        if (protectedAfter > 0) detailText += $"\nKorunan (temizlenmez): {FormatSize(protectedAfter)}";

        ModuleResult result;
        if (cleanableBefore == 0 && errorCount == 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.UpToDate, Summary = "Temizlenecek dosya kalmamıştı",
                Details = detailText, Items = items
            };
            logger.Success("Geçici dosyalar: temizlik anında temizlenebilir dosya kalmamıştı.");
        }
        else if (cleaned > 0 && remainingCleanable == 0 && problems.Count == 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.Updated, Summary = $"Temizlenen: {FormatSize(cleaned)}",
                Details = detailText, Items = items,
                Reason = protectedAfter > 0 ? $"Korunan {FormatSize(protectedAfter)} temizlik kapsamında değildi." : null
            };
            logger.Success($"Geçici dosyalar temizlendi: {FormatSize(cleaned)}.");
        }
        else if (cleaned > 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.PartiallyUpdated,
                Summary = $"Kısmen temizlendi: {FormatSize(cleaned)}",
                Details = detailText, Reason = string.Join("\n", problems), Items = items
            };
            logger.Warning($"Geçici dosyalar kısmen temizlendi: {FormatSize(cleaned)} temizlendi" +
                           (remainingCleanable > 0 ? $", {FormatSize(remainingCleanable)} kullanımda olduğu için kaldı" : string.Empty) + ".");
        }
        else
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.Failed, Summary = "Geçici dosyalar temizlenemedi",
                Details = detailText, Items = items,
                Reason = problems.Count > 0 ? string.Join("\n", problems) : "Hiçbir dosya silinemedi."
            };
            logger.Error("Geçici dosyalar temizlenemedi.");
        }
        return result;
    }

    // ------------------------------------------------------------------ ölçüm

    private async Task<Measurement> MeasureAsync(Category c, CancellationToken ct)
    {
        if (c.IsDeliveryOptimization) return await MeasureDeliveryOptimizationAsync(ct);
        return await Task.Run(() => MeasureFolders(c, ct), ct);
    }

    private static Measurement MeasureFolders(Category c, CancellationToken ct)
    {
        long measured = 0, cleanable = 0, recent = 0, readOnlyOrSystem = 0;
        int measuredFiles = 0, cleanableFiles = 0;
        string? error = null;
        var cutoff = DateTime.UtcNow - MinimumAge;
        foreach (var root in c.Roots)
        {
            if (!Directory.Exists(root)) continue;
            // Kök okunamıyorsa (ör. yönetici izni yok) "0 bayt / temizlenecek dosya yok" DENMEZ; hata olarak raporlanır.
            var access = ProbeRoot(root);
            if (access is not null)
            {
                error = access;
                continue;
            }
            try
            {
                foreach (var f in AllFiles(root, c.Excluded))
                {
                    ct.ThrowIfCancellationRequested();
                    measured += f.Length;
                    measuredFiles++;
                    if (IsEligible(f, cutoff))
                    {
                        cleanable += f.Length;
                        cleanableFiles++;
                    }
                    else if (f.LastWriteTimeUtc > cutoff)
                    {
                        recent += f.Length;
                    }
                    else
                    {
                        readOnlyOrSystem += f.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error = ex.Message;
            }
        }
        if (error is not null && measuredFiles == 0) return Measurement.Failed(error);

        var why = new List<string>();
        if (recent > 0) why.Add("son 24 saatte değişen");
        if (readOnlyOrSystem > 0) why.Add("salt okunur/sistem");
        return new Measurement(measured, measuredFiles, cleanable, cleanableFiles, measured - cleanable,
            string.Join(", ", why), null, []);
    }

    /// <summary>Kök klasörün içeriği okunabiliyor mu? Okunamıyorsa nedeni döner.</summary>
    private static string? ProbeRoot(string root)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            e.MoveNext();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return AdminPrivilegeManager.IsElevated ? "Erişim reddedildi" : "Erişim reddedildi (yönetici izni gerekli)";
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Kökün içindeki tüm dosyalar (bağlantı noktaları izlenmez; dışlanan klasörler atlanır).</summary>
    private static IEnumerable<FileInfo> AllFiles(string root, string[] excluded)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device,
            ReturnSpecialDirectories = false
        };
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (excluded.Any(e => f.FullName.StartsWith(e.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            yield return f;
        }
    }

    /// <summary>Silinebilir mi: sistem/salt okunur değil ve son 24 saatte değişmemiş.</summary>
    private static bool IsEligible(FileInfo f, DateTime cutoffUtc) =>
        (f.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) == 0 && f.LastWriteTimeUtc <= cutoffUtc;

    // ------------------------------------------------------------------ silme

    /// <summary>Uygun dosyaları siler; kullanımda olduğu için silinemeyenlerin sayısı ve boyutu döner.</summary>
    private (int Files, long Bytes, string? Error) CleanFolders(Category c)
    {
        var lockedFiles = 0;
        long lockedBytes = 0;
        string? error = null;
        var cutoff = DateTime.UtcNow - MinimumAge;
        foreach (var root in c.Roots)
        {
            if (!Directory.Exists(root) || ProbeRoot(root) is not null) continue;
            try
            {
                foreach (var f in AllFiles(root, c.Excluded).Where(f => IsEligible(f, cutoff)).ToList())
                {
                    try
                    {
                        f.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        lockedFiles++;
                        lockedBytes += SafeLength(f);
                    }
                }

                // Boşalan alt klasörleri kaldır. Kök klasörün kendisi, dışlanan klasörler ve son 24 saatte OLUŞTURULMUŞ
                // klasörler (çalışan bir uygulamanın yeni açtığı boş klasör olabilir) asla silinmez.
                var dirOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                    ReturnSpecialDirectories = false
                };
                var dirs = new DirectoryInfo(root).EnumerateDirectories("*", dirOptions)
                    .Where(d => !c.Excluded.Any(e => d.FullName.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Where(d => d.CreationTimeUtc <= cutoff)
                    .OrderByDescending(d => d.FullName.Length)
                    .ToList();
                foreach (var d in dirs)
                {
                    try
                    {
                        if (!d.EnumerateFileSystemInfos().Any()) d.Delete(recursive: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kullanımda */ }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error = ex.Message;
                logger.Warning($"{c.Label} temizlenirken hata: {ex.Message}");
            }
        }
        return (lockedFiles, lockedBytes, error);
    }

    private static long SafeLength(FileInfo f)
    {
        try
        {
            f.Refresh();
            return f.Exists ? f.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // --- Teslim En İyileştirme: yalnızca Windows'un resmi cmdlet'leri ---

    private const string DoStatusScript = """
        $list = @()
        foreach ($x in @(Get-DeliveryOptimizationStatus -WarningAction SilentlyContinue)) {
            $list += @{ id = [string]$x.FileId; size = [long]$x.FileSize; pinned = [bool]$x.IsPinned; status = [string]$x.Status }
        }
        $dir = $null
        try { $dir = [string](Get-DOConfig -ErrorAction Stop).WorkingDirectory } catch { }
        Write-Result @{ files = $list; dir = $dir }
        """;

    private const string DoDeleteScript = """
        Delete-DeliveryOptimizationCache -Force
        Write-Result @{ ok = $true }
        """;

    private async Task<Measurement> MeasureDeliveryOptimizationAsync(CancellationToken ct)
    {
        var ps = await PowerShellRunner.RunAsync(DoStatusScript, TimeSpan.FromMinutes(2), ct, traceName: "Get-DeliveryOptimizationStatus / Get-DOConfig");
        if (!ps.Ok) return Measurement.Failed(ps.DescribeFailure("Get-DeliveryOptimizationStatus"));
        var d = ps.Data!.Value;

        HashSet<string> retained;
        lock (RetainedLock) retained = [.. RetainedDoFiles];

        long listed = 0, cleanable = 0, pinned = 0, active = 0, retainedBytes = 0;
        var cleanableIds = new List<string>();
        var listedFiles = 0;
        foreach (var f in d.Arr("files"))
        {
            var size = f.Long("size") ?? 0;
            var id = f.Str("id") ?? string.Empty;
            var status = f.Str("status") ?? string.Empty;
            listed += size;
            listedFiles++;
            if (f.Bool("pinned") == true) pinned += size;
            else if (status.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                     status.Contains("Pause", StringComparison.OrdinalIgnoreCase) ||
                     status.Contains("Queue", StringComparison.OrdinalIgnoreCase)) active += size;
            else if (retained.Contains(id)) retainedBytes += size;
            else
            {
                cleanable += size;
                cleanableIds.Add(id);
            }
        }

        // Diskteki GERÇEK önbellek boyutu (klasör okunabiliyorsa – yönetici olarak çalışırken).
        long? disk = null;
        var diskFiles = 0;
        var dir = d.Str("dir");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                if (Directory.Exists(dir) && ProbeRoot(dir) is null)
                {
                    long sum = 0;
                    foreach (var f in AllFiles(dir, []))
                    {
                        sum += f.Length;
                        diskFiles++;
                    }
                    disk = sum;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Output($"Teslim En İyileştirme önbellek klasörü okunamadı ({dir}): {ex.Message}");
            }
        }

        // Ne diskteki klasör okunabildi ne de Windows kayıt bildirdi: "0 bayt / temizlenecek dosya yok" DENMEZ.
        if (disk is null && listedFiles == 0)
            return Measurement.Failed("Windows etkin önbellek kaydı bildirmedi ve önbellek klasörü okunamadı" +
                                      (AdminPrivilegeManager.IsElevated ? string.Empty : " (yönetici izni gerekli)"));

        var measured = disk ?? listed;
        cleanable = Math.Min(cleanable, measured);
        var protectedBytes = Math.Max(0, measured - cleanable);
        var unlisted = disk is { } dk ? Math.Max(0, dk - listed) : 0;

        var why = new List<string>();
        if (pinned > 0) why.Add($"Windows tarafından sabitlenmiş {FormatSize(pinned)}");
        if (active > 0) why.Add($"etkin indirme {FormatSize(active)}");
        if (retainedBytes > 0) why.Add($"önceki temizlikte Windows silmedi {FormatSize(retainedBytes)}");
        if (unlisted > 0) why.Add($"Windows'un etkin kayıt bildirmediği önbellek {FormatSize(unlisted)}");
        if (disk is null) why.Add("önbellek klasörü okunamadı; Windows'un bildirdiği kayıtlar ölçüldü");

        ExecutionTrace.Note($"Teslim En İyileştirme: Windows kaydı {listedFiles} dosya / {FormatSize(listed)} · sabitlenmiş {FormatSize(pinned)} · " +
                            $"etkin {FormatSize(active)} · diskte {(disk is null ? "okunamadı" : FormatSize(disk.Value) + $" ({diskFiles} dosya)")}");
        return new Measurement(measured, disk is null ? listedFiles : diskFiles, cleanable, cleanableIds.Count, protectedBytes,
            string.Join(", ", why), null, cleanableIds);
    }

    private async Task<(int Files, long Bytes, string? Error)> CleanDeliveryOptimizationAsync()
    {
        var ps = await PowerShellRunner.RunAsync(DoDeleteScript, TimeSpan.FromMinutes(5), CancellationToken.None,
            traceName: "Delete-DeliveryOptimizationCache -Force");
        if (ps.Ok) return (0, 0, null);
        var error = ps.DescribeFailure("Delete-DeliveryOptimizationCache");
        logger.Warning("Teslim En İyileştirme önbelleği temizlenemedi: " + error);
        return (0, 0, error);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bayt"
    };

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }
}

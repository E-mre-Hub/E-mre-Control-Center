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
///                                        Get-DeliveryOptimizationPerfSnap / Delete-DeliveryOptimizationCache) + (okunabiliyorsa)
///                                        önbellek klasörünün diskteki gerçek boyutu
///  - Windows hata raporlama dosyaları  : %ProgramData%\Microsoft\Windows\WER\ReportArchive, ReportQueue
///  - DirectX gölgelendirici önbelleği  : %LOCALAPPDATA%\D3DSCache
///
/// Her kategori için ayrı ayrı tutulur: ÖLÇÜLEN (bulunan toplam), TEMİZLENEBİLİR (bu işlemde gerçekten silinebilecek),
/// KORUNAN (silinmeyecek: son 24 saatte oluşturulan / değişen / klasöre taşınan (NTFS ChangeTime), salt okunur/sistem, şu anda
/// başka bir uygulamanın silinmesine izin vermeden açık tuttuğu – Restart Manager ile uygulama adıyla –, erişim reddedilen,
/// Windows'un sabitlediği veya etkin kullandığı önbellek),
/// temizlikten sonra TEMİZLENEN, KALAN ve KULLANIMDA OLDUĞU İÇİN ATLANAN. "Temizlenebilir" asla korunan alanı içermez.
///
/// Teslim En İyileştirme: sabitlenmiş (IsPinned – Windows Update/Store'un bekleyen işleri için tutulan) ve etkin indirilen
/// dosyalar temizlenebilir sayılmaz ve silinmez (-IncludePinnedFiles KULLANILMAZ). Bir temizlikte Windows'un silmediği
/// dosyalar oturum boyunca "korunan" sayılır; aynı alan tekrar "temizlenebilir" diye gösterilmez. Windows silmeyi ARKA PLANDA
/// yaptığı için silinen dosyaların Windows kaydından çıkması en fazla DoWaitSeconds sn beklenir (2026-09-26: komuttan 1 sn sonra
/// hâlâ listelenen 149 MB birkaç dakika içinde silinmişti – uygulama yanlışlıkla "Windows silmedi" diyordu).
///
/// Dokunulmayanlar: Çöp Kutusu (ayrı kart), İndirilenler ve diğer kişisel klasörler, Windows.old, sürücü paketleri,
/// Windows Update bileşen deposu (DISM gerektirir), küçük resim önbelleği (Gezgin tarafından kilitli).
/// Güvenlik kuralları: yalnızca bu kök klasörlerin İÇİ; bağlantı noktaları izlenmez; kök klasörler ve son 24 saatte
/// oluşturulmuş klasörler silinmez; kullanımdaki (kilitli) dosyalar denenmez, silme anında kilitlenen atlanır ve kullanan uygulama yazılır.
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

        var checkCutoff = DateTime.UtcNow - MinimumAge;
        for (var i = 0; i < categories.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = categories[i];
            Report($"{c.Label} ölçülüyor...", 100.0 * i / categories.Count);
            var m = await MeasureAsync(c, checkCutoff, ct);
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

        // Önce / temizlik / sonra ölçümleri TEK bir "24 saatten eski" sınırı kullanır. NEDEN (2026-09-30 günlüğü): Teslim En İyileştirme
        // beklemesi 52 sn sürdü; bu sırada Windows Temp'teki bir dosya 24 saat sınırını geçip yalnızca SON ölçümde "temizlenebilir" oldu ve
        // sonuç yanlışlıkla "151 KB kullanımda olduğu için kaldı – kısmen temizlendi" çıktı (dosya 3 dk sonra sorunsuz silindi).
        var cutoff = DateTime.UtcNow - MinimumAge;

        // 1) Temizlikten hemen önce GERÇEK ölçüm (kontrolden bu yana değişmiş olabilir).
        var before = new Dictionary<string, Measurement>();
        for (var i = 0; i < categories.Count; i++)
        {
            Report($"{categories[i].Label} ölçülüyor...", 5.0 * i / categories.Count);
            before[categories[i].Id] = await MeasureAsync(categories[i], cutoff, CancellationToken.None);
        }

        // 2) Yalnızca seçilen kategorileri temizle.
        var skipped = new Dictionary<string, (int Files, long Bytes, string? Error, string? Note)>();
        var toClean = categories.Where(c => selected.Contains(c.Id)).ToList();
        for (var i = 0; i < toClean.Count; i++)
        {
            var c = toClean[i];
            Report($"{c.Label} temizleniyor...", 5 + 85.0 * i / toClean.Count);
            logger.Info($"Temizleniyor: {c.Label}...");
            skipped[c.Id] = c.IsDeliveryOptimization
                ? await CleanDeliveryOptimizationAsync(before[c.Id].Error is null ? before[c.Id].CleanableIds : [])
                : await Task.Run(() => CleanFolders(c, cutoff));
        }

        // 3) Temizlikten sonra TÜM kategoriler dosya sisteminden yeniden ölçülür (gerçek yeniden kontrol).
        Report("Temizlik sonrası yeniden ölçülüyor...", 92);
        logger.Info("Temizlik sonrası geçici dosyalar yeniden ölçülüyor...");
        var after = new Dictionary<string, Measurement>();
        foreach (var c in categories)
            after[c.Id] = await MeasureAsync(c, cutoff, CancellationToken.None);

        // Teslim En İyileştirme: Windows'un silmediği "temizlenebilir" dosyalar bir daha temizlenebilir gösterilmez.
        var doBefore = before["delivery-optimization"];
        var doAfter = after["delivery-optimization"];
        if (selected.Contains("delivery-optimization") && doBefore.Error is null && doAfter.Error is null)
        {
            var retained = doBefore.CleanableIds.Intersect(doAfter.CleanableIds, StringComparer.OrdinalIgnoreCase).ToList();
            if (retained.Count > 0)
            {
                lock (RetainedLock) RetainedDoFiles.UnionWith(retained);
                logger.Warning($"Teslim En İyileştirme: Windows {retained.Count} önbellek dosyasını {DoWaitSeconds} sn beklemeye rağmen silmedi; " +
                               "bu dosyalar oturum boyunca korunan sayılacak.");
            }
        }

        // 4) Kategori bazında gerçek sonuç.
        var items = new List<UpdateItem>();
        var problems = new List<string>();
        var notes = new List<string>();
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
            var skippedText = sk.Files == 0 ? null
                : $"{sk.Files} dosya silinirken kullanımda / erişilemez olduğu için atlandı ({FormatSize(sk.Bytes)}" +
                  (sk.Note is null ? ")" : $" – {sk.Note})");
            if (skippedText is not null)
            {
                parts.Add(skippedText);
                notes.Add($"{c.Label}: {skippedText}.");
            }
            if (a.Protected > 0) parts.Add($"Korunan {FormatSize(a.Protected)} ({a.ProtectedWhy})");
            if (sk.Error is not null)
            {
                parts.Add("Hata: " + sk.Error);
                problems.Add($"{c.Label}: {sk.Error}");
            }
            else if (a.Cleanable > 0)
            {
                problems.Add($"{c.Label}: {FormatSize(a.Cleanable)} temizlenemedi – " + (c.IsDeliveryOptimization
                    ? (sk.Note ?? "Windows silme komutunu onayladı ancak dosyalar Windows kaydında duruyor") + "."
                    : "silme isteği Windows tarafından reddedildi" + (sk.Note is null ? "." : $" (kullanan: {sk.Note}).")));
            }
            else if (c.IsDeliveryOptimization && sk.Note is not null)
            {
                notes.Add($"{c.Label}: {sk.Note}.");
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
                OutcomeText = outcome == ItemOutcome.Updated
                    ? (sk.Files > 0 ? $"Temizlendi – {sk.Files} dosya kullanımda olduğu için atlandı" : "Temizlendi")
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
                Reason = string.Join("\n", notes.Append(protectedAfter > 0
                    ? $"Korunan {FormatSize(protectedAfter)} temizlik kapsamında değildi (son 24 saatte eklenen/değişen, salt okunur/sistem veya kullanımdaki dosyalar)."
                    : string.Empty).Where(l => l.Length > 0)) is { Length: > 0 } reason ? reason : null
            };
            logger.Success($"Geçici dosyalar temizlendi: {FormatSize(cleaned)}.");
        }
        else if (cleaned > 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.PartiallyUpdated,
                Summary = $"Kısmen temizlendi: {FormatSize(cleaned)}",
                Details = detailText, Reason = string.Join("\n", problems.Concat(notes)), Items = items
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

    private async Task<Measurement> MeasureAsync(Category c, DateTime cutoffUtc, CancellationToken ct)
    {
        if (c.IsDeliveryOptimization) return await MeasureDeliveryOptimizationAsync(ct);
        return await Task.Run(() => MeasureFolders(c, cutoffUtc, ct), ct);
    }

    private static Measurement MeasureFolders(Category c, DateTime cutoff, CancellationToken ct)
    {
        long measured = 0, cleanable = 0, recent = 0, readOnlyOrSystem = 0, inUse = 0, denied = 0;
        int measuredFiles = 0, cleanableFiles = 0;
        var inUsePaths = new List<string>();
        string? error = null;
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
                    var length = f.Length;
                    switch (Classify(f, cutoff))
                    {
                        case FileClass.Missing:
                            continue; // ölçüm sırasında silindi
                        case FileClass.Cleanable:
                            cleanable += length;
                            cleanableFiles++;
                            break;
                        case FileClass.Recent:
                            recent += length;
                            break;
                        case FileClass.ReadOnlyOrSystem:
                            readOnlyOrSystem += length;
                            break;
                        case FileClass.InUse:
                            inUse += length;
                            if (inUsePaths.Count < 200) inUsePaths.Add(f.FullName);
                            break;
                        default:
                            denied += length;
                            break;
                    }
                    measured += length;
                    measuredFiles++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error = ex.Message;
            }
        }
        if (error is not null && measuredFiles == 0) return Measurement.Failed(error);

        var why = new List<string>();
        if (recent > 0) why.Add("son 24 saatte eklenen/değişen");
        if (readOnlyOrSystem > 0) why.Add("salt okunur/sistem");
        if (inUse > 0) why.Add($"kullanımda {FormatSize(inUse)}{AppsSuffix(FileProbe.AppsUsing(inUsePaths))}");
        if (denied > 0) why.Add($"erişim reddedildi {FormatSize(denied)}");
        return new Measurement(measured, measuredFiles, cleanable, cleanableFiles, measured - cleanable,
            string.Join(", ", why), null, []);
    }

    private static string AppsSuffix(IReadOnlyList<string> apps) => apps.Count == 0 ? string.Empty : " – " + string.Join(", ", apps);

    /// <summary>Tek bir dosyanın temizlikteki yeri.</summary>
    private enum FileClass { Cleanable, Recent, ReadOnlyOrSystem, InUse, AccessDenied, Missing }

    /// <summary>
    /// Silinebilir mi: sistem / salt okunur değil; son 24 saatte oluşturulmamış, değişmemiş ve klasöre TAŞINMAMIŞ (NTFS
    /// ChangeTime – uygulamalar dosyayı eski tarihleriyle Temp'e taşıyabilir); şu anda başka bir işlem tarafından silinmesine
    /// izin vermeden açık tutulmuyor (kullanımda). Dosya bu sırada değiştirilmez / silinmez (bkz. <see cref="FileProbe"/>).
    /// </summary>
    private static FileClass Classify(FileInfo f, DateTime cutoffUtc)
    {
        if ((f.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) return FileClass.ReadOnlyOrSystem;
        var basic = f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;
        if (basic > cutoffUtc) return FileClass.Recent;
        var probe = FileProbe.Probe(f.FullName, basic);
        if (probe.State == FileDeleteState.Missing) return FileClass.Missing;
        if (probe.NewestUtc > cutoffUtc) return FileClass.Recent;
        return probe.State switch
        {
            FileDeleteState.Deletable => FileClass.Cleanable,
            FileDeleteState.InUse => FileClass.InUse,
            _ => FileClass.AccessDenied
        };
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

    // ------------------------------------------------------------------ silme

    /// <summary>
    /// Temizlenebilir dosyaları siler (kullanımdaki, yeni gelen, salt okunur dosyalar denenmez – ölçümde korunan sayılırlar).
    /// Silinebilir görünüp silme anında silinemeyenlerin sayısı, boyutu ve (Restart Manager ile) onları kullanan uygulamalar döner.
    /// </summary>
    private (int Files, long Bytes, string? Error, string? Note) CleanFolders(Category c, DateTime cutoff)
    {
        var lockedFiles = 0;
        long lockedBytes = 0;
        var lockedPaths = new List<string>();
        string? error = null;
        foreach (var root in c.Roots)
        {
            if (!Directory.Exists(root) || ProbeRoot(root) is not null) continue;
            try
            {
                foreach (var f in AllFiles(root, c.Excluded).Where(f => Classify(f, cutoff) == FileClass.Cleanable).ToList())
                {
                    try
                    {
                        f.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        lockedFiles++;
                        lockedBytes += SafeLength(f);
                        if (lockedPaths.Count < 200) lockedPaths.Add(f.FullName);
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
        var apps = FileProbe.AppsUsing(lockedPaths);
        return (lockedFiles, lockedBytes, error, apps.Count > 0 ? string.Join(", ", apps) : null);
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
        $cache = $null
        try { $cache = [long](Get-DeliveryOptimizationPerfSnap -ErrorAction Stop).CacheSizeBytes } catch { }
        Write-Result @{ files = $list; dir = $dir; cache = $cache }
        """;

    /// <summary>
    /// Delete-DeliveryOptimizationCache, silmeyi Teslim En İyileştirme hizmetine bırakır ve hizmet dosyaları ARKA PLANDA siler:
    /// komut "Successfully deleted" dedikten 1 sn sonra dosyalar hâlâ listelenebilir, birkaç saniye / dakika içinde kaybolur
    /// (2026-09-26 20:08:57 silindi → 20:08:58'de 5 dosya listede → birkaç dakika sonra 0 kayıt, önbellek 0 bayt). Bu yüzden
    /// temizlenecek dosyaların Windows kaydından gerçekten çıkması en fazla <paramref name="waitSeconds"/> sn beklenir.
    /// </summary>
    private static string DoDeleteScript(IReadOnlyCollection<string> ids, int waitSeconds) => $$"""
        $targets = [string[]]@()
        if ({{ids.Count}} -gt 0) { $targets = [string[]](ConvertFrom-Json {{PowerShellRunner.ToPsLiteral(ids)}}) }
        Delete-DeliveryOptimizationCache -Force
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $remaining = $targets.Count
        $statusError = $null
        while ($targets.Count -gt 0) {
            $present = @{}
            try {
                foreach ($x in @(Get-DeliveryOptimizationStatus -WarningAction SilentlyContinue -ErrorAction Stop)) { $present[[string]$x.FileId] = $true }
            } catch { $statusError = $_.Exception.Message; break }
            $remaining = @($targets | Where-Object { $present.ContainsKey($_) }).Count
            if ($remaining -eq 0 -or $sw.Elapsed.TotalSeconds -ge {{waitSeconds}}) { break }
            Start-Sleep -Seconds 2
        }
        Write-Result @{ ok = $true; targets = $targets.Count; remaining = $remaining; waited = [int][Math]::Ceiling($sw.Elapsed.TotalSeconds); statusError = $statusError }
        """;

    private const int DoWaitSeconds = 60;

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

        // Windows'un kendi bildirdiği önbellek boyutu (Get-DeliveryOptimizationPerfSnap.CacheSizeBytes). Önbellek klasörü
        // okunamadığında (Get-DOConfig bu Windows sürümünde WorkingDirectory vermiyor; klasör NetworkService'e ait) ölçüm budur.
        var reported = d.Long("cache");

        // Ne diskteki klasör okunabildi ne de Windows kayıt / boyut bildirdi: "0 bayt / temizlenecek dosya yok" DENMEZ.
        if (disk is null && listedFiles == 0 && reported is null)
            return Measurement.Failed("Windows önbellek kaydı ve önbellek boyutu bildirmedi; önbellek klasörü okunamadı");

        var measured = disk ?? Math.Max(listed, reported ?? 0);
        cleanable = Math.Min(cleanable, measured);
        var protectedBytes = Math.Max(0, measured - cleanable);
        var unlisted = Math.Max(0, measured - listed);

        var why = new List<string>();
        if (pinned > 0) why.Add($"Windows tarafından sabitlenmiş {FormatSize(pinned)}");
        if (active > 0) why.Add($"etkin indirme {FormatSize(active)}");
        if (retainedBytes > 0) why.Add($"önceki temizlikte Windows {DoWaitSeconds} sn içinde silmedi {FormatSize(retainedBytes)}");
        if (unlisted > 0) why.Add($"Windows'un etkin kayıt bildirmediği önbellek {FormatSize(unlisted)}");
        if (disk is null && measured > 0) why.Add("önbellek klasörü okunamadı; Windows'un bildirdiği kayıtlar / boyut ölçüldü");

        ExecutionTrace.Note($"Teslim En İyileştirme: Windows kaydı {listedFiles} dosya / {FormatSize(listed)} · sabitlenmiş {FormatSize(pinned)} · " +
                            $"etkin {FormatSize(active)} · Windows'un bildirdiği önbellek {(reported is null ? "okunamadı" : FormatSize(reported.Value))} · " +
                            $"diskte {(disk is null ? "okunamadı" : FormatSize(disk.Value) + $" ({diskFiles} dosya)")}");
        return new Measurement(measured, disk is null ? listedFiles : diskFiles, cleanable, cleanableIds.Count, protectedBytes,
            string.Join(", ", why), null, cleanableIds);
    }

    /// <summary>
    /// Windows'un resmi silme komutu + silinmesi istenen dosyaların Windows kaydından gerçekten çıkmasının beklenmesi
    /// (en fazla <see cref="DoWaitSeconds"/> sn). Sonuç yine de ardından yapılan yeniden ölçümden okunur.
    /// </summary>
    private async Task<(int Files, long Bytes, string? Error, string? Note)> CleanDeliveryOptimizationAsync(IReadOnlyCollection<string> ids)
    {
        if (ids.Count > 0)
            Report($"Teslim En İyileştirme: Windows'un {ids.Count} önbellek dosyasını silmesi bekleniyor (en fazla {DoWaitSeconds} sn)...", null);
        var ps = await PowerShellRunner.RunAsync(DoDeleteScript(ids, DoWaitSeconds), TimeSpan.FromMinutes(5), CancellationToken.None,
            traceName: $"Delete-DeliveryOptimizationCache -Force + silinmenin doğrulanması (en fazla {DoWaitSeconds} sn)");
        if (!ps.Ok)
        {
            var error = ps.DescribeFailure("Delete-DeliveryOptimizationCache");
            logger.Warning("Teslim En İyileştirme önbelleği temizlenemedi: " + error);
            return (0, 0, error, null);
        }

        var d = ps.Data!.Value;
        var targets = d.Long("targets") ?? 0;
        var remaining = d.Long("remaining") ?? 0;
        var waited = d.Long("waited") ?? 0;
        var statusError = d.Str("statusError");
        string? note = null;
        if (statusError is not null)
        {
            note = $"silme sonrası Windows kaydı okunamadı: {statusError}";
            logger.Warning($"Teslim En İyileştirme: {note}");
        }
        else if (targets > 0 && remaining == 0)
        {
            logger.Info($"Teslim En İyileştirme: Windows {targets} önbellek dosyasını sildi (Windows kaydından çıktığı {waited} sn içinde doğrulandı).");
        }
        else if (remaining > 0)
        {
            note = $"Windows silme komutunu onayladı ancak {waited} sn içinde {remaining}/{targets} dosyayı silmedi";
            logger.Warning($"Teslim En İyileştirme: {note}.");
        }
        return (0, 0, null, note);
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

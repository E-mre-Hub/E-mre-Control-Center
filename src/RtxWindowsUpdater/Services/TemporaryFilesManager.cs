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
    public string DisplayName => L.T("Windows Geçici Dosyalar", "Windows Temporary Files");

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
            new("user-temp", L.T("Kullanıcı geçici dosyaları", "User temporary files"), false, [userTemp],
                // .NET tek dosya uygulamalarının (bu uygulama dahil) çıkarılmış çalışma dosyaları silinmez.
                [Path.Combine(userTemp, ".net")]),
            new("windows-temp", L.T("Windows geçici dosyaları", "Windows temporary files"), false, [Path.Combine(windows, "Temp")], []),
            new("delivery-optimization", L.T("Teslim En İyileştirme (Delivery Optimization) önbelleği", "Delivery Optimization cache"), true, [], []),
            new("wer", L.T("Windows hata raporlama dosyaları", "Windows error reporting files"), false,
                [Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                 Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")], []),
            new("d3d-shader-cache", L.T("DirectX gölgelendirici önbelleği", "DirectX shader cache"), false, [Path.Combine(local, "D3DSCache")], [])
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
        logger.Info(L.T("Windows geçici dosyaları ölçülüyor (güvenli kategoriler; korunan dosyalar ayrı hesaplanır)...", "Measuring Windows temporary files (safe categories; protected files are counted separately)..."));
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
            Report(L.T($"{c.Label} ölçülüyor...", $"Measuring {c.Label}..."), 100.0 * i / categories.Count);
            var m = await MeasureAsync(c, checkCutoff, ct);
            if (m.Error is not null)
            {
                errors.Add($"{c.Label}: {m.Error}");
                items.Add(new UpdateItem
                {
                    Name = c.Label, Id = c.Id, CurrentVersion = "—", NewVersion = "—",
                    StatusText = L.T("Okunamadı: ", "Unreadable: ") + m.Error, Tag = "0"
                });
                logger.Warning(L.T($"  {c.Label}: okunamadı – {m.Error}", $"  {c.Label}: unreadable – {m.Error}"));
                ExecutionTrace.Note(L.T($"{c.Label}: okunamadı – {m.Error}", $"{c.Label}: unreadable – {m.Error}"));
                continue;
            }

            measured += m.Measured;
            cleanable += m.Cleanable;
            protectedBytes += m.Protected;
            if (m.Protected > 0) protectedWhy.Add($"{c.Label}: {FormatSize(m.Protected)} ({m.ProtectedWhy})");

            var status = m.Cleanable > 0
                ? (m.Protected > 0 ? L.T($"Temizlenebilir · korunan {FormatSize(m.Protected)} ({m.ProtectedWhy})", $"Cleanable · protected {FormatSize(m.Protected)} ({m.ProtectedWhy})") : L.T("Temizlenebilir", "Cleanable"))
                : m.Measured > 0 ? L.T($"Temizlenebilir dosya yok · korunan {FormatSize(m.Protected)} ({m.ProtectedWhy})", $"No cleanable files · protected {FormatSize(m.Protected)} ({m.ProtectedWhy})") : L.T("Temizlenecek dosya yok", "Nothing to clean");
            items.Add(new UpdateItem
            {
                Name = c.Label,
                Id = c.Id,
                CurrentVersion = FormatSize(m.Cleanable),
                NewVersion = L.T($"Ölçülen {FormatSize(m.Measured)}", $"Measured {FormatSize(m.Measured)}"),
                UpdateAvailable = m.Cleanable > 0,
                AutoUpdatable = m.Cleanable > 0,
                StatusText = status,
                Tag = m.Cleanable.ToString()
            });

            var line = L.T($"{c.Label}: ölçülen {FormatSize(m.Measured)} ({m.MeasuredFiles} dosya) · temizlenebilir {FormatSize(m.Cleanable)} ({m.CleanableFiles} dosya)", $"{c.Label}: measured {FormatSize(m.Measured)} ({m.MeasuredFiles} files) · cleanable {FormatSize(m.Cleanable)} ({m.CleanableFiles} files)") +
                       (m.Protected > 0 ? L.T($" · korunan {FormatSize(m.Protected)} ({m.ProtectedWhy})", $" · protected {FormatSize(m.Protected)} ({m.ProtectedWhy})") : string.Empty);
            logger.Info("  " + line);
            ExecutionTrace.Note(line);
        }

        if (errors.Count == categories.Count)
        {
            var reason = L.T("Geçici dosya konumlarının hiçbiri okunamadı.", "None of the temporary file locations could be read.");
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }

        var actionable = items.Count(x => x.UpdateAvailable);
        if (actionable > 0)
            logger.Warning(L.T($"Geçici dosyalar: ölçülen {FormatSize(measured)} · temizlenebilir {FormatSize(cleanable)}", $"Temporary files: measured {FormatSize(measured)} · cleanable {FormatSize(cleanable)}") +
                           (protectedBytes > 0 ? L.T($" · korunan (temizlenmez) {FormatSize(protectedBytes)}", $" · protected (not cleaned) {FormatSize(protectedBytes)}") : string.Empty) + ".");
        else
            logger.Success(L.T($"Temizlenecek geçici dosya bulunamadı (ölçülen {FormatSize(measured)}, tamamı korunuyor veya boş).", $"No temporary files to clean (measured {FormatSize(measured)}, all protected or empty)."));

        var details = L.T($"Ölçülen: {FormatSize(measured)}\nTemizlenebilir: {FormatSize(cleanable)}", $"Measured: {FormatSize(measured)}\nCleanable: {FormatSize(cleanable)}");
        if (protectedBytes > 0) details += L.T($"\nKorunan (temizlenmez): {FormatSize(protectedBytes)}", $"\nProtected (not cleaned): {FormatSize(protectedBytes)}");

        var reasons = new List<string>();
        if (protectedBytes > 0)
            reasons.Add(L.T($"Ek olarak {FormatSize(protectedBytes)} korunuyor ve temizlenmez – ", $"In addition, {FormatSize(protectedBytes)} is protected and not cleaned – ") + string.Join("; ", protectedWhy) + ".");
        if (errors.Count > 0)
            reasons.Add(L.T($"{errors.Count} kategori okunamadı: ", $"{errors.Count} category(ies) could not be read: ") + string.Join("; ", errors) + ".");

        return new ModuleResult
        {
            Key = Key,
            Status = actionable > 0 ? ComponentStatus.UpdateAvailable : ComponentStatus.UpToDate,
            Summary = actionable > 0 ? CleanablePrefix + FormatSize(cleanable) : L.T("Temizlenecek geçici dosya bulunamadı", "No temporary files to clean"),
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
            logger.Info(L.T("Geçici dosyalar: temizlenecek kategori seçilmedi; hiçbir dosyaya dokunulmadı.", "Temporary files: no category selected for cleaning; no file was touched."));
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Skipped,
                Summary = L.T("Kategori seçilmedi – temizlenmedi", "No category selected – not cleaned"),
                Reason = L.T("Temizlik için hiçbir kategori seçilmedi.", "No category was selected for cleaning.")
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
            Report(L.T($"{categories[i].Label} ölçülüyor...", $"Measuring {categories[i].Label}..."), 5.0 * i / categories.Count);
            before[categories[i].Id] = await MeasureAsync(categories[i], cutoff, CancellationToken.None);
        }

        // 2) Yalnızca seçilen kategorileri temizle.
        var skipped = new Dictionary<string, (int Files, long Bytes, string? Error, string? Note)>();
        var toClean = categories.Where(c => selected.Contains(c.Id)).ToList();
        for (var i = 0; i < toClean.Count; i++)
        {
            var c = toClean[i];
            Report(L.T($"{c.Label} temizleniyor...", $"Cleaning {c.Label}..."), 5 + 85.0 * i / toClean.Count);
            logger.Info(L.T($"Temizleniyor: {c.Label}...", $"Cleaning: {c.Label}..."));
            skipped[c.Id] = c.IsDeliveryOptimization
                ? await CleanDeliveryOptimizationAsync(before[c.Id].Error is null ? before[c.Id].CleanableIds : [])
                : await Task.Run(() => CleanFolders(c, cutoff));
        }

        // 3) Temizlikten sonra TÜM kategoriler dosya sisteminden yeniden ölçülür (gerçek yeniden kontrol).
        Report(L.T("Temizlik sonrası yeniden ölçülüyor...", "Measuring again after cleaning..."), 92);
        logger.Info(L.T("Temizlik sonrası geçici dosyalar yeniden ölçülüyor...", "Measuring temporary files again after cleaning..."));
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
                logger.Warning(L.T($"Teslim En İyileştirme: Windows {retained.Count} önbellek dosyasını {DoWaitSeconds} sn beklemeye rağmen silmedi; ", $"Delivery Optimization: Windows did not delete {retained.Count} cache file(s) despite waiting {DoWaitSeconds} sec; ") +
                               L.T("bu dosyalar oturum boyunca korunan sayılacak.", "these files will count as protected for the rest of the session."));
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
                    CurrentVersion = b.Error is null ? L.T($"Önce {FormatSize(b.Measured)}", $"Before {FormatSize(b.Measured)}") : "—",
                    NewVersion = a.Error is null ? L.T($"Sonra {FormatSize(a.Measured)}", $"After {FormatSize(a.Measured)}") : "—",
                    StatusText = L.T("Seçilmedi – temizlenmedi", "Not selected – not cleaned")
                });
                continue;
            }

            skipped.TryGetValue(c.Id, out var sk);
            if (b.Error is not null || a.Error is not null)
            {
                errorCount++;
                var err = b.Error ?? a.Error!;
                problems.Add(L.T($"{c.Label}: ölçülemedi – {err}", $"{c.Label}: could not be measured – {err}"));
                items.Add(new UpdateItem
                {
                    Name = c.Label, Id = c.Id, CurrentVersion = "—", NewVersion = "—",
                    StatusText = L.T("Ölçülemedi: ", "Could not be measured: ") + err, Outcome = ItemOutcome.Failed, OutcomeText = L.T("Ölçülemedi", "Could not be measured")
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
                L.T($"Temizlenen {FormatSize(cleanedHere)}", $"Cleaned {FormatSize(cleanedHere)}"),
                L.T($"Önce {FormatSize(b.Measured)} · Sonra {FormatSize(a.Measured)}", $"Before {FormatSize(b.Measured)} · After {FormatSize(a.Measured)}")
            };
            if (a.Cleanable > 0) parts.Add(L.T($"Kalan temizlenebilir {FormatSize(a.Cleanable)}", $"Still cleanable {FormatSize(a.Cleanable)}"));
            var skippedText = sk.Files == 0 ? null
                : L.T($"{sk.Files} dosya silinirken kullanımda / erişilemez olduğu için atlandı ({FormatSize(sk.Bytes)}", $"{sk.Files} file(s) skipped because they were in use / inaccessible during deletion ({FormatSize(sk.Bytes)}") +
                  (sk.Note is null ? ")" : $" – {sk.Note})");
            if (skippedText is not null)
            {
                parts.Add(skippedText);
                notes.Add($"{c.Label}: {skippedText}.");
            }
            if (a.Protected > 0) parts.Add(L.T($"Korunan {FormatSize(a.Protected)} ({a.ProtectedWhy})", $"Protected {FormatSize(a.Protected)} ({a.ProtectedWhy})"));
            if (sk.Error is not null)
            {
                parts.Add(L.T("Hata: ", "Error: ") + sk.Error);
                problems.Add($"{c.Label}: {sk.Error}");
            }
            else if (a.Cleanable > 0)
            {
                problems.Add(L.T($"{c.Label}: {FormatSize(a.Cleanable)} temizlenemedi – ", $"{c.Label}: {FormatSize(a.Cleanable)} could not be cleaned – ") + (c.IsDeliveryOptimization
                    ? (sk.Note ?? L.T("Windows silme komutunu onayladı ancak dosyalar Windows kaydında duruyor", "Windows accepted the delete command but the files are still in the Windows records")) + "."
                    : L.T("silme isteği Windows tarafından reddedildi", "Windows rejected the delete request") + (sk.Note is null ? "." : L.T($" (kullanan: {sk.Note}).", $" (used by: {sk.Note})."))));
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
                CurrentVersion = L.T($"Önce {FormatSize(b.Measured)}", $"Before {FormatSize(b.Measured)}"),
                NewVersion = L.T($"Sonra {FormatSize(a.Measured)}", $"After {FormatSize(a.Measured)}"),
                StatusText = string.Join(" · ", parts),
                Tag = cleanedHere.ToString(),
                Outcome = outcome,
                OutcomeText = outcome == ItemOutcome.Updated
                    ? (sk.Files > 0 ? L.T($"Temizlendi – {sk.Files} dosya kullanımda olduğu için atlandı", $"Cleaned – {sk.Files} file(s) skipped because they were in use") : L.T("Temizlendi", "Cleaned"))
                    : cleanedHere > 0 ? L.T("Kısmen temizlendi", "Partially cleaned") : L.T("Temizlenemedi", "Could not be cleaned")
            });
            var line = $"{c.Label}: {string.Join(" · ", parts)}";
            logger.Info("  " + line);
            ExecutionTrace.Note(line);
        }

        logger.Info(L.T($"Temizlik öncesi: {FormatSize(measuredBefore)} · Temizlik sonrası: {FormatSize(measuredAfter)} · ", $"Before cleaning: {FormatSize(measuredBefore)} · After cleaning: {FormatSize(measuredAfter)} · ") +
                    L.T($"Gerçekten temizlenen: {FormatSize(cleaned)}", $"Actually cleaned: {FormatSize(cleaned)}"));

        var detailText =
            L.T($"Ölçülen (önce): {FormatSize(measuredBefore)}\n", $"Measured (before): {FormatSize(measuredBefore)}\n") +
            L.T($"Bu işlemde temizlenebilen: {FormatSize(cleanableBefore)}\n", $"Cleanable in this operation: {FormatSize(cleanableBefore)}\n") +
            L.T($"Temizlenen: {FormatSize(cleaned)}\n", $"Cleaned: {FormatSize(cleaned)}\n") +
            L.T($"Kalan (sonra ölçülen): {FormatSize(measuredAfter)}", $"Remaining (measured after): {FormatSize(measuredAfter)}");
        if (remainingCleanable > 0) detailText += L.T($"\nKullanımda / atlanan: {FormatSize(remainingCleanable)}", $"\nIn use / skipped: {FormatSize(remainingCleanable)}");
        if (protectedAfter > 0) detailText += L.T($"\nKorunan (temizlenmez): {FormatSize(protectedAfter)}", $"\nProtected (not cleaned): {FormatSize(protectedAfter)}");

        ModuleResult result;
        if (cleanableBefore == 0 && errorCount == 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.UpToDate, Summary = L.T("Temizlenecek dosya kalmamıştı", "No files were left to clean"),
                Details = detailText, Items = items
            };
            logger.Success(L.T("Geçici dosyalar: temizlik anında temizlenebilir dosya kalmamıştı.", "Temporary files: no cleanable files were left at the time of cleaning."));
        }
        else if (cleaned > 0 && remainingCleanable == 0 && problems.Count == 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.Updated, Summary = L.T($"Temizlenen: {FormatSize(cleaned)}", $"Cleaned: {FormatSize(cleaned)}"),
                Details = detailText, Items = items,
                Reason = string.Join("\n", notes.Append(protectedAfter > 0
                    ? L.T($"Korunan {FormatSize(protectedAfter)} temizlik kapsamında değildi (son 24 saatte eklenen/değişen, salt okunur/sistem veya kullanımdaki dosyalar).", $"The protected {FormatSize(protectedAfter)} was not in the cleanup scope (files added/changed in the last 24 hours, read-only/system or in use).")
                    : string.Empty).Where(l => l.Length > 0)) is { Length: > 0 } reason ? reason : null
            };
            logger.Success(L.T($"Geçici dosyalar temizlendi: {FormatSize(cleaned)}.", $"Temporary files cleaned: {FormatSize(cleaned)}."));
        }
        else if (cleaned > 0)
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.PartiallyUpdated,
                Summary = L.T($"Kısmen temizlendi: {FormatSize(cleaned)}", $"Partially cleaned: {FormatSize(cleaned)}"),
                Details = detailText, Reason = string.Join("\n", problems.Concat(notes)), Items = items
            };
            logger.Warning(L.T($"Geçici dosyalar kısmen temizlendi: {FormatSize(cleaned)} temizlendi", $"Temporary files partially cleaned: {FormatSize(cleaned)} cleaned") +
                           (remainingCleanable > 0 ? L.T($", {FormatSize(remainingCleanable)} kullanımda olduğu için kaldı", $", {FormatSize(remainingCleanable)} remained because it was in use") : string.Empty) + ".");
        }
        else
        {
            result = new ModuleResult
            {
                Key = Key, Status = ComponentStatus.Failed, Summary = L.T("Geçici dosyalar temizlenemedi", "Temporary files could not be cleaned"),
                Details = detailText, Items = items,
                Reason = problems.Count > 0 ? string.Join("\n", problems) : L.T("Hiçbir dosya silinemedi.", "No file could be deleted.")
            };
            logger.Error(L.T("Geçici dosyalar temizlenemedi.", "Temporary files could not be cleaned."));
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
        if (recent > 0) why.Add(L.T("son 24 saatte eklenen/değişen", "added/changed in the last 24 hours"));
        if (readOnlyOrSystem > 0) why.Add(L.T("salt okunur/sistem", "read-only/system"));
        if (inUse > 0) why.Add(L.T($"kullanımda {FormatSize(inUse)}{AppsSuffix(FileProbe.AppsUsing(inUsePaths))}", $"in use {FormatSize(inUse)}{AppsSuffix(FileProbe.AppsUsing(inUsePaths))}"));
        if (denied > 0) why.Add(L.T($"erişim reddedildi {FormatSize(denied)}", $"access denied {FormatSize(denied)}"));
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
            return AdminPrivilegeManager.IsElevated ? L.T("Erişim reddedildi", "Access denied") : L.T("Erişim reddedildi (yönetici izni gerekli)", "Access denied (administrator permission required)");
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
                logger.Warning(L.T($"{c.Label} temizlenirken hata: {ex.Message}", $"Error while cleaning {c.Label}: {ex.Message}"));
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
                logger.Output(L.T($"Teslim En İyileştirme önbellek klasörü okunamadı ({dir}): {ex.Message}", $"Could not read the Delivery Optimization cache folder ({dir}): {ex.Message}"));
            }
        }

        // Windows'un kendi bildirdiği önbellek boyutu (Get-DeliveryOptimizationPerfSnap.CacheSizeBytes). Önbellek klasörü
        // okunamadığında (Get-DOConfig bu Windows sürümünde WorkingDirectory vermiyor; klasör NetworkService'e ait) ölçüm budur.
        var reported = d.Long("cache");

        // Ne diskteki klasör okunabildi ne de Windows kayıt / boyut bildirdi: "0 bayt / temizlenecek dosya yok" DENMEZ.
        if (disk is null && listedFiles == 0 && reported is null)
            return Measurement.Failed(L.T("Windows önbellek kaydı ve önbellek boyutu bildirmedi; önbellek klasörü okunamadı", "Windows reported no cache records and no cache size; the cache folder could not be read"));

        var measured = disk ?? Math.Max(listed, reported ?? 0);
        cleanable = Math.Min(cleanable, measured);
        var protectedBytes = Math.Max(0, measured - cleanable);
        var unlisted = Math.Max(0, measured - listed);

        var why = new List<string>();
        if (pinned > 0) why.Add(L.T($"Windows tarafından sabitlenmiş {FormatSize(pinned)}", $"pinned by Windows {FormatSize(pinned)}"));
        if (active > 0) why.Add(L.T($"etkin indirme {FormatSize(active)}", $"active download {FormatSize(active)}"));
        if (retainedBytes > 0) why.Add(L.T($"önceki temizlikte Windows {DoWaitSeconds} sn içinde silmedi {FormatSize(retainedBytes)}", $"not deleted by Windows within {DoWaitSeconds} sec in the previous cleanup {FormatSize(retainedBytes)}"));
        if (unlisted > 0) why.Add(L.T($"Windows'un etkin kayıt bildirmediği önbellek {FormatSize(unlisted)}", $"cache without active records reported by Windows {FormatSize(unlisted)}"));
        if (disk is null && measured > 0) why.Add(L.T("önbellek klasörü okunamadı; Windows'un bildirdiği kayıtlar / boyut ölçüldü", "the cache folder could not be read; the records / size reported by Windows were measured"));

        ExecutionTrace.Note(L.T($"Teslim En İyileştirme: Windows kaydı {listedFiles} dosya / {FormatSize(listed)} · sabitlenmiş {FormatSize(pinned)} · ", $"Delivery Optimization: Windows records {listedFiles} files / {FormatSize(listed)} · pinned {FormatSize(pinned)} · ") +
                            L.T($"etkin {FormatSize(active)} · Windows'un bildirdiği önbellek {(reported is null ? "okunamadı" : FormatSize(reported.Value))} · ", $"active {FormatSize(active)} · cache reported by Windows {(reported is null ? "unreadable" : FormatSize(reported.Value))} · ") +
                            L.T($"diskte {(disk is null ? "okunamadı" : FormatSize(disk.Value) + $" ({diskFiles} dosya)")}", $"on disk {(disk is null ? "unreadable" : FormatSize(disk.Value) + $" ({diskFiles} files)")}"));
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
            Report(L.T($"Teslim En İyileştirme: Windows'un {ids.Count} önbellek dosyasını silmesi bekleniyor (en fazla {DoWaitSeconds} sn)...", $"Delivery Optimization: waiting for Windows to delete {ids.Count} cache file(s) (up to {DoWaitSeconds} sec)..."), null);
        var ps = await PowerShellRunner.RunAsync(DoDeleteScript(ids, DoWaitSeconds), TimeSpan.FromMinutes(5), CancellationToken.None,
            traceName: L.T($"Delete-DeliveryOptimizationCache -Force + silinmenin doğrulanması (en fazla {DoWaitSeconds} sn)", $"Delete-DeliveryOptimizationCache -Force + verification of the deletion (up to {DoWaitSeconds} sec)"));
        if (!ps.Ok)
        {
            var error = ps.DescribeFailure("Delete-DeliveryOptimizationCache");
            logger.Warning(L.T("Teslim En İyileştirme önbelleği temizlenemedi: ", "Could not clean the Delivery Optimization cache: ") + error);
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
            note = L.T($"silme sonrası Windows kaydı okunamadı: {statusError}", $"could not read the Windows records after deletion: {statusError}");
            logger.Warning(L.T($"Teslim En İyileştirme: {note}", $"Delivery Optimization: {note}"));
        }
        else if (targets > 0 && remaining == 0)
        {
            logger.Info(L.T($"Teslim En İyileştirme: Windows {targets} önbellek dosyasını sildi (Windows kaydından çıktığı {waited} sn içinde doğrulandı).", $"Delivery Optimization: Windows deleted {targets} cache file(s) (verified within {waited} sec as they left the Windows records)."));
        }
        else if (remaining > 0)
        {
            note = L.T($"Windows silme komutunu onayladı ancak {waited} sn içinde {remaining}/{targets} dosyayı silmedi", $"Windows accepted the delete command but did not delete {remaining}/{targets} file(s) within {waited} sec");
            logger.Warning(L.T($"Teslim En İyileştirme: {note}.", $"Delivery Optimization: {note}."));
        }
        return (0, 0, null, note);
    }

    /// <summary>Kontrol özetinin öneki ("Temizlenebilir: 1,2 GB"); onay penceresi boyutu bu önek olmadan gösterir.</summary>
    public static string CleanablePrefix => L.T("Temizlenebilir: ", "Cleanable: ");

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => L.T($"{bytes} bayt", $"{bytes} bytes")
    };

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }
}

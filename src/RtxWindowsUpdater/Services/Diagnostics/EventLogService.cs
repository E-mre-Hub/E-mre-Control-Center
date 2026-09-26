using System.Diagnostics.Eventing.Reader;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Olay Görüntüleyicisi'ndeki bir kayıt; ileti Windows'un kendi metnidir (FormatDescription), değiştirilmez.</summary>
public sealed record EventEntry(DateTime? Time, string Log, string Provider, int Id, int Level, string Message)
{
    public string TimeText => Formats.Date(Time);
    public string LevelText => EventLogService.LevelText(Level);
    public string LogText => Log switch { "System" => "Sistem", "Application" => "Uygulama", _ => Log };
    public string ShortMessage => Message.Length <= 220 ? Message.ReplaceLineEndings(" ") : Message[..220].ReplaceLineEndings(" ") + "…";
}

public sealed record EventQueryResult(IReadOnlyList<EventEntry> Entries, bool Truncated, IReadOnlyList<string> Errors);

/// <summary>Bir günlükteki düzey sayıları (ileti biçimlendirilmez – hızlı).</summary>
public sealed record EventCounts(int Critical, int Errors, int Warnings, bool Capped, string? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// Windows olay günlüğü okuyucu (EventLogReader; Olay Görüntüleyicisi ile aynı kaynak). Sistem ve Uygulama günlükleri yönetici
/// gerektirmeden okunur (Güvenlik günlüğü okunmaz). Sorgu yalnızca ilgili ekran açılınca / filtre değişince çalışır, sürekli izleme yok.
/// </summary>
public sealed class EventLogService(Logger logger)
{
    public const int MaxEntries = 400;
    private const int CountCap = 10000;

    public static string LevelText(int level) => level switch
    {
        1 => "Kritik",
        2 => "Hata",
        3 => "Uyarı",
        4 or 0 => "Bilgi",
        5 => "Ayrıntılı",
        _ => $"Düzey {level}"
    };

    internal static string XPath(IEnumerable<int> levels, TimeSpan period, string? extra = null)
    {
        var lv = string.Join(" or ", levels.Select(l => $"Level={l}"));
        var ms = (long)Math.Max(1, period.TotalMilliseconds);
        return $"*[System[({lv}) and TimeCreated[timediff(@SystemTime) <= {ms}]{(extra is null ? "" : " and " + extra)}]]";
    }

    /// <summary>Seçilen günlüklerden, seçilen düzeylerdeki son kayıtlar (en yeni önce, toplam en fazla <see cref="MaxEntries"/>).</summary>
    public Task<EventQueryResult> QueryAsync(IReadOnlyCollection<string> logs, IReadOnlyCollection<int> levels, TimeSpan period,
        CancellationToken ct) => Task.Run(() =>
    {
        var all = new List<EventEntry>();
        var errors = new List<string>();
        var truncated = false;
        if (levels.Count == 0) return new EventQueryResult(all, false, errors);
        var xpath = XPath(levels, period);
        foreach (var log in logs)
        {
            try
            {
                var got = 0;
                foreach (var e in Read(log, xpath, ct))
                {
                    if (got++ >= MaxEntries) { truncated = true; break; }
                    all.Add(e);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors.Add($"{log} günlüğü okunamadı: {Describe(ex)}");
            }
        }
        var ordered = all.OrderByDescending(e => e.Time).Take(MaxEntries).ToList();
        truncated |= all.Count > ordered.Count;
        logger.Info($"Olay günlüğü sorgusu: {string.Join("+", logs)}, düzey {string.Join(",", levels)}, {period.TotalHours:0} sa → {ordered.Count} kayıt" +
                    (truncated ? " (sınır)" : "") + (errors.Count > 0 ? "; " + string.Join("; ", errors) : "."));
        return new EventQueryResult(ordered, truncated, errors);
    }, ct);

    /// <summary>Bir günlükteki kritik / hata / uyarı sayıları (iletiler biçimlendirilmez).</summary>
    public Task<EventCounts> CountAsync(string log, TimeSpan period, CancellationToken ct) => Task.Run(() =>
    {
        int c = 0, e = 0, w = 0, total = 0;
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, XPath([1, 2, 3], period)) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            {
                using (r)
                {
                    ct.ThrowIfCancellationRequested();
                    switch (r.Level)
                    {
                        case 1: c++; break;
                        case 2: e++; break;
                        case 3: w++; break;
                    }
                }
                if (++total >= CountCap) return new EventCounts(c, e, w, true, null);
            }
            return new EventCounts(c, e, w, false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new EventCounts(0, 0, 0, false, Describe(ex));
        }
    }, ct);

    /// <summary>XPath sorgusundaki kayıtları (en yeni önce) iletileriyle birlikte okur. Çökme analizi de bunu kullanır.</summary>
    internal static IEnumerable<EventEntry> Read(string log, string xpath, CancellationToken ct)
    {
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
        {
            ct.ThrowIfCancellationRequested();
            using (r) yield return ToEntry(log, r);
        }
    }

    internal static IEnumerable<EventRecord> ReadRecords(string log, string xpath, CancellationToken ct)
    {
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
        {
            ct.ThrowIfCancellationRequested();
            yield return r; // çağıran Dispose eder
        }
    }

    internal static EventEntry ToEntry(string log, EventRecord r) =>
        new(r.TimeCreated, log, r.ProviderName ?? "—", r.Id, r.Level ?? 0, Message(r));

    /// <summary>Windows'un ileti metni; sağlayıcı metni bulunamazsa olayın ham verisi (uydurma açıklama yok).</summary>
    internal static string Message(EventRecord r)
    {
        try
        {
            var text = r.FormatDescription();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch (EventLogException)
        {
            // ileti kaynağı yüklenemedi – aşağıda ham veri
        }
        var props = r.Properties?.Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
        return props.Count == 0
            ? "(Windows bu olay için ileti metni bulamadı; olay verisi yok.)"
            : "(Windows bu olay için ileti metni bulamadı.) Olay verisi: " + string.Join(" · ", props);
    }

    internal static string Describe(Exception ex) => ex switch
    {
        EventLogNotFoundException => "Günlük bu sistemde bulunamadı.",
        UnauthorizedAccessException or EventLogReadingException { HResult: unchecked((int)0x80070005) } => "Erişim reddedildi (yönetici yetkisi gerekebilir).",
        _ => ex.Message.Trim()
    };
}

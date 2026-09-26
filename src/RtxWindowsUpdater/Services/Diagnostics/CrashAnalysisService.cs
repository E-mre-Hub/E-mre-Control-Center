using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

public enum CrashKind { BugCheck, KernelPower, UnexpectedShutdown, DisplayReset, Hardware }

/// <summary>Olay günlüğünden okunan tek çökme / beklenmedik kapanma kaydı (alanlar olayın kendi verisinden).</summary>
public sealed record CrashEvent(
    DateTime? Time,
    CrashKind Kind,
    string Provider,
    int EventId,
    uint? BugCheckCode,
    string? DumpPath,
    string? Module,
    string Message)
{
    public string TimeText => Formats.Date(Time);
    public string KindText => Kind switch
    {
        CrashKind.BugCheck => "Mavi ekran (BugCheck)",
        CrashKind.KernelPower => BugCheckCode is > 0 ? "Beklenmedik yeniden başlatma (hata denetimi)" : "Beklenmedik güç kaybı / kilitlenme",
        CrashKind.UnexpectedShutdown => "Beklenmedik kapanma",
        CrashKind.DisplayReset => "Ekran sürücüsü yanıt vermedi (kurtarıldı)",
        _ => "Donanım hatası (WHEA)"
    };
    public string CodeText => BugCheckCode is { } c and > 0 ? $"0x{c:X8} {BugCheckNames.Name(c)}" : "—";
    public string RelationText => BugCheckCode is { } c and > 0 ? BugCheckNames.Relation(c)
        : Kind switch
        {
            CrashKind.DisplayReset => Module is null ? "Ekran kartı sürücüsüyle ilişkili olabilir." : $"{Module} (ekran sürücüsü) ile ilişkili olabilir.",
            CrashKind.KernelPower => "Hata kodu yok: güç kesintisi, güç düğmesiyle kapatma veya donanım kilitlenmesiyle ilişkili olabilir.",
            CrashKind.Hardware => "Donanım (CPU / bellek / PCIe aygıtı) hata bildirdi; ayrıntı olay iletisinde.",
            _ => "Windows kapanma nedenini kaydedemedi."
        };
    public string ModuleText => Module ?? "—";
    public string DumpText => DumpPath ?? "—";
}

/// <summary>Minidump dosyası; kod / parametreler dosya başlığından okunur (okunamazsa neden).</summary>
public sealed record DumpFileInfo(string Path, DateTime Time, long Size, uint? BugCheckCode, IReadOnlyList<ulong> Parameters, string? Error)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string TimeText => Formats.Date(Time);
    public string SizeText => Formats.Bytes(Size);
    public string CodeText => BugCheckCode is { } c ? $"0x{c:X8} {BugCheckNames.Name(c)}" : Error ?? "—";
    public string ParametersText => Parameters.Count == 0 ? "—" : string.Join(", ", Parameters.Select(p => $"0x{p:X}"));
    public string RelationText => BugCheckCode is { } c ? BugCheckNames.Relation(c) : "—";
}

public sealed record CrashReport(
    IReadOnlyList<CrashEvent> Events,
    IReadOnlyList<DumpFileInfo> Dumps,
    string? DumpNote,
    bool FullDumpExists,
    long? FullDumpSize,
    string? EventError,
    TimeSpan Period)
{
    public int BugChecks => Events.Count(e => e.Kind == CrashKind.BugCheck);
    public int Unexpected => Events.Count(e => e.Kind is CrashKind.UnexpectedShutdown or CrashKind.KernelPower);
    public int DisplayResets => Events.Count(e => e.Kind == CrashKind.DisplayReset);
    public int Hardware => Events.Count(e => e.Kind == CrashKind.Hardware);
}

/// <summary>
/// Çökme analizi: Sistem günlüğündeki BugCheck (WER 1001), Kernel-Power 41, EventLog 6008, Display 4101 ve WHEA olayları + minidump
/// başlığı (hata denetimi kodu ve 4 parametre). Kesin neden belirtilmez: sürücü adı yalnızca olayın kendisi bildiriyorsa (Display 4101)
/// yazılır, hata kodundan çıkarılan bileşen "ilişkili olabilir" diye sunulur. Tam yığın analizi için dump WinDbg ile incelenmelidir.
/// </summary>
public sealed class CrashAnalysisService(Logger logger)
{
    private static readonly Regex HexCode = new(@"0x([0-9a-fA-F]{1,8})\b", RegexOptions.Compiled);

    internal static readonly string CrashXPathFilter =
        "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
        "(Provider[@Name='BugCheck'] and EventID=1001) or " +
        "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
        "(Provider[@Name='EventLog'] and EventID=6008) or " +
        "(Provider[@Name='Display'] and EventID=4101) or " +
        "(Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1 or EventID=18 or EventID=19 or EventID=47))";

    public Task<CrashReport> AnalyzeAsync(TimeSpan period, bool readDumps, CancellationToken ct) => Task.Run(() =>
    {
        var events = new List<CrashEvent>();
        string? eventError = null;
        try
        {
            var xpath = $"*[System[({CrashXPathFilter}) and TimeCreated[timediff(@SystemTime) <= {(long)period.TotalMilliseconds}]]]";
            foreach (var r in EventLogService.ReadRecords("System", xpath, ct))
            {
                using (r)
                {
                    events.Add(ToCrash(r));
                }
                if (events.Count >= 300) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            eventError = "Sistem günlüğü okunamadı: " + EventLogService.Describe(ex);
        }

        var dumps = new List<DumpFileInfo>();
        string? dumpNote = null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (readDumps)
        {
            var dir = Path.Combine(windows, "Minidump");
            try
            {
                if (!Directory.Exists(dir))
                    dumpNote = "Minidump klasörü yok (bu sistemde küçük bellek dökümü oluşmamış).";
                else
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).Take(50))
                        dumps.Add(ReadDump(f));
            }
            catch (UnauthorizedAccessException)
            {
                dumpNote = "Minidump klasörü (" + dir + ") yalnızca yönetici yetkisiyle okunabilir.";
            }
            catch (IOException ex)
            {
                dumpNote = "Minidump klasörü okunamadı: " + ex.Message;
            }
        }
        else
        {
            dumpNote = "Minidump klasörü yalnızca yönetici yetkisiyle okunabilir.";
        }

        var full = new FileInfo(Path.Combine(windows, "MEMORY.DMP"));
        bool fullExists;
        long? fullSize = null;
        try
        {
            fullExists = full.Exists;
            if (fullExists) fullSize = full.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fullExists = false;
        }

        var report = new CrashReport(events, dumps, dumpNote, fullExists, fullSize, eventError, period);
        logger.Info($"Çökme analizi ({period.TotalDays:0} gün): {report.BugChecks} BugCheck, {report.Unexpected} beklenmedik kapanma kaydı, " +
                    $"{report.DisplayResets} ekran sürücüsü sıfırlama, {report.Hardware} WHEA, {dumps.Count} minidump" +
                    (eventError is null ? "" : "; " + eventError) + (dumpNote is null ? "." : "; " + dumpNote));
        return report;
    }, ct);

    private static CrashEvent ToCrash(EventRecord r)
    {
        var provider = r.ProviderName ?? "—";
        var props = r.Properties?.Select(p => p.Value).ToList() ?? [];
        var message = EventLogService.Message(r);
        switch (r.Id)
        {
            case 1001:
            {
                // WER-SystemErrorReporting 1001: param1 = "0x0000009f (0x…, …)", param2 = dump yolu.
                var text = props.Select(p => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList();
                uint? code = null;
                if (text.Count > 0 && HexCode.Match(text[0]) is { Success: true } m) code = Convert.ToUInt32(m.Groups[1].Value, 16);
                else if (HexCode.Match(message) is { Success: true } mm) code = Convert.ToUInt32(mm.Groups[1].Value, 16);
                var dump = text.FirstOrDefault(t => t.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                return new CrashEvent(r.TimeCreated, CrashKind.BugCheck, provider, r.Id, code, dump, null, message);
            }
            case 41:
            {
                uint? code = props.Count > 0 ? ToUInt(props[0]) : null;
                return new CrashEvent(r.TimeCreated, CrashKind.KernelPower, provider, r.Id, code, null, null, message);
            }
            case 6008:
                return new CrashEvent(r.TimeCreated, CrashKind.UnexpectedShutdown, provider, r.Id, null, null, null, message);
            case 4101 when provider == "Display":
            {
                var module = props.Count > 0 ? Convert.ToString(props[0], System.Globalization.CultureInfo.InvariantCulture) : null;
                return new CrashEvent(r.TimeCreated, CrashKind.DisplayReset, provider, r.Id, null, null,
                    string.IsNullOrWhiteSpace(module) ? null : module.Trim(), message);
            }
            default:
                return new CrashEvent(r.TimeCreated, CrashKind.Hardware, provider, r.Id, null, null, null, message);
        }

        static uint? ToUInt(object? v)
        {
            try { return v is null ? null : Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
        }
    }

    /// <summary>
    /// Çekirdek döküm başlığı: 64 bit "PAGEDU64" → hata kodu 0x38, parametreler 0x40..0x58; 32 bit "PAGEDUMP" → kod 0x28, parametreler 0x2C..0x38.
    /// Başka biçim / okunamayan dosya uydurulmaz: Error doldurulur.
    /// </summary>
    internal static DumpFileInfo ReadDump(FileInfo f)
    {
        try
        {
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[0x60];
            if (s.Read(head, 0, head.Length) < head.Length) return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, null, [], "Dosya çok kısa");
            var sig = System.Text.Encoding.ASCII.GetString(head, 0, 8);
            if (sig == "PAGEDU64")
            {
                var code = BitConverter.ToUInt32(head, 0x38);
                var p = new[] { BitConverter.ToUInt64(head, 0x40), BitConverter.ToUInt64(head, 0x48), BitConverter.ToUInt64(head, 0x50), BitConverter.ToUInt64(head, 0x58) };
                return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, code, p, null);
            }
            if (sig == "PAGEDUMP")
            {
                var code = BitConverter.ToUInt32(head, 0x28);
                var p = new ulong[] { BitConverter.ToUInt32(head, 0x2C), BitConverter.ToUInt32(head, 0x30), BitConverter.ToUInt32(head, 0x34), BitConverter.ToUInt32(head, 0x38) };
                return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, code, p, null);
            }
            return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, null, [], "Tanınmayan döküm biçimi");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, null, [], "Okunamadı: " + ex.Message);
        }
    }
}

/// <summary>
/// Microsoft'un yayımladığı hata denetimi (bug check) kodu adları ve kodun genel anlamından çıkan "ilişkili olabilir" açıklaması.
/// Açıklama kesin neden DEĞİLDİR; tabloda olmayan kod için yalnızca kod gösterilir.
/// </summary>
public static class BugCheckNames
{
    private static readonly Dictionary<uint, (string Name, string Relation)> Table = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "Bir çekirdek sürücüsünün geçersiz bellek erişimiyle ilişkili olabilir."),
        [0x0D1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Bir sürücünün geçersiz bellek erişimiyle ilişkili olabilir (sık: ağ / depolama / ekran sürücüleri)."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Bellek (RAM) hatası veya bellek yöneten bir sürücüyle ilişkili olabilir."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "Çekirdek modunda işlenmeyen bir özel durum; bir sürücüyle ilişkili olabilir."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "Sistem hizmeti çağrısında özel durum; ekran / güvenlik yazılımı sürücüleriyle ilişkili olabilir."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Geçersiz bellek adresi; RAM, disk veya bir sürücüyle ilişkili olabilir."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Diskten bellek sayfası okunamadı; disk / kablo / depolama sürücüsüyle ilişkili olabilir."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Sistem iş parçacığında işlenmeyen özel durum; bir sürücüyle ilişkili olabilir."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "İşlemci tuzağı; donanım (RAM / CPU / hız aşırtma) veya sürücüyle ilişkili olabilir."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "Uyku / uyanma sırasında yanıt vermeyen bir sürücüyle ilişkili olabilir."),
        [0xA0] = ("INTERNAL_POWER_ERROR", "Güç yönetimi hatası; güç / ACPI sürücüleriyle ilişkili olabilir."),
        [0xC2] = ("BAD_POOL_CALLER", "Hatalı bellek havuzu isteği; bir sürücüyle ilişkili olabilir."),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Sürücü Doğrulayıcı bir sürücü ihlali yakaladı."),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "Bir sürücünün bellek havuzunu bozmasıyla ilişkili olabilir."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "Kritik bir sistem işlemi sonlandı; sistem dosyaları, disk veya güvenlik yazılımıyla ilişkili olabilir."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "Kritik bir işlem sonlandı; disk / depolama sorunuyla ilişkili olabilir."),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "Çalıştırılamaz belleği çalıştırma girişimi; bir sürücüyle ilişkili olabilir."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "Bir işlemci çekirdeği yanıt vermedi; CPU / hız aşırtma / BIOS ile ilişkili olabilir."),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Çekirdek yapısı bozuldu; bellek veya bir sürücüyle ilişkili olabilir."),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", "Ekran kartı bellek yönetimi hatası; ekran sürücüsüyle ilişkili olabilir."),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", "DirectX çekirdek hatası; ekran sürücüsüyle ilişkili olabilir."),
        [0x116] = ("VIDEO_TDR_FAILURE", "Ekran sürücüsü zaman aşımından kurtarılamadı; ekran kartı / sürücüsüyle ilişkili olabilir."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "Ekran sürücüsü zaman aşımı; ekran kartı / sürücüsüyle ilişkili olabilir."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "Ekran zamanlayıcı hatası; ekran sürücüsüyle ilişkili olabilir."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "Donanımın bildirdiği düzeltilemeyen hata; CPU / RAM / anakart / güç kaynağı / hız aşırtma ile ilişkili olabilir."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "Bir sürücü çok uzun süre işlemciyi tuttu; depolama (SSD ürün yazılımı) / ağ / ekran sürücüleriyle ilişkili olabilir."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Çekirdek veri yapısı bozulması; bir sürücü veya bellekle ilişkili olabilir."),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "Çekirdek yığın bozulması; bir sürücüyle ilişkili olabilir."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "Bellek sıkıştırma deposu hatası; disk veya bellekle ilişkili olabilir."),
        [0x15F] = ("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "Modern bekleme sırasında zaman aşımı; güç yönetimi sürücüleriyle ilişkili olabilir."),
        [0x1D8] = ("SWITCH_TO_DEBUGGER", "Hata ayıklayıcıya geçiş istendi."),
        [0xE2] = ("MANUALLY_INITIATED_CRASH", "Çökme kullanıcı tarafından bilerek başlatıldı (klavye kısayolu / araç)."),
        [0x1E0] = ("MANUALLY_INITIATED_POWER_BUTTON_HOLD", "Güç düğmesine uzun basılarak başlatıldı."),
        [0x19] = ("BAD_POOL_HEADER", "Bellek havuzu başlığı bozuk; bir sürücü veya RAM ile ilişkili olabilir."),
        [0x24] = ("NTFS_FILE_SYSTEM", "NTFS dosya sistemi hatası; disk veya dosya sistemi bozulmasıyla ilişkili olabilir."),
        [0x3D] = ("INTERRUPT_EXCEPTION_NOT_HANDLED", "Kesme işlenemedi; bir aygıt sürücüsüyle ilişkili olabilir."),
        [0x4E] = ("PFN_LIST_CORRUPT", "Bellek sayfa listesi bozuk; RAM veya bir sürücüyle ilişkili olabilir."),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "Önyükleme diskine erişilemedi; depolama denetleyicisi / BIOS ayarıyla ilişkili olabilir."),
        [0xD5] = ("DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "Bir sürücünün serbest bırakılmış belleğe erişmesiyle ilişkili olabilir."),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "Sistem iş parçacığında işlenmeyen özel durum; bir sürücüyle ilişkili olabilir."),
        [0x1000008E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", "Çekirdek modunda işlenmeyen özel durum; bir sürücüyle ilişkili olabilir.")
    };

    public static string Name(uint code) => Table.TryGetValue(code, out var t) ? t.Name : string.Empty;

    public static string Relation(uint code) =>
        Table.TryGetValue(code, out var t) ? t.Relation : "Bu kod için hazır açıklama yok; Microsoft'un hata denetimi kodu başvurusuna bakın.";
}

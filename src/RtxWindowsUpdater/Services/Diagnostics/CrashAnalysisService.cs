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
        CrashKind.BugCheck => L.T("Mavi ekran (BugCheck)", "Blue screen (BugCheck)"),
        CrashKind.KernelPower => BugCheckCode is > 0 ? L.T("Beklenmedik yeniden başlatma (hata denetimi)", "Unexpected restart (bug check)") : L.T("Beklenmedik güç kaybı / kilitlenme", "Unexpected power loss / hang"),
        CrashKind.UnexpectedShutdown => L.T("Beklenmedik kapanma", "Unexpected shutdown"),
        CrashKind.DisplayReset => L.T("Ekran sürücüsü yanıt vermedi (kurtarıldı)", "Display driver stopped responding (recovered)"),
        _ => L.T("Donanım hatası (WHEA)", "Hardware error (WHEA)")
    };
    public string CodeText => BugCheckCode is { } c and > 0 ? $"0x{c:X8} {BugCheckNames.Name(c)}" : "—";
    public string RelationText => BugCheckCode is { } c and > 0 ? BugCheckNames.Relation(c)
        : Kind switch
        {
            CrashKind.DisplayReset => Module is null ? L.T("Ekran kartı sürücüsüyle ilişkili olabilir.", "May be related to the graphics card driver.") : L.T($"{Module} (ekran sürücüsü) ile ilişkili olabilir.", $"May be related to {Module} (display driver)."),
            CrashKind.KernelPower => L.T("Hata kodu yok: güç kesintisi, güç düğmesiyle kapatma veya donanım kilitlenmesiyle ilişkili olabilir.", "No error code: may be related to a power cut, shutting down with the power button or a hardware hang."),
            CrashKind.Hardware => L.T("Donanım (CPU / bellek / PCIe aygıtı) hata bildirdi; ayrıntı olay iletisinde.", "The hardware (CPU / memory / PCIe device) reported an error; details are in the event message."),
            _ => L.T("Windows kapanma nedenini kaydedemedi.", "Windows could not record the shutdown reason.")
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
            eventError = L.T("Sistem günlüğü okunamadı: ", "Could not read the System log: ") + EventLogService.Describe(ex);
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
                    dumpNote = L.T("Minidump klasörü yok (bu sistemde küçük bellek dökümü oluşmamış).", "No Minidump folder (no small memory dump has been created on this system).");
                else
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).Take(50))
                        dumps.Add(ReadDump(f));
            }
            catch (UnauthorizedAccessException)
            {
                dumpNote = L.T("Minidump klasörü (", "The Minidump folder (") + dir + L.T(") yalnızca yönetici yetkisiyle okunabilir.", ") can only be read with administrator rights.");
            }
            catch (IOException ex)
            {
                dumpNote = L.T("Minidump klasörü okunamadı: ", "Could not read the Minidump folder: ") + ex.Message;
            }
        }
        else
        {
            dumpNote = L.T("Minidump klasörü yalnızca yönetici yetkisiyle okunabilir.", "The Minidump folder can only be read with administrator rights.");
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
        logger.Info(L.T($"Çökme analizi ({period.TotalDays:0} gün): {report.BugChecks} BugCheck, {report.Unexpected} beklenmedik kapanma kaydı, ", $"Crash analysis ({period.TotalDays:0} days): {report.BugChecks} BugCheck, {report.Unexpected} unexpected shutdown record(s), ") +
                    L.T($"{report.DisplayResets} ekran sürücüsü sıfırlama, {report.Hardware} WHEA, {dumps.Count} minidump", $"{report.DisplayResets} display driver reset(s), {report.Hardware} WHEA, {dumps.Count} minidump(s)") +
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
            if (s.Read(head, 0, head.Length) < head.Length) return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, null, [], L.T("Dosya çok kısa", "File too short"));
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
            return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, null, [], L.T("Tanınmayan döküm biçimi", "Unrecognized dump format"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DumpFileInfo(f.FullName, f.LastWriteTime, f.Length, null, [], L.T("Okunamadı: ", "Unreadable: ") + ex.Message);
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
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", L.T("Bir çekirdek sürücüsünün geçersiz bellek erişimiyle ilişkili olabilir.", "May be related to invalid memory access by a kernel driver.")),
        [0x0D1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", L.T("Bir sürücünün geçersiz bellek erişimiyle ilişkili olabilir (sık: ağ / depolama / ekran sürücüleri).", "May be related to invalid memory access by a driver (common: network / storage / display drivers).")),
        [0x1A] = ("MEMORY_MANAGEMENT", L.T("Bellek (RAM) hatası veya bellek yöneten bir sürücüyle ilişkili olabilir.", "May be related to a memory (RAM) error or a driver that manages memory.")),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", L.T("Çekirdek modunda işlenmeyen bir özel durum; bir sürücüyle ilişkili olabilir.", "An unhandled exception in kernel mode; may be related to a driver.")),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", L.T("Sistem hizmeti çağrısında özel durum; ekran / güvenlik yazılımı sürücüleriyle ilişkili olabilir.", "Exception in a system service call; may be related to display / security software drivers.")),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", L.T("Geçersiz bellek adresi; RAM, disk veya bir sürücüyle ilişkili olabilir.", "Invalid memory address; may be related to RAM, disk or a driver.")),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", L.T("Diskten bellek sayfası okunamadı; disk / kablo / depolama sürücüsüyle ilişkili olabilir.", "A memory page could not be read from disk; may be related to the disk / cable / storage driver.")),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", L.T("Sistem iş parçacığında işlenmeyen özel durum; bir sürücüyle ilişkili olabilir.", "Unhandled exception in a system thread; may be related to a driver.")),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", L.T("İşlemci tuzağı; donanım (RAM / CPU / hız aşırtma) veya sürücüyle ilişkili olabilir.", "Processor trap; may be related to hardware (RAM / CPU / overclocking) or a driver.")),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", L.T("Uyku / uyanma sırasında yanıt vermeyen bir sürücüyle ilişkili olabilir.", "May be related to a driver that did not respond during sleep / wake.")),
        [0xA0] = ("INTERNAL_POWER_ERROR", L.T("Güç yönetimi hatası; güç / ACPI sürücüleriyle ilişkili olabilir.", "Power management error; may be related to power / ACPI drivers.")),
        [0xC2] = ("BAD_POOL_CALLER", L.T("Hatalı bellek havuzu isteği; bir sürücüyle ilişkili olabilir.", "Bad memory pool request; may be related to a driver.")),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", L.T("Sürücü Doğrulayıcı bir sürücü ihlali yakaladı.", "Driver Verifier caught a driver violation.")),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", L.T("Bir sürücünün bellek havuzunu bozmasıyla ilişkili olabilir.", "May be related to a driver corrupting the memory pool.")),
        [0xEF] = ("CRITICAL_PROCESS_DIED", L.T("Kritik bir sistem işlemi sonlandı; sistem dosyaları, disk veya güvenlik yazılımıyla ilişkili olabilir.", "A critical system process ended; may be related to system files, the disk or security software.")),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", L.T("Kritik bir işlem sonlandı; disk / depolama sorunuyla ilişkili olabilir.", "A critical process ended; may be related to a disk / storage problem.")),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", L.T("Çalıştırılamaz belleği çalıştırma girişimi; bir sürücüyle ilişkili olabilir.", "Attempt to execute non-executable memory; may be related to a driver.")),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", L.T("Bir işlemci çekirdeği yanıt vermedi; CPU / hız aşırtma / BIOS ile ilişkili olabilir.", "A processor core did not respond; may be related to the CPU / overclocking / BIOS.")),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", L.T("Çekirdek yapısı bozuldu; bellek veya bir sürücüyle ilişkili olabilir.", "A kernel structure was corrupted; may be related to memory or a driver.")),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", L.T("Ekran kartı bellek yönetimi hatası; ekran sürücüsüyle ilişkili olabilir.", "Graphics card memory management error; may be related to the display driver.")),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", L.T("DirectX çekirdek hatası; ekran sürücüsüyle ilişkili olabilir.", "DirectX kernel error; may be related to the display driver.")),
        [0x116] = ("VIDEO_TDR_FAILURE", L.T("Ekran sürücüsü zaman aşımından kurtarılamadı; ekran kartı / sürücüsüyle ilişkili olabilir.", "The display driver could not recover from a timeout; may be related to the graphics card / driver.")),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", L.T("Ekran sürücüsü zaman aşımı; ekran kartı / sürücüsüyle ilişkili olabilir.", "Display driver timeout; may be related to the graphics card / driver.")),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", L.T("Ekran zamanlayıcı hatası; ekran sürücüsüyle ilişkili olabilir.", "Display scheduler error; may be related to the display driver.")),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", L.T("Donanımın bildirdiği düzeltilemeyen hata; CPU / RAM / anakart / güç kaynağı / hız aşırtma ile ilişkili olabilir.", "Uncorrectable error reported by the hardware; may be related to the CPU / RAM / motherboard / power supply / overclocking.")),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", L.T("Bir sürücü çok uzun süre işlemciyi tuttu; depolama (SSD ürün yazılımı) / ağ / ekran sürücüleriyle ilişkili olabilir.", "A driver held the processor for too long; may be related to storage (SSD firmware) / network / display drivers.")),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", L.T("Çekirdek veri yapısı bozulması; bir sürücü veya bellekle ilişkili olabilir.", "Kernel data structure corruption; may be related to a driver or memory.")),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", L.T("Çekirdek yığın bozulması; bir sürücüyle ilişkili olabilir.", "Kernel stack corruption; may be related to a driver.")),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", L.T("Bellek sıkıştırma deposu hatası; disk veya bellekle ilişkili olabilir.", "Memory compression store error; may be related to the disk or memory.")),
        [0x15F] = ("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", L.T("Modern bekleme sırasında zaman aşımı; güç yönetimi sürücüleriyle ilişkili olabilir.", "Timeout during modern standby; may be related to power management drivers.")),
        [0x1D8] = ("SWITCH_TO_DEBUGGER", L.T("Hata ayıklayıcıya geçiş istendi.", "A break into the debugger was requested.")),
        [0xE2] = ("MANUALLY_INITIATED_CRASH", L.T("Çökme kullanıcı tarafından bilerek başlatıldı (klavye kısayolu / araç).", "The crash was started deliberately by the user (keyboard shortcut / tool).")),
        [0x1E0] = ("MANUALLY_INITIATED_POWER_BUTTON_HOLD", L.T("Güç düğmesine uzun basılarak başlatıldı.", "Started by a long press of the power button.")),
        [0x19] = ("BAD_POOL_HEADER", L.T("Bellek havuzu başlığı bozuk; bir sürücü veya RAM ile ilişkili olabilir.", "Memory pool header corrupted; may be related to a driver or RAM.")),
        [0x24] = ("NTFS_FILE_SYSTEM", L.T("NTFS dosya sistemi hatası; disk veya dosya sistemi bozulmasıyla ilişkili olabilir.", "NTFS file system error; may be related to disk or file system corruption.")),
        [0x3D] = ("INTERRUPT_EXCEPTION_NOT_HANDLED", L.T("Kesme işlenemedi; bir aygıt sürücüsüyle ilişkili olabilir.", "An interrupt could not be handled; may be related to a device driver.")),
        [0x4E] = ("PFN_LIST_CORRUPT", L.T("Bellek sayfa listesi bozuk; RAM veya bir sürücüyle ilişkili olabilir.", "Memory page list corrupted; may be related to RAM or a driver.")),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", L.T("Önyükleme diskine erişilemedi; depolama denetleyicisi / BIOS ayarıyla ilişkili olabilir.", "The boot disk could not be accessed; may be related to the storage controller / BIOS setting.")),
        [0xD5] = ("DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", L.T("Bir sürücünün serbest bırakılmış belleğe erişmesiyle ilişkili olabilir.", "May be related to a driver accessing freed memory.")),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", L.T("Sistem iş parçacığında işlenmeyen özel durum; bir sürücüyle ilişkili olabilir.", "Unhandled exception in a system thread; may be related to a driver.")),
        [0x1000008E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", L.T("Çekirdek modunda işlenmeyen özel durum; bir sürücüyle ilişkili olabilir.", "Unhandled exception in kernel mode; may be related to a driver."))
    };

    public static string Name(uint code) => Table.TryGetValue(code, out var t) ? t.Name : string.Empty;

    public static string Relation(uint code) =>
        Table.TryGetValue(code, out var t) ? t.Relation : L.T("Bu kod için hazır açıklama yok; Microsoft'un hata denetimi kodu başvurusuna bakın.", "No ready description for this code; see Microsoft's bug check code reference.");
}

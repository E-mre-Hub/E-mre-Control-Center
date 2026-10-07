using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows Sistem Dosyası Denetleyicisi (sfc.exe).
///
/// Kontrol  : sfc /verifyonly  → yalnızca tarar, HİÇBİR onarım yapmaz (Tümünü/Seçilenleri Kontrol Et).
/// Kart     : sfc /scannow     → tarar ve bozuk dosyaları onarmayı dener ("Tarama Başlat").
/// Güncelle : sfc /scannow     → yalnızca kontrolde bütünlük ihlali bulunduysa çalıştırılır.
///
/// sfc.exe çıktısını UTF-16 olarak yazar. Sonuç, sfc.exe'nin kendi mesaj tablosundan Windows'un
/// görüntüleme dilinde yüklenen gerçek mesajlarla karşılaştırılarak belirlenir (uydurma yok).
/// </summary>
public sealed class SfcManager(Logger logger) : IMaintenanceModule, IProgressReportingModule
{
    public string Key => ComponentKeys.Sfc;
    public string DisplayName => L.T("Windows Sistem Dosyası Kontrolü", "Windows System File Check");

    public event Action<ModuleProgress>? ProgressChanged;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(90);
    private static string SfcPath => Path.Combine(Environment.SystemDirectory, "sfc.exe");

    // sfc.exe mesaj kimlikleri (sfc.exe.mui mesaj tablosu)
    private const uint MsgRepairAfterReboot = 0x40001002;
    private const uint MsgMaxQueuedReboot = 0x40001003;
    private const uint MsgMaxRepairedRunAgain = 0x40001004;
    private const uint MsgAdminRequired = 0x40001005;
    private const uint MsgCouldNotPerform = 0x40001007;
    private const uint MsgFoundAndRepaired = 0x40001008;
    private const uint MsgFoundSomeUnfixed = 0x40001009;
    private const uint MsgNoViolations = 0x4000100A;
    private const uint MsgFoundViolations = 0x4000100B;
    private const uint MsgRepairPendingReboot = 0x4000100D;
    private const uint MsgCouldNotStartService = 0x4000100E;
    private const uint MsgVerificationPercent = 0x40001012;
    private const uint MsgAnotherOperation = 0x40001013;
    private const uint MsgNoViolationsMetadataCorrupt = 0x40001015;

    // Windows'un İngilizce metinleri (yerelleştirilmiş metin yüklenemezse veya çıktı İngilizceyse).
    private static readonly Dictionary<uint, string> English = new()
    {
        [MsgRepairAfterReboot] = "The system file repair changes will take effect after the next reboot.",
        [MsgMaxQueuedReboot] = "The maximum number of files have been queued for repair. A reboot is required to complete the repair of these files.",
        [MsgMaxRepairedRunAgain] = "The maximum number of files have been repaired. To repair the remaining corrupted files run sfc again.",
        [MsgAdminRequired] = "You must be an administrator running a console session in order to use the sfc utility.",
        [MsgCouldNotPerform] = "Windows Resource Protection could not perform the requested operation.",
        [MsgFoundAndRepaired] = "Windows Resource Protection found corrupt files and successfully repaired them.",
        [MsgFoundSomeUnfixed] = "Windows Resource Protection found corrupt files but was unable to fix some of them.",
        [MsgNoViolations] = "Windows Resource Protection did not find any integrity violations.",
        [MsgFoundViolations] = "Windows Resource Protection found integrity violations.",
        [MsgRepairPendingReboot] = "There is a system repair pending which requires reboot to complete.",
        [MsgCouldNotStartService] = "Windows Resource Protection could not start the repair service.",
        [MsgVerificationPercent] = "Verification %1!u!%% complete.%0",
        [MsgAnotherOperation] = "Another servicing or repair operation is currently running.",
        [MsgNoViolationsMetadataCorrupt] = "Windows Resource Protection did not find any integrity violations but cannot guarantee the integrity of the system because component metadata is corrupt."
    };

    private static readonly Lazy<Dictionary<uint, string>> Localized = new(() =>
    {
        try { return SystemMessages.LoadMessageTable(SfcPath, English.Keys); }
        catch { return new Dictionary<uint, string>(); }
    });

    public Task<ModuleResult> CheckAsync(CancellationToken ct) => RunSfcAsync(repair: false, ct);

    public Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct) =>
        check.HasActionableUpdates ? RunSfcAsync(repair: true, CancellationToken.None) : Task.FromResult(check);

    /// <summary>Kart butonu: sfc /scannow. Onarım yarıda kesilmesin diye iptal belirteci süreci sonlandırmaz.</summary>
    public Task<ModuleResult> RunActionAsync(CancellationToken ct) => RunSfcAsync(repair: true, CancellationToken.None);

    private async Task<ModuleResult> RunSfcAsync(bool repair, CancellationToken ct)
    {
        var mode = repair ? "/scannow" : "/verifyonly";
        if (!File.Exists(SfcPath))
            return Done(Fail(repair, L.T("sfc.exe bulunamadı: ", "sfc.exe not found: ") + SfcPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Done(AdminRequired());

        logger.Info(repair
            ? L.T("[SFC] Sistem dosyası taraması başlatılıyor (sfc /scannow – bozuk dosyalar onarılmaya çalışılır)...", "[SFC] Starting the system file scan (sfc /scannow – tries to repair corrupt files)...")
            : L.T("[SFC] Sistem dosyası doğrulaması başlatılıyor (sfc /verifyonly – yalnızca tarama, onarım yapılmaz)...", "[SFC] Starting the system file verification (sfc /verifyonly – scan only, no repair)..."));
        logger.Info(L.T("[SFC] Bu işlem 10-30 dakika sürebilir.", "[SFC] This can take 10-30 minutes."));
        Report(repair ? L.T("Tarama başlatılıyor...", "Starting the scan...") : L.T("Doğrulama başlatılıyor...", "Starting the verification..."), 0);

        var percentRegex = SystemMessages.BuildPercentRegex(Message(MsgVerificationPercent)) ??
                           SystemMessages.BuildPercentRegex(English[MsgVerificationPercent]);
        var lastLoggedPercent = -1;
        var lastPercent = -1;

        // cmd.exe üzerinden: cmd.exe /d /s /c ""C:\Windows\System32\sfc.exe" /verifyonly" (çıktı UTF-16 kalır).
        var r = await ProcessRunner.RunCmdAsync(SfcPath, [mode], Timeout, ct,
            onStdOut: line =>
            {
                var clean = line.Replace("\0", string.Empty).Trim();
                if (clean.Length == 0) return;

                var norm = SystemMessages.Normalize(clean);
                var m = percentRegex?.Match(norm);
                if (m is { Success: true } && int.TryParse(m.Groups[1].Value, out var pct))
                {
                    if (pct == lastPercent) return;
                    lastPercent = pct;
                    Report(L.T($"Doğrulama %{pct} tamamlandı", $"Verification {pct}% complete"), pct);
                    if (pct / 10 > lastLoggedPercent / 10 || pct == 100)
                    {
                        lastLoggedPercent = pct;
                        logger.Output($"[SFC] {clean}");
                    }
                    return;
                }
                logger.Output("[SFC] " + clean);
            },
            onStdErr: line =>
            {
                var clean = line.Replace("\0", string.Empty).Trim();
                if (clean.Length > 0) logger.Warning("[SFC] " + clean);
            },
            outputEncoding: Encoding.Unicode);

        if (!r.Started)
            return Done(r.StartErrorCode == 740 ? AdminRequired() : Fail(repair, r.StartError!));
        if (r.TimedOut)
            return Done(Fail(repair, L.T("SFC zaman aşımına uğradı ve sonlandırıldı (90 dk).", "SFC timed out and was ended (90 min).")));
        if (r.Cancelled)
            return Done(new ModuleResult { Key = Key, Status = ComponentStatus.Skipped, Summary = L.T("İptal edildi", "Cancelled"), Reason = L.T("SFC doğrulaması kullanıcı tarafından iptal edildi.", "The SFC verification was cancelled by the user.") });

        logger.Info(L.T($"[SFC] Tarama tamamlandı. Çıkış kodu: {r.ExitCode}", $"[SFC] Scan completed. Exit code: {r.ExitCode}"));
        var output = SystemMessages.Normalize(r.StdOut.Replace("\0", string.Empty));
        return Done(Interpret(output, repair, r));
    }

    private ModuleResult Interpret(string output, bool repair, ProcessResult r)
    {
        bool Has(uint id) => Matches(id, output);
        var reboot = Has(MsgRepairAfterReboot);
        var command = repair ? "sfc /scannow" : "sfc /verifyonly";

        if (Has(MsgAdminRequired))
            return AdminRequired();

        if (Has(MsgRepairPendingReboot))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.RebootRequired,
                Summary = L.T("Bekleyen bir sistem onarımı var", "A system repair is pending"),
                Details = L.T("Windows: yeniden başlatma gerektiren bir sistem onarımı bekliyor.", "Windows: a system repair that requires a restart is pending."),
                Reason = L.T("Bilgisayarı yeniden başlatın ve SFC'yi tekrar çalıştırın.", "Restart the computer and run SFC again."),
                RebootRequired = true
            };

        if (Has(MsgAnotherOperation))
            return Fail(repair, L.T("Başka bir bakım veya onarım işlemi çalışıyor (ör. Windows Update). Bitmesini bekleyip tekrar deneyin.", "Another servicing or repair operation is running (e.g. Windows Update). Wait for it to finish and try again."));

        if (Has(MsgCouldNotStartService))
            return Fail(repair, L.T("Windows Kaynak Koruması onarım hizmetini (TrustedInstaller) başlatamadı.", "Windows Resource Protection could not start the repair service (TrustedInstaller)."));

        if (Has(MsgCouldNotPerform))
            return Fail(repair, L.T("Windows Kaynak Koruması istenen işlemi gerçekleştiremedi.", "Windows Resource Protection could not perform the requested operation."));

        if (Has(MsgFoundSomeUnfixed))
            return new ModuleResult
            {
                Key = Key,
                Status = repair ? ComponentStatus.PartiallyUpdated : ComponentStatus.UpdateAvailable,
                Summary = L.T("Bozuk dosyalar bulundu ancak bazıları onarılamadı", "Corrupt files were found but some of them could not be repaired"),
                Details = L.T("Ayrıntılar: %windir%\\Logs\\CBS\\CBS.log", "Details: %windir%\\Logs\\CBS\\CBS.log"),
                Reason = L.T("Onarılamayan dosyalar için DISM /Online /Cleanup-Image /RestoreHealth ile bileşen deposunun onarılması gerekebilir ", "For files that could not be repaired, the component store may need to be repaired with DISM /Online /Cleanup-Image /RestoreHealth ") +
                         L.T("(Windows Image Sağlık Kontrolü kartı bileşen deposunu \"onarılabilir\" bulursa onayınızla onarır; onaysız çalışmaz). ", "(if the Windows Image Health Check card finds the component store \"repairable\", it repairs it with your approval; never without it). ") +
                         L.T("Onarımdan sonra SFC taramasını tekrarlayın.", "Run the SFC scan again after the repair."),
                RebootRequired = reboot
            };

        if (Has(MsgMaxQueuedReboot))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.RebootRequired,
                Summary = L.T("Onarım sıraya alındı – yeniden başlatma gerekli", "Repair queued – restart required"),
                Reason = L.T("Yeniden başlattıktan sonra kalan dosyalar için SFC'yi tekrar çalıştırın.", "After restarting, run SFC again for the remaining files."),
                RebootRequired = true
            };

        if (Has(MsgMaxRepairedRunAgain))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.PartiallyUpdated,
                Summary = L.T("Dosyaların bir kısmı onarıldı", "Some of the files were repaired"),
                Reason = L.T("Kalan bozuk dosyalar için SFC'yi tekrar çalıştırın.", "Run SFC again for the remaining corrupt files.")
            };

        if (Has(MsgFoundAndRepaired))
            return new ModuleResult
            {
                Key = Key,
                Status = reboot ? ComponentStatus.RebootRequired : ComponentStatus.Updated,
                Summary = L.T("Bozuk dosyalar bulundu ve onarıldı", "Corrupt files were found and repaired"),
                Details = L.T("Ayrıntılar: %windir%\\Logs\\CBS\\CBS.log", "Details: %windir%\\Logs\\CBS\\CBS.log"),
                Reason = reboot ? L.T("Onarımlar bir sonraki yeniden başlatmada etkinleşecek.", "The repairs take effect at the next restart.") : null,
                RebootRequired = reboot
            };

        if (Has(MsgFoundViolations))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpdateAvailable,
                Summary = L.T("Bozuk sistem dosyası bulundu", "Corrupt system files found"),
                Details = L.T("sfc /verifyonly bütünlük ihlali buldu (onarım yapılmadı).\nAyrıntılar: %windir%\\Logs\\CBS\\CBS.log", "sfc /verifyonly found integrity violations (no repair was made).\nDetails: %windir%\\Logs\\CBS\\CBS.log"),
                Reason = L.T("Onarmak için kartın \"Tarama Başlat\" butonunu (sfc /scannow) veya güncelleme butonlarını kullanın.", "To repair, use the card's \"Start Scan\" button (sfc /scannow) or the update buttons."),
                ActionableCount = 1
            };

        if (Has(MsgNoViolationsMetadataCorrupt))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Attention,
                Summary = L.T("Dikkat: İhlal yok, ancak bileşen meta verisi bozuk", "Attention: No violations, but component metadata is corrupt"),
                Reason = L.T("Windows, sistem bütünlüğünü garanti edemediğini bildirdi. DISM /Online /Cleanup-Image /RestoreHealth önerilir ", "Windows reported that it cannot guarantee system integrity. DISM /Online /Cleanup-Image /RestoreHealth is recommended ") +
                         L.T("(Windows Image Sağlık Kontrolü kartı \"onarılabilir\" bulursa onayınızla onarır; onaysız çalışmaz).", "(if the Windows Image Health Check card finds it \"repairable\", it repairs it with your approval; never without it).")
            };

        if (Has(MsgNoViolations))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpToDate,
                Summary = repair ? L.T("Tarama başarılı – bozuk dosya bulunamadı", "Scan succeeded – no corrupt files found") : L.T("Bozuk dosya bulunamadı", "No corrupt files found"),
                Details = L.T($"Son işlem: {command}", $"Last operation: {command}")
            };

        // Bilinen hiçbir Windows mesajı eşleşmedi: sonucu uydurma, gerçek çıktıyı göster.
        var last = ProcessRunner.LastMeaningfulLine(r.StdOut.Replace("\0", string.Empty)) ?? L.T("(çıktı yok)", "(no output)");
        return Fail(repair, L.T($"SFC sonucu yorumlanamadı (çıkış kodu {r.ExitCode}). Windows'un son mesajı: {last}", $"The SFC result could not be interpreted (exit code {r.ExitCode}). Windows' last message: {last}"));
    }

    private static string? Message(uint id) =>
        Localized.Value.TryGetValue(id, out var s) ? s : null;

    private static bool Matches(uint id, string normalizedOutput)
    {
        foreach (var text in new[] { Message(id), English[id] })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var key = SystemMessages.FirstSentenceKey(text);
            if (key.Length >= 12 && normalizedOutput.Contains(key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private ModuleResult Done(ModuleResult result)
    {
        LogResult(logger, "SFC", result);
        return result;
    }

    private ModuleResult AdminRequired()
    {
        return new ModuleResult
        {
            Key = Key,
            Status = ComponentStatus.AdminRequired,
            Summary = L.T("Yönetici izni gerekli", "Administrator permission required"),
            Reason = L.T("SFC yalnızca yönetici yetkisiyle çalışır.", "SFC only runs with administrator rights.")
        };
    }

    private ModuleResult Fail(bool repair, string reason)
    {
        return new ModuleResult
        {
            Key = Key,
            Status = repair ? ComponentStatus.Failed : ComponentStatus.CheckFailed,
            Summary = L.T("İşlem başarısız", "Operation failed"),
            Reason = reason
        };
    }

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }

    /// <summary>Sonucu durumuna uygun renkte günlüğe yazar (bakım modülleri ortak kullanır).</summary>
    internal static void LogResult(Logger logger, string tag, ModuleResult result)
    {
        var text = L.T($"[{tag}] Sonuç: {result.Summary}", $"[{tag}] Result: {result.Summary}") + (string.IsNullOrWhiteSpace(result.Reason) ? "" : $" – {result.Reason}");
        switch (result.Status)
        {
            case ComponentStatus.UpToDate or ComponentStatus.Updated:
                logger.Success(text);
                break;
            case ComponentStatus.CheckFailed or ComponentStatus.Failed or ComponentStatus.AdminRequired:
                logger.Error(text);
                break;
            default:
                logger.Warning(text);
                break;
        }
    }
}

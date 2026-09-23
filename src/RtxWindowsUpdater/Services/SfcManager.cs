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
    public string DisplayName => "Windows Sistem Dosyası Kontrolü";

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
            return Done(Fail(repair, "sfc.exe bulunamadı: " + SfcPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Done(AdminRequired());

        logger.Info(repair
            ? "[SFC] Sistem dosyası taraması başlatılıyor (sfc /scannow – bozuk dosyalar onarılmaya çalışılır)..."
            : "[SFC] Sistem dosyası doğrulaması başlatılıyor (sfc /verifyonly – yalnızca tarama, onarım yapılmaz)...");
        logger.Info("[SFC] Bu işlem 10-30 dakika sürebilir.");
        Report(repair ? "Tarama başlatılıyor..." : "Doğrulama başlatılıyor...", 0);

        var percentRegex = SystemMessages.BuildPercentRegex(Message(MsgVerificationPercent)) ??
                           SystemMessages.BuildPercentRegex(English[MsgVerificationPercent]);
        var lastLoggedPercent = -1;
        var lastPercent = -1;

        var r = await ProcessRunner.RunAsync(SfcPath, mode, Timeout, ct,
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
                    Report($"Doğrulama %{pct} tamamlandı", pct);
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
            return Done(Fail(repair, "SFC zaman aşımına uğradı ve sonlandırıldı (90 dk)."));
        if (r.Cancelled)
            return Done(new ModuleResult { Key = Key, Status = ComponentStatus.Skipped, Summary = "İptal edildi", Reason = "SFC doğrulaması kullanıcı tarafından iptal edildi." });

        logger.Info($"[SFC] Tarama tamamlandı. Çıkış kodu: {r.ExitCode}");
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
                Summary = "Bekleyen bir sistem onarımı var",
                Details = "Windows: yeniden başlatma gerektiren bir sistem onarımı bekliyor.",
                Reason = "Bilgisayarı yeniden başlatın ve SFC'yi tekrar çalıştırın.",
                RebootRequired = true
            };

        if (Has(MsgAnotherOperation))
            return Fail(repair, "Başka bir bakım veya onarım işlemi çalışıyor (ör. Windows Update). Bitmesini bekleyip tekrar deneyin.");

        if (Has(MsgCouldNotStartService))
            return Fail(repair, "Windows Kaynak Koruması onarım hizmetini (TrustedInstaller) başlatamadı.");

        if (Has(MsgCouldNotPerform))
            return Fail(repair, "Windows Kaynak Koruması istenen işlemi gerçekleştiremedi.");

        if (Has(MsgFoundSomeUnfixed))
            return new ModuleResult
            {
                Key = Key,
                Status = repair ? ComponentStatus.PartiallyUpdated : ComponentStatus.UpdateAvailable,
                Summary = "Bozuk dosyalar bulundu ancak bazıları onarılamadı",
                Details = "Ayrıntılar: %windir%\\Logs\\CBS\\CBS.log",
                Reason = "Onarılamayan dosyalar için DISM /Online /Cleanup-Image /RestoreHealth ile bileşen deposunun onarılması gerekebilir (uygulama bunu otomatik çalıştırmaz).",
                RebootRequired = reboot
            };

        if (Has(MsgMaxQueuedReboot))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.RebootRequired,
                Summary = "Onarım sıraya alındı – yeniden başlatma gerekli",
                Reason = "Yeniden başlattıktan sonra kalan dosyalar için SFC'yi tekrar çalıştırın.",
                RebootRequired = true
            };

        if (Has(MsgMaxRepairedRunAgain))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.PartiallyUpdated,
                Summary = "Dosyaların bir kısmı onarıldı",
                Reason = "Kalan bozuk dosyalar için SFC'yi tekrar çalıştırın."
            };

        if (Has(MsgFoundAndRepaired))
            return new ModuleResult
            {
                Key = Key,
                Status = reboot ? ComponentStatus.RebootRequired : ComponentStatus.Updated,
                Summary = "Bozuk dosyalar bulundu ve onarıldı",
                Details = "Ayrıntılar: %windir%\\Logs\\CBS\\CBS.log",
                Reason = reboot ? "Onarımlar bir sonraki yeniden başlatmada etkinleşecek." : null,
                RebootRequired = reboot
            };

        if (Has(MsgFoundViolations))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpdateAvailable,
                Summary = "Bozuk sistem dosyası bulundu",
                Details = "sfc /verifyonly bütünlük ihlali buldu (onarım yapılmadı).\nAyrıntılar: %windir%\\Logs\\CBS\\CBS.log",
                Reason = "Onarmak için kartın \"Tarama Başlat\" butonunu (sfc /scannow) veya güncelleme butonlarını kullanın.",
                ActionableCount = 1
            };

        if (Has(MsgNoViolationsMetadataCorrupt))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Attention,
                Summary = "İhlal yok, ancak bileşen meta verisi bozuk",
                Reason = "Windows, sistem bütünlüğünü garanti edemediğini bildirdi. DISM /Online /Cleanup-Image /RestoreHealth önerilir (uygulama bunu otomatik çalıştırmaz)."
            };

        if (Has(MsgNoViolations))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpToDate,
                Summary = repair ? "Tarama başarılı – bozuk dosya bulunamadı" : "Bozuk dosya bulunamadı",
                Details = $"Son işlem: {command}"
            };

        // Bilinen hiçbir Windows mesajı eşleşmedi: sonucu uydurma, gerçek çıktıyı göster.
        var last = ProcessRunner.LastMeaningfulLine(r.StdOut.Replace("\0", string.Empty)) ?? "(çıktı yok)";
        return Fail(repair, $"SFC sonucu yorumlanamadı (çıkış kodu {r.ExitCode}). Windows'un son mesajı: {last}");
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
            Summary = "Yönetici izni gerekli",
            Reason = "SFC yalnızca yönetici yetkisiyle çalışır."
        };
    }

    private ModuleResult Fail(bool repair, string reason)
    {
        return new ModuleResult
        {
            Key = Key,
            Status = repair ? ComponentStatus.Failed : ComponentStatus.CheckFailed,
            Summary = "İşlem başarısız",
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
        var text = $"[{tag}] Sonuç: {result.Summary}" + (string.IsNullOrWhiteSpace(result.Reason) ? "" : $" – {result.Reason}");
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

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows görüntüsü (bileşen deposu / component store) sağlık kontrolü ve onayla onarımı.
///
/// Kontrol (kart butonu ve "Kontrol Et" akışları): DISM /Online /Cleanup-Image /CheckHealth – yalnızca okur.
/// Onarım (yalnızca kontrol "onarılabilir" dediyse VE kullanıcı "Güncelle / Onar" onayı verdiyse):
///     DISM /Online /Cleanup-Image /RestoreHealth
///   ardından sonuç GERÇEKTEN doğrulanır: /CheckHealth yeniden çalıştırılır; bileşen deposu sağlıklı değilse
///   onarım başarılı gösterilmez. Onarım başladıktan sonra yarıda kesilmez.
/// Çıktının Windows dilinden bağımsız okunabilmesi için DISM'in kendi /English görüntüleme seçeneği eklenir.
/// Kontrol sırasında hiçbir onarım yapılmaz; RestoreHealth kullanıcı onayı olmadan asla çalışmaz.
/// </summary>
public sealed class DismManager(Logger logger) : IMaintenanceModule, IProgressReportingModule
{
    public string Key => ComponentKeys.Dism;
    public string DisplayName => L.T("Windows Image Sağlık Kontrolü", "Windows Image Health Check");

    public event Action<ModuleProgress>? ProgressChanged;

    public const string DisplayCommand = "DISM /Online /Cleanup-Image /CheckHealth";
    public const string RepairCommand = "DISM /Online /Cleanup-Image /RestoreHealth";
    private static readonly string[] CheckArguments = ["/English", "/Online", "/Cleanup-Image", "/CheckHealth"];
    private static readonly string[] RepairArguments = ["/English", "/Online", "/Cleanup-Image", "/RestoreHealth"];
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromHours(3);
    private static string DismPath => Path.Combine(Environment.SystemDirectory, "Dism.exe");

    // CbsProvider.dll.mui dizge tablosundaki (330/331/332) gerçek DISM sonuç metinleri.
    private const string Healthy = "No component store corruption detected.";
    private const string Repairable = "The component store is repairable.";
    private const string NotRepairable = "The component store cannot be repaired.";
    private const string RestoreCompleted = "The restore operation completed successfully.";

    private const int SuccessRebootRequired = 3010;

    private static readonly Regex ProgressRegex = new(@"(\d{1,3}(?:\.\d)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex ErrorRegex = new(@"^Error:\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Sık görülen DISM onarım hataları (CBS_E_*); açıklama DISM'in kendi mesajıyla birlikte gösterilir.</summary>
    private static readonly Dictionary<uint, string> KnownErrors = new()
    {
        [0x800F081F] = L.T("CBS_E_SOURCE_MISSING – onarım için gereken kaynak dosyalar bulunamadı (Windows Update'ten alınamadı).", "CBS_E_SOURCE_MISSING – the source files needed for the repair were not found (could not be obtained from Windows Update)."),
        [0x800F0906] = L.T("CBS_E_DOWNLOAD_FAILURE – onarım dosyaları indirilemedi (internet bağlantısını / Windows Update erişimini kontrol edin).", "CBS_E_DOWNLOAD_FAILURE – the repair files could not be downloaded (check the internet connection / Windows Update access)."),
        [0x800F0907] = L.T("CBS_E_GROUPPOLICY_DISALLOWED – grup ilkesi onarım dosyalarının Windows Update'ten indirilmesine izin vermiyor.", "CBS_E_GROUPPOLICY_DISALLOWED – group policy does not allow downloading repair files from Windows Update."),
        [0x800F082F] = L.T("CBS_E_PENDING – bekleyen bir Windows işlemi var; bilgisayarı yeniden başlatıp tekrar deneyin.", "CBS_E_PENDING – a Windows operation is pending; restart the computer and try again."),
        [0x80070005] = L.T("Erişim reddedildi.", "Access denied."),
        [0x800704C7] = L.T("İşlem iptal edildi.", "Operation cancelled.")
    };

    public Task<ModuleResult> CheckAsync(CancellationToken ct) => RunCheckHealthAsync(ct, logResult: true);

    public Task<ModuleResult> RunActionAsync(CancellationToken ct) => RunCheckHealthAsync(ct, logResult: true);

    /// <summary>
    /// Kontrol "onarılabilir" dediyse (ve kullanıcı onay verdiyse) RestoreHealth ile onarır, ardından CheckHealth ile doğrular.
    /// </summary>
    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        if (!check.HasActionableUpdates) return check;
        if (!File.Exists(DismPath))
            return Done(RepairFailed(L.T("Dism.exe bulunamadı: ", "Dism.exe not found: ") + DismPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Done(AdminRequired());

        logger.Info(L.T($"[DISM] Windows bileşen deposu onarılıyor ({RepairCommand})...", $"[DISM] Repairing the Windows component store ({RepairCommand})..."));
        logger.Info(L.T("[DISM] Bu işlem 10-60 dakika sürebilir; onarım dosyaları Windows Update'ten indirilebilir. Onarım yarıda kesilmez.", "[DISM] This can take 10-60 minutes; repair files may be downloaded from Windows Update. The repair is not interrupted."));
        Report(L.T("Onarılıyor...", "Repairing..."), null);

        // Onarım başladıktan sonra iptal edilmez (yarıda kalan onarım bileşen deposunu tutarsız bırakabilir).
        // DISM bu sırada onarım dosyalarını Windows Update'ten indirir ve yüzde uzun süre (%62-65 civarı) değişmeyebilir;
        // uygulamanın donmadığı ve onay beklemediği anlaşılsın diye dakikada bir GERÇEK geçen süre bildirilir.
        Volatile.Write(ref _lastPercent, -1);
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = RepairHeartbeatAsync(DateTime.Now, heartbeatCts.Token);
        ProcessResult r;
        try
        {
            r = await RunDismAsync(RepairArguments, RepairTimeout, CancellationToken.None, L.T("Onarım", "Repair"));
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
        }

        if (!r.Started)
            return Done(r.StartErrorCode == 740 ? AdminRequired() : RepairFailed(r.StartError!));
        if (r.TimedOut)
            return Done(RepairFailed(L.T($"DISM onarımı {RepairTimeout.TotalHours:0} saat içinde tamamlanmadı ve sonlandırıldı.", $"The DISM repair did not finish within {RepairTimeout.TotalHours:0} hours and was ended.")));

        logger.Info(L.T($"[DISM] Onarım komutu tamamlandı. Çıkış kodu: {r.ExitCode} ({r.ExitCodeHex})", $"[DISM] Repair command finished. Exit code: {r.ExitCode} ({r.ExitCodeHex})"));
        var output = r.StdOut + "\n" + r.StdErr;
        if (r.ExitCode == 740 || output.Contains("Elevated permissions are required", StringComparison.OrdinalIgnoreCase))
            return Done(AdminRequired());

        if (r.ExitCode != 0 && r.ExitCode != SuccessRebootRequired)
            return Done(RepairFailed(DescribeError(r, output)));

        var rebootRequired = r.ExitCode == SuccessRebootRequired ||
                             output.Contains("restart", StringComparison.OrdinalIgnoreCase) && output.Contains("required", StringComparison.OrdinalIgnoreCase);
        var reportedSuccess = output.Contains(RestoreCompleted, StringComparison.OrdinalIgnoreCase);
        ExecutionTrace.Note(L.T("DISM RestoreHealth: ", "DISM RestoreHealth: ") + (reportedSuccess ? RestoreCompleted : ProcessRunner.LastMeaningfulLine(r.StdOut) ?? L.T("(çıktı yok)", "(no output)")));

        // DISM'in "başarılı" mesajı doğrudan kabul edilmez: bileşen deposu yeniden gerçekten kontrol edilir.
        logger.Info(L.T("[DISM] Onarım sonrası doğrulama: ", "[DISM] Verification after repair: ") + DisplayCommand + "...");
        Report(L.T("Onarım doğrulanıyor...", "Verifying the repair..."), null);
        var verify = await RunCheckHealthAsync(CancellationToken.None, logResult: false);

        ModuleResult result;
        if (verify.Status == ComponentStatus.UpToDate)
        {
            result = new ModuleResult
            {
                Key = Key,
                Status = rebootRequired ? ComponentStatus.RebootRequired : ComponentStatus.Updated,
                Summary = rebootRequired ? L.T("Onarıldı – yeniden başlatma gerekli", "Repaired – restart required") : L.T("Onarıldı (doğrulandı)", "Repaired (verified)"),
                Details = L.T($"RestoreHealth: {(reportedSuccess ? RestoreCompleted : "çıkış kodu " + r.ExitCode)}\nDoğrulama (CheckHealth): {Healthy}", $"RestoreHealth: {(reportedSuccess ? RestoreCompleted : "exit code " + r.ExitCode)}\nVerification (CheckHealth): {Healthy}"),
                Reason = L.T("Bileşen deposu onarıldı. Windows Sistem Dosyası Kontrolü'nü (SFC) çalıştırmanız önerilir.", "The component store was repaired. Running Windows System File Check (SFC) is recommended.") +
                         (rebootRequired ? L.T(" Onarımın tamamlanması için yeniden başlatma gerekiyor.", " A restart is required to complete the repair.") : string.Empty),
                RebootRequired = rebootRequired,
                Items = [new UpdateItem
                {
                    Name = L.T("Windows bileşen deposu", "Windows component store"), CurrentVersion = L.T("Onarılabilir", "Repairable"), NewVersion = L.T("Sağlıklı", "Healthy"),
                    StatusText = L.T("Onarıldı – CheckHealth ile doğrulandı", "Repaired – verified with CheckHealth"), Outcome = ItemOutcome.Updated, OutcomeText = L.T("Onarıldı", "Repaired"),
                    ResultCode = r.ExitCodeHex
                }]
            };
        }
        else
        {
            var why = verify.Status switch
            {
                ComponentStatus.UpdateAvailable => L.T("DISM onarımın tamamlandığını bildirdi ancak doğrulamada bileşen deposu hâlâ onarılabilir durumda.", "DISM reported that the repair finished, but the verification shows the component store is still repairable."),
                ComponentStatus.Failed => L.T("DISM onarımdan sonra bileşen deposunun onarılamaz durumda olduğunu bildirdi. Windows'un onarım yüklemesi (yerinde yükseltme) gerekebilir.", "After the repair DISM reported that the component store cannot be repaired. A Windows repair install (in-place upgrade) may be needed."),
                _ => L.T("Onarım sonrası doğrulama yapılamadı: ", "Could not verify after the repair: ") + (verify.Reason ?? verify.Summary)
            };
            result = new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Failed,
                Summary = L.T("Onarım doğrulanamadı", "Repair could not be verified"),
                Details = L.T($"RestoreHealth: {(reportedSuccess ? RestoreCompleted : "çıkış kodu " + r.ExitCode)}\nDoğrulama (CheckHealth): {verify.Summary}", $"RestoreHealth: {(reportedSuccess ? RestoreCompleted : "exit code " + r.ExitCode)}\nVerification (CheckHealth): {verify.Summary}"),
                Reason = why,
                Items = [new UpdateItem
                {
                    Name = L.T("Windows bileşen deposu", "Windows component store"), CurrentVersion = L.T("Onarılabilir", "Repairable"), NewVersion = verify.Summary,
                    StatusText = why, Outcome = ItemOutcome.Failed, OutcomeText = L.T("Onarım doğrulanamadı", "Repair could not be verified"), ResultCode = r.ExitCodeHex
                }]
            };
        }
        return Done(result);
    }

    // ------------------------------------------------------------------ CheckHealth

    private async Task<ModuleResult> RunCheckHealthAsync(CancellationToken ct, bool logResult)
    {
        ModuleResult Finish(ModuleResult m) => logResult ? Done(m) : m;

        if (!File.Exists(DismPath))
            return Finish(CheckFailed(L.T("Dism.exe bulunamadı: ", "Dism.exe not found: ") + DismPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Finish(AdminRequired());

        logger.Info(L.T("[DISM] Windows image kontrol ediliyor (", "[DISM] Checking the Windows image (") + DisplayCommand + ")...");
        Report(L.T("Kontrol ediliyor...", "Checking..."), null);

        var r = await RunDismAsync(CheckArguments, CheckTimeout, ct, L.T("Kontrol", "Check"));

        if (!r.Started)
            return Finish(r.StartErrorCode == 740 ? AdminRequired() : CheckFailed(r.StartError!));
        if (r.TimedOut)
            return Finish(CheckFailed(L.T("DISM zaman aşımına uğradı ve sonlandırıldı (15 dk).", "DISM timed out and was ended (15 min).")));
        if (r.Cancelled)
            return Finish(new ModuleResult { Key = Key, Status = ComponentStatus.Skipped, Summary = L.T("İptal edildi", "Cancelled"), Reason = L.T("DISM kontrolü kullanıcı tarafından iptal edildi.", "The DISM check was cancelled by the user.") });

        logger.Info(L.T($"[DISM] Kontrol tamamlandı. Çıkış kodu: {r.ExitCode}", $"[DISM] Check finished. Exit code: {r.ExitCode}"));
        return Finish(InterpretCheckHealth(r));
    }

    /// <summary>CheckHealth çıktısını ve çıkış kodunu GERÇEK DISM metinleriyle yorumlar.</summary>
    internal ModuleResult InterpretCheckHealth(ProcessResult r)
    {
        var output = r.StdOut + "\n" + r.StdErr;
        if (r.ExitCode == 740 || output.Contains("Elevated permissions are required", StringComparison.OrdinalIgnoreCase))
            return AdminRequired();

        if (r.ExitCode != 0)
            return CheckFailed(DescribeError(r, output));

        if (output.Contains(NotRepairable, StringComparison.OrdinalIgnoreCase))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Failed,
                Summary = L.T("Bozulma tespit edildi (onarılamaz)", "Corruption detected (not repairable)"),
                Details = "DISM: " + NotRepairable,
                Reason = L.T("Windows bileşen deposu onarılamaz durumda. Windows'un onarım yüklemesi (yerinde yükseltme) gerekebilir.", "The Windows component store cannot be repaired. A Windows repair install (in-place upgrade) may be needed.")
            };

        if (output.Contains(Repairable, StringComparison.OrdinalIgnoreCase))
            return new ModuleResult
            {
                Key = Key,
                // Başarılı DEĞİL: işlem gerektiren (onarılabilir) durum. Onarım yalnızca kullanıcı onayıyla yapılır.
                Status = ComponentStatus.UpdateAvailable,
                ActionableCount = 1,
                Summary = L.T("Dikkat: Onarılabilir durumda", "Attention: Repairable"),
                Details = "DISM: " + Repairable,
                Reason = L.T("Windows bileşen deposunda onarılabilir bozulma bulundu. Onayınızla ", "Repairable corruption was found in the Windows component store. With your approval it is repaired by running ") + RepairCommand +
                         L.T(" çalıştırılarak onarılır (\"Tümünü Güncelle\", \"Seçilenleri Çalıştır\" veya kartın onay penceresi).", " (\"Update All\", \"Run Selected\" or the card's confirmation window)."),
                Items = [new UpdateItem
                {
                    Name = L.T("Windows bileşen deposu", "Windows component store"), CurrentVersion = L.T("Onarılabilir", "Repairable"), NewVersion = L.T("RestoreHealth ile onarım", "Repair with RestoreHealth"),
                    UpdateAvailable = true, StatusText = L.T("Onarılabilir – onay bekliyor", "Repairable – waiting for approval")
                }]
            };

        if (output.Contains(Healthy, StringComparison.OrdinalIgnoreCase))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpToDate,
                Summary = L.T("Sağlıklı", "Healthy"),
                Details = "DISM: " + Healthy
            };

        var last = ProcessRunner.LastMeaningfulLine(r.StdOut) ?? L.T("(çıktı yok)", "(no output)");
        return CheckFailed(L.T($"DISM tamamlandı ancak sonuç yorumlanamadı. DISM'in son mesajı: {last}", $"DISM finished but the result could not be interpreted. DISM's last message: {last}"));
    }

    private static string DescribeError(ProcessResult r, string output)
    {
        var err = output.Split('\n').Select(l => ErrorRegex.Match(l.Trim())).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var msg = ProcessRunner.LastMeaningfulLine(output) ?? L.T("(çıktı yok)", "(no output)");
        var known = KnownErrors.TryGetValue(unchecked((uint)r.ExitCode), out var k) ? " " + k : string.Empty;
        return L.T($"DISM hata ile sonlandı (çıkış kodu {r.ExitCode} / {r.ExitCodeHex}{(err is null ? "" : ", hata " + err)}).{known} DISM: {msg}", $"DISM ended with an error (exit code {r.ExitCode} / {r.ExitCodeHex}{(err is null ? "" : ", error " + err)}).{known} DISM: {msg}");
    }

    // ------------------------------------------------------------------ çalıştırma

    /// <summary>DISM'in bildirdiği son tam yüzde (-1: henüz bildirilmedi).</summary>
    private int _lastPercent = -1;

    private async Task RepairHeartbeatAsync(DateTime started, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                var minutes = (int)(DateTime.Now - started).TotalMinutes;
                var pct = Volatile.Read(ref _lastPercent);
                logger.Info(L.T($"[DISM] Onarım sürüyor... ({minutes} dk{(pct >= 0 ? $", DISM'in bildirdiği son ilerleme %{pct}" : string.Empty)}). ", $"[DISM] Repair in progress... ({minutes} min{(pct >= 0 ? $", last progress reported by DISM {pct}%" : string.Empty)}). ") +
                            L.T("Onay beklenmiyor; DISM onarım dosyalarını Windows Update'ten indirirken yüzde uzun süre aynı kalabilir.", "No approval is awaited; while DISM downloads repair files from Windows Update, the percentage may stay the same for a long time."));
                Report(pct >= 0 ? L.T($"Onarım %{pct} · {minutes} dk sürüyor", $"Repair {pct}% · running for {minutes} min") : L.T($"Onarım sürüyor · {minutes} dk", $"Repair running · {minutes} min"), pct >= 0 ? pct : null);
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task<ProcessResult> RunDismAsync(string[] args, TimeSpan timeout, CancellationToken ct, string phase)
    {
        var lastPercent = -1;
        return ProcessRunner.RunCmdAsync(DismPath, args, timeout, ct,
            onStdOut: line =>
            {
                var clean = line.Trim();
                if (clean.Length == 0) return;
                if (clean.StartsWith('[') || (clean.Contains('%') && clean.Contains('=')))
                {
                    var m = ProgressRegex.Match(clean);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    {
                        var whole = (int)pct;
                        if (whole != lastPercent)
                        {
                            lastPercent = whole;
                            Volatile.Write(ref _lastPercent, whole);
                            Report(L.T($"{phase} %{whole}", $"{phase} {whole}%"), pct);
                        }
                    }
                    return;
                }
                logger.Output("[DISM] " + clean);
            },
            onStdErr: line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) logger.Warning("[DISM] " + line.Trim());
            });
    }

    private ModuleResult Done(ModuleResult result)
    {
        SfcManager.LogResult(logger, "DISM", result);
        return result;
    }

    private ModuleResult AdminRequired() => new()
    {
        Key = Key,
        Status = ComponentStatus.AdminRequired,
        Summary = L.T("Yönetici izni gerekli", "Administrator permission required"),
        Reason = L.T("DISM yalnızca yönetici yetkisiyle çalışır (hata 740).", "DISM only runs with administrator rights (error 740).")
    };

    private ModuleResult CheckFailed(string reason) => new()
    {
        Key = Key,
        Status = ComponentStatus.CheckFailed,
        Summary = L.T("Kontrol başarısız", "Check failed"),
        Reason = reason
    };

    private ModuleResult RepairFailed(string reason) => new()
    {
        Key = Key,
        Status = ComponentStatus.Failed,
        Summary = L.T("Onarım başarısız", "Repair failed"),
        Reason = reason,
        Items = [new UpdateItem
        {
            Name = L.T("Windows bileşen deposu", "Windows component store"), CurrentVersion = L.T("Onarılabilir", "Repairable"), NewVersion = "—",
            StatusText = reason, Outcome = ItemOutcome.Failed, OutcomeText = L.T("Onarım başarısız", "Repair failed")
        }]
    };

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }
}

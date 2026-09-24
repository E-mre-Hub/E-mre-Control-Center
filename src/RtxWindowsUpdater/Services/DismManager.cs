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
    public string DisplayName => "Windows Image Sağlık Kontrolü";

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
        [0x800F081F] = "CBS_E_SOURCE_MISSING – onarım için gereken kaynak dosyalar bulunamadı (Windows Update'ten alınamadı).",
        [0x800F0906] = "CBS_E_DOWNLOAD_FAILURE – onarım dosyaları indirilemedi (internet bağlantısını / Windows Update erişimini kontrol edin).",
        [0x800F0907] = "CBS_E_GROUPPOLICY_DISALLOWED – grup ilkesi onarım dosyalarının Windows Update'ten indirilmesine izin vermiyor.",
        [0x800F082F] = "CBS_E_PENDING – bekleyen bir Windows işlemi var; bilgisayarı yeniden başlatıp tekrar deneyin.",
        [0x80070005] = "Erişim reddedildi.",
        [0x800704C7] = "İşlem iptal edildi."
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
            return Done(RepairFailed("Dism.exe bulunamadı: " + DismPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Done(AdminRequired());

        logger.Info($"[DISM] Windows bileşen deposu onarılıyor ({RepairCommand})...");
        logger.Info("[DISM] Bu işlem 10-60 dakika sürebilir; onarım dosyaları Windows Update'ten indirilebilir. Onarım yarıda kesilmez.");
        Report("Onarılıyor...", null);

        // Onarım başladıktan sonra iptal edilmez (yarıda kalan onarım bileşen deposunu tutarsız bırakabilir).
        // DISM bu sırada onarım dosyalarını Windows Update'ten indirir ve yüzde uzun süre (%62-65 civarı) değişmeyebilir;
        // uygulamanın donmadığı ve onay beklemediği anlaşılsın diye dakikada bir GERÇEK geçen süre bildirilir.
        Volatile.Write(ref _lastPercent, -1);
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = RepairHeartbeatAsync(DateTime.Now, heartbeatCts.Token);
        ProcessResult r;
        try
        {
            r = await RunDismAsync(RepairArguments, RepairTimeout, CancellationToken.None, "Onarım");
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
        }

        if (!r.Started)
            return Done(r.StartErrorCode == 740 ? AdminRequired() : RepairFailed(r.StartError!));
        if (r.TimedOut)
            return Done(RepairFailed($"DISM onarımı {RepairTimeout.TotalHours:0} saat içinde tamamlanmadı ve sonlandırıldı."));

        logger.Info($"[DISM] Onarım komutu tamamlandı. Çıkış kodu: {r.ExitCode} ({r.ExitCodeHex})");
        var output = r.StdOut + "\n" + r.StdErr;
        if (r.ExitCode == 740 || output.Contains("Elevated permissions are required", StringComparison.OrdinalIgnoreCase))
            return Done(AdminRequired());

        if (r.ExitCode != 0 && r.ExitCode != SuccessRebootRequired)
            return Done(RepairFailed(DescribeError(r, output)));

        var rebootRequired = r.ExitCode == SuccessRebootRequired ||
                             output.Contains("restart", StringComparison.OrdinalIgnoreCase) && output.Contains("required", StringComparison.OrdinalIgnoreCase);
        var reportedSuccess = output.Contains(RestoreCompleted, StringComparison.OrdinalIgnoreCase);
        ExecutionTrace.Note("DISM RestoreHealth: " + (reportedSuccess ? RestoreCompleted : ProcessRunner.LastMeaningfulLine(r.StdOut) ?? "(çıktı yok)"));

        // DISM'in "başarılı" mesajı doğrudan kabul edilmez: bileşen deposu yeniden gerçekten kontrol edilir.
        logger.Info("[DISM] Onarım sonrası doğrulama: " + DisplayCommand + "...");
        Report("Onarım doğrulanıyor...", null);
        var verify = await RunCheckHealthAsync(CancellationToken.None, logResult: false);

        ModuleResult result;
        if (verify.Status == ComponentStatus.UpToDate)
        {
            result = new ModuleResult
            {
                Key = Key,
                Status = rebootRequired ? ComponentStatus.RebootRequired : ComponentStatus.Updated,
                Summary = rebootRequired ? "Onarıldı – yeniden başlatma gerekli" : "Onarıldı (doğrulandı)",
                Details = $"RestoreHealth: {(reportedSuccess ? RestoreCompleted : "çıkış kodu " + r.ExitCode)}\nDoğrulama (CheckHealth): {Healthy}",
                Reason = "Bileşen deposu onarıldı. Windows Sistem Dosyası Kontrolü'nü (SFC) çalıştırmanız önerilir." +
                         (rebootRequired ? " Onarımın tamamlanması için yeniden başlatma gerekiyor." : string.Empty),
                RebootRequired = rebootRequired,
                Items = [new UpdateItem
                {
                    Name = "Windows bileşen deposu", CurrentVersion = "Onarılabilir", NewVersion = "Sağlıklı",
                    StatusText = "Onarıldı – CheckHealth ile doğrulandı", Outcome = ItemOutcome.Updated, OutcomeText = "Onarıldı",
                    ResultCode = r.ExitCodeHex
                }]
            };
        }
        else
        {
            var why = verify.Status switch
            {
                ComponentStatus.UpdateAvailable => "DISM onarımın tamamlandığını bildirdi ancak doğrulamada bileşen deposu hâlâ onarılabilir durumda.",
                ComponentStatus.Failed => "DISM onarımdan sonra bileşen deposunun onarılamaz durumda olduğunu bildirdi. Windows'un onarım yüklemesi (yerinde yükseltme) gerekebilir.",
                _ => "Onarım sonrası doğrulama yapılamadı: " + (verify.Reason ?? verify.Summary)
            };
            result = new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Failed,
                Summary = "Onarım doğrulanamadı",
                Details = $"RestoreHealth: {(reportedSuccess ? RestoreCompleted : "çıkış kodu " + r.ExitCode)}\nDoğrulama (CheckHealth): {verify.Summary}",
                Reason = why,
                Items = [new UpdateItem
                {
                    Name = "Windows bileşen deposu", CurrentVersion = "Onarılabilir", NewVersion = verify.Summary,
                    StatusText = why, Outcome = ItemOutcome.Failed, OutcomeText = "Onarım doğrulanamadı", ResultCode = r.ExitCodeHex
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
            return Finish(CheckFailed("Dism.exe bulunamadı: " + DismPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Finish(AdminRequired());

        logger.Info("[DISM] Windows image kontrol ediliyor (" + DisplayCommand + ")...");
        Report("Kontrol ediliyor...", null);

        var r = await RunDismAsync(CheckArguments, CheckTimeout, ct, "Kontrol");

        if (!r.Started)
            return Finish(r.StartErrorCode == 740 ? AdminRequired() : CheckFailed(r.StartError!));
        if (r.TimedOut)
            return Finish(CheckFailed("DISM zaman aşımına uğradı ve sonlandırıldı (15 dk)."));
        if (r.Cancelled)
            return Finish(new ModuleResult { Key = Key, Status = ComponentStatus.Skipped, Summary = "İptal edildi", Reason = "DISM kontrolü kullanıcı tarafından iptal edildi." });

        logger.Info($"[DISM] Kontrol tamamlandı. Çıkış kodu: {r.ExitCode}");
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
                Summary = "Bozulma tespit edildi (onarılamaz)",
                Details = "DISM: " + NotRepairable,
                Reason = "Windows bileşen deposu onarılamaz durumda. Windows'un onarım yüklemesi (yerinde yükseltme) gerekebilir."
            };

        if (output.Contains(Repairable, StringComparison.OrdinalIgnoreCase))
            return new ModuleResult
            {
                Key = Key,
                // Başarılı DEĞİL: işlem gerektiren (onarılabilir) durum. Onarım yalnızca kullanıcı onayıyla yapılır.
                Status = ComponentStatus.UpdateAvailable,
                ActionableCount = 1,
                Summary = "Dikkat: Onarılabilir durumda",
                Details = "DISM: " + Repairable,
                Reason = "Windows bileşen deposunda onarılabilir bozulma bulundu. Onayınızla " + RepairCommand +
                         " çalıştırılarak onarılır (\"Tümünü Güncelle\", \"Seçilenleri Çalıştır\" veya kartın onay penceresi).",
                Items = [new UpdateItem
                {
                    Name = "Windows bileşen deposu", CurrentVersion = "Onarılabilir", NewVersion = "RestoreHealth ile onarım",
                    UpdateAvailable = true, StatusText = "Onarılabilir – onay bekliyor"
                }]
            };

        if (output.Contains(Healthy, StringComparison.OrdinalIgnoreCase))
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpToDate,
                Summary = "Sağlıklı",
                Details = "DISM: " + Healthy
            };

        var last = ProcessRunner.LastMeaningfulLine(r.StdOut) ?? "(çıktı yok)";
        return CheckFailed($"DISM tamamlandı ancak sonuç yorumlanamadı. DISM'in son mesajı: {last}");
    }

    private static string DescribeError(ProcessResult r, string output)
    {
        var err = output.Split('\n').Select(l => ErrorRegex.Match(l.Trim())).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var msg = ProcessRunner.LastMeaningfulLine(output) ?? "(çıktı yok)";
        var known = KnownErrors.TryGetValue(unchecked((uint)r.ExitCode), out var k) ? " " + k : string.Empty;
        return $"DISM hata ile sonlandı (çıkış kodu {r.ExitCode} / {r.ExitCodeHex}{(err is null ? "" : ", hata " + err)}).{known} DISM: {msg}";
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
                logger.Info($"[DISM] Onarım sürüyor... ({minutes} dk{(pct >= 0 ? $", DISM'in bildirdiği son ilerleme %{pct}" : string.Empty)}). " +
                            "Onay beklenmiyor; DISM onarım dosyalarını Windows Update'ten indirirken yüzde uzun süre aynı kalabilir.");
                Report(pct >= 0 ? $"Onarım %{pct} · {minutes} dk sürüyor" : $"Onarım sürüyor · {minutes} dk", pct >= 0 ? pct : null);
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
                            Report($"{phase} %{whole}", pct);
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
        Summary = "Yönetici izni gerekli",
        Reason = "DISM yalnızca yönetici yetkisiyle çalışır (hata 740)."
    };

    private ModuleResult CheckFailed(string reason) => new()
    {
        Key = Key,
        Status = ComponentStatus.CheckFailed,
        Summary = "Kontrol başarısız",
        Reason = reason
    };

    private ModuleResult RepairFailed(string reason) => new()
    {
        Key = Key,
        Status = ComponentStatus.Failed,
        Summary = "Onarım başarısız",
        Reason = reason,
        Items = [new UpdateItem
        {
            Name = "Windows bileşen deposu", CurrentVersion = "Onarılabilir", NewVersion = "—",
            StatusText = reason, Outcome = ItemOutcome.Failed, OutcomeText = "Onarım başarısız"
        }]
    };

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }
}

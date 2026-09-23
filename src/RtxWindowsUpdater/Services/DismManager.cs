using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows görüntüsü (component store) sağlık kontrolü.
///
/// Çalıştırılan TEK komut: DISM /Online /Cleanup-Image /CheckHealth
/// (Çıktının Windows dilinden bağımsız ve güvenilir okunabilmesi için DISM'in kendi /English
///  görüntüleme seçeneği eklenir; işlem aynıdır ve yalnızca okuma yapar.)
///
/// /RestoreHealth, /ScanHealth veya başka bir onarım komutu bu uygulama tarafından ASLA çalıştırılmaz.
/// Bu yüzden bu modülün "güncelleme" adımı yoktur; sonuç yalnızca raporlanır.
/// </summary>
public sealed class DismManager(Logger logger) : IMaintenanceModule, IProgressReportingModule
{
    public string Key => ComponentKeys.Dism;
    public string DisplayName => "Windows Image Sağlık Kontrolü";

    public event Action<ModuleProgress>? ProgressChanged;

    public const string DisplayCommand = "DISM /Online /Cleanup-Image /CheckHealth";
    private const string Arguments = "/English /Online /Cleanup-Image /CheckHealth";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private static string DismPath => Path.Combine(Environment.SystemDirectory, "Dism.exe");

    // CbsProvider.dll.mui dizge tablosundaki (330/331/332) gerçek DISM sonuç metinleri.
    private const string Healthy = "No component store corruption detected.";
    private const string Repairable = "The component store is repairable.";
    private const string NotRepairable = "The component store cannot be repaired.";

    private static readonly Regex ProgressRegex = new(@"(\d{1,3}(?:\.\d)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex ErrorRegex = new(@"^Error:\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<ModuleResult> CheckAsync(CancellationToken ct) => RunCheckHealthAsync(ct);

    /// <summary>DISM kartında onarım yapılmaz; kontrol sonucu aynen döndürülür.</summary>
    public Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct) => Task.FromResult(check);

    public Task<ModuleResult> RunActionAsync(CancellationToken ct) => RunCheckHealthAsync(ct);

    private async Task<ModuleResult> RunCheckHealthAsync(CancellationToken ct)
    {
        if (!File.Exists(DismPath))
            return Done(Failed("Dism.exe bulunamadı: " + DismPath));
        if (!AdminPrivilegeManager.IsElevated)
            return Done(AdminRequired());

        logger.Info("[DISM] Windows image kontrol ediliyor (" + DisplayCommand + ")...");
        Report("Kontrol ediliyor...", null);

        var lastPercent = -1;
        var r = await ProcessRunner.RunAsync(DismPath, Arguments, Timeout, ct,
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
                            Report($"Kontrol %{whole}", pct);
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

        if (!r.Started)
            return Done(r.StartErrorCode == 740 ? AdminRequired() : Failed(r.StartError!));
        if (r.TimedOut)
            return Done(Failed("DISM zaman aşımına uğradı ve sonlandırıldı (15 dk)."));
        if (r.Cancelled)
            return Done(new ModuleResult { Key = Key, Status = ComponentStatus.Skipped, Summary = "İptal edildi", Reason = "DISM kontrolü kullanıcı tarafından iptal edildi." });

        logger.Info($"[DISM] Kontrol tamamlandı. Çıkış kodu: {r.ExitCode}");

        var output = r.StdOut + "\n" + r.StdErr;
        if (r.ExitCode == 740 || output.Contains("Elevated permissions are required", StringComparison.OrdinalIgnoreCase))
            return Done(AdminRequired());

        if (r.ExitCode != 0)
        {
            var err = output.Split('\n').Select(l => ErrorRegex.Match(l.Trim())).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            var msg = ProcessRunner.LastMeaningfulLine(output) ?? "(çıktı yok)";
            return Done(Failed($"DISM hata ile sonlandı (çıkış kodu {r.ExitCode}{(err is null ? "" : ", hata " + err)}): {msg}"));
        }

        if (output.Contains(NotRepairable, StringComparison.OrdinalIgnoreCase))
            return Done(new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Failed,
                Summary = "Bozulma tespit edildi (onarılamaz)",
                Details = "DISM: " + NotRepairable,
                Reason = "Windows bileşen deposu onarılamaz durumda. Windows'un onarım yüklemesi (yerinde yükseltme) gerekebilir."
            });

        if (output.Contains(Repairable, StringComparison.OrdinalIgnoreCase))
            return Done(new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.Attention,
                Summary = "Onarılabilir durumda",
                Details = "DISM: " + Repairable,
                Reason = "Bileşen deposunda bozulma işaretlenmiş ve onarılabilir. Onarım için DISM /Online /Cleanup-Image /RestoreHealth gerekir; bu uygulama onarımı sizin isteğiniz olmadan çalıştırmaz."
            });

        if (output.Contains(Healthy, StringComparison.OrdinalIgnoreCase))
            return Done(new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpToDate,
                Summary = "Sağlıklı",
                Details = "DISM: " + Healthy
            });

        var last = ProcessRunner.LastMeaningfulLine(r.StdOut) ?? "(çıktı yok)";
        return Done(Failed($"DISM tamamlandı ancak sonuç yorumlanamadı. DISM'in son mesajı: {last}"));
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

    private ModuleResult Failed(string reason) => new()
    {
        Key = Key,
        Status = ComponentStatus.CheckFailed,
        Summary = "Kontrol başarısız",
        Reason = reason
    };

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }
}

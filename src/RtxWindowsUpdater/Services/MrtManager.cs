using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Microsoft Windows Kötü Amaçlı Yazılım Temizleme Aracı (MRT.exe) – yalnızca HIZLI TARAMA.
///
/// Kontrol / kart butonu : MRT.exe /Q /N  → sessiz hızlı tarama, yalnızca TESPİT (dosyalara dokunulmaz)
/// Güncelle (onaylı)     : MRT.exe /Q     → sessiz hızlı tarama + tespit edilenleri temizleme
///                         (yalnızca tespit taramasında tehdit bulunduysa ve kullanıcı onayladıysa)
/// Tam tarama (/F) hiçbir zaman başlatılmaz.
///
/// Sonuç, MRT'nin kendi günlüğüne (%windir%\debug\mrt.log) bu çalıştırma için eklediği bölümdeki
/// "Results Summary" ve "Return code" satırlarından okunur; dönüş kodları Microsoft KB891716'ya göre yorumlanır.
/// </summary>
public sealed class MrtManager(Logger logger) : IMaintenanceModule, IProgressReportingModule
{
    public string Key => ComponentKeys.Mrt;
    public string DisplayName => "Microsoft Kötü Amaçlı Yazılım Temizleme Aracı";

    public event Action<ModuleProgress>? ProgressChanged;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);
    private static string MrtPath => Path.Combine(Environment.SystemDirectory, "MRT.exe");
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "debug", "mrt.log");

    private static readonly Regex ReturnCodeRegex = new(@"Return code:\s*(\d+)", RegexOptions.IgnoreCase);

    public Task<ModuleResult> CheckAsync(CancellationToken ct) => RunMrtAsync(detectOnly: true, ct);

    public Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct) =>
        check.HasActionableUpdates ? RunMrtAsync(detectOnly: false, CancellationToken.None) : Task.FromResult(check);

    /// <summary>Kart butonu: hızlı tarama (yalnızca tespit). Temizlik ayrıca onay ister.</summary>
    public Task<ModuleResult> RunActionAsync(CancellationToken ct) => RunMrtAsync(detectOnly: true, ct);

    private async Task<ModuleResult> RunMrtAsync(bool detectOnly, CancellationToken ct)
    {
        if (!File.Exists(MrtPath))
            return Done(Failed(detectOnly,
                "MRT.exe bu sistemde bulunamadı. Araç, Windows Update üzerinden (KB890830) aylık olarak kurulur."));
        if (!AdminPrivilegeManager.IsElevated)
            return Done(AdminRequired());

        var version = FileVersionInfo.GetVersionInfo(MrtPath).FileVersion?.Split(' ')[0] ?? "?";
        logger.Info($"[MRT] Microsoft Windows Kötü Amaçlı Yazılım Temizleme Aracı başlatılıyor (sürüm {version})...");
        logger.Info(detectOnly
            ? "[MRT] Hızlı tarama başlatılıyor (MRT /Q /N – yalnızca tespit, dosyalara dokunulmaz)..."
            : "[MRT] Hızlı tarama temizleme modunda başlatılıyor (MRT /Q – tespit edilen tehditler kaldırılır)...");
        Report("Hızlı tarama sürüyor...", null);

        var logOffset = GetLogLength();
        var started = DateTime.Now;

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(started, heartbeatCts.Token);

        var r = await ProcessRunner.RunAsync(MrtPath, detectOnly ? "/Q /N" : "/Q", Timeout, ct);

        // MRT tarama motorunu ayrı bir alt süreçte çalıştırabilir; günlüğe sonuç yazılana kadar bekle.
        string? block = null;
        if (r.Started && !r.TimedOut && !r.Cancelled)
            block = await WaitForLogBlockAsync(logOffset, started, ct);

        heartbeatCts.Cancel();
        try { await heartbeat; } catch (OperationCanceledException) { }

        if (!r.Started)
            return Done(r.StartErrorCode == 740 ? AdminRequired() : Failed(detectOnly, r.StartError!));
        if (r.TimedOut)
            return Done(Failed(detectOnly, "MRT zaman aşımına uğradı ve sonlandırıldı (60 dk)."));
        if (r.Cancelled || ct.IsCancellationRequested)
            return Done(new ModuleResult { Key = Key, Status = ComponentStatus.Skipped, Summary = "İptal edildi", Reason = "MRT taraması kullanıcı tarafından iptal edildi." });

        var elapsed = DateTime.Now - started;
        logger.Info($"[MRT] Tarama tamamlandı ({elapsed.TotalMinutes:0.0} dk). Süreç çıkış kodu: {r.ExitCode}");

        int? code = null;
        var summaryLines = new List<string>();
        if (block is not null)
        {
            var m = ReturnCodeRegex.Match(block);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var c)) code = c;
            summaryLines = ExtractSummary(block);
            foreach (var line in summaryLines)
                logger.Output("[MRT] " + line);
        }
        else
        {
            logger.Warning("[MRT] Bu taramaya ait sonuç mrt.log dosyasında bulunamadı; süreç çıkış kodu kullanılıyor.");
        }

        code ??= r.ExitCode;
        logger.Info($"[MRT] Dönüş kodu: {code}");
        return Done(Interpret(code.Value, detectOnly, summaryLines, version));
    }

    private ModuleResult Interpret(int code, bool detectOnly, List<string> summary, string version)
    {
        var summaryText = summary.Count > 0 ? string.Join("\n", summary.Take(6)) : string.Empty;
        var details = $"MRT sürümü: {version}\nMod: {(detectOnly ? "Hızlı tarama (yalnızca tespit)" : "Hızlı tarama (temizleme)")}" +
                      (summaryText.Length > 0 ? "\nMRT: " + summaryText : string.Empty);

        ModuleResult R(ComponentStatus s, string sum, string? reason = null, int actionable = 0, bool reboot = false) => new()
        {
            Key = Key,
            Status = s,
            Summary = sum,
            Details = details,
            Reason = reason,
            ActionableCount = actionable,
            RebootRequired = reboot,
            Items =
            [
                new UpdateItem
                {
                    Name = "MRT hızlı tarama",
                    CurrentVersion = version,
                    UpdateAvailable = actionable > 0,
                    AutoUpdatable = actionable > 0,
                    StatusText = sum
                }
            ]
        };

        return code switch
        {
            0 => R(ComponentStatus.UpToDate, "Tehdit bulunamadı"),
            6 when detectOnly => R(ComponentStatus.UpdateAvailable, "Tehdit tespit edildi",
                "Tespit edilen tehditler henüz temizlenmedi. Temizlik yalnızca onayınızla yapılır.", actionable: 1),
            6 => R(ComponentStatus.Failed, "Tehdit tespit edildi ancak temizlenemedi",
                "MRT tehdidi tespit etti fakat kaldıramadı. Microsoft Defender ile tam tarama önerilir."),
            7 => R(ComponentStatus.Updated, "Tehdit tespit edildi ve temizlendi"),
            8 => R(ComponentStatus.PartiallyUpdated, "Temizlendi – elle yapılması gereken adımlar var",
                "Tam temizlik için ek adımlar gerekiyor; ayrıntılar %windir%\\debug\\mrt.log dosyasında."),
            9 => R(ComponentStatus.PartiallyUpdated, "Temizlendi – elle adım gerekli ve hatalar oluştu",
                "Ayrıntılar %windir%\\debug\\mrt.log dosyasında."),
            10 => R(ComponentStatus.RebootRequired, "Temizlendi – yeniden başlatma gerekli",
                "Tam temizlik için bilgisayarın yeniden başlatılması gerekiyor.", reboot: true),
            11 => R(ComponentStatus.RebootRequired, "Temizlendi – yeniden başlatma gerekli (hatalarla)",
                "Tam temizlik için yeniden başlatma gerekiyor; bazı hatalar oluştu.", reboot: true),
            12 or 13 => R(ComponentStatus.RebootRequired, "Temizlendi – elle adım ve yeniden başlatma gerekli",
                "Ayrıntılar %windir%\\debug\\mrt.log dosyasında.", reboot: true),
            1 => R(detectOnly ? ComponentStatus.CheckFailed : ComponentStatus.Failed, "Tarama başarısız", "MRT: işletim sistemi ortam hatası (kod 1)."),
            2 => R(ComponentStatus.AdminRequired, "Yönetici izni gerekli", "MRT yönetici olarak çalışmadı (kod 2)."),
            3 => R(detectOnly ? ComponentStatus.CheckFailed : ComponentStatus.Failed, "Tarama başarısız", "MRT: desteklenmeyen işletim sistemi (kod 3)."),
            4 => R(detectOnly ? ComponentStatus.CheckFailed : ComponentStatus.Failed, "Tarama başarısız",
                "MRT tarayıcısı başlatılamadı (kod 4). Windows Update ile MRT'nin güncel sürümünü alın."),
            _ => R(detectOnly ? ComponentStatus.CheckFailed : ComponentStatus.Failed, "Tarama başarısız",
                $"MRT bilinmeyen bir dönüş kodu verdi: {code}.")
        };
    }

    /// <summary>mrt.log'daki "Results Summary:" bölümünün satırlarını döndürür.</summary>
    private static List<string> ExtractSummary(string block)
    {
        var lines = block.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindLastIndex(l => l.StartsWith("Results Summary", StringComparison.OrdinalIgnoreCase));
        var result = new List<string>();
        if (start < 0) return result;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Length == 0 || l.All(c => c == '-')) continue;
            if (l.StartsWith("Return code", StringComparison.OrdinalIgnoreCase)) break;
            if (l.StartsWith("Microsoft Windows Malicious Software Removal Tool Finished", StringComparison.OrdinalIgnoreCase)) break;
            result.Add(l);
            if (result.Count >= 20) break;
        }
        return result;
    }

    private async Task<string?> WaitForLogBlockAsync(long offset, DateTime started, CancellationToken ct)
    {
        var deadline = started + Timeout;
        var idleSince = (DateTime?)null;
        while (DateTime.Now < deadline && !ct.IsCancellationRequested)
        {
            var text = ReadLogFrom(offset);
            if (text is not null && ReturnCodeRegex.IsMatch(text))
            {
                // Bu çalıştırmanın bloğu: son "reboot-mode:" başlığından itibaren.
                var idx = text.LastIndexOf("reboot-mode:", StringComparison.OrdinalIgnoreCase);
                return idx >= 0 ? text[idx..] : text;
            }

            var running = IsMrtRunning();
            if (!running)
            {
                idleSince ??= DateTime.Now;
                if (DateTime.Now - idleSince > TimeSpan.FromSeconds(15)) return null;
            }
            else
            {
                idleSince = null;
            }

            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static bool IsMrtRunning()
    {
        foreach (var name in new[] { "MRT", "mrtstub" })
        {
            var procs = Process.GetProcessesByName(name);
            var any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    private static long GetLogLength()
    {
        try { return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0; }
        catch { return 0; }
    }

    /// <summary>mrt.log (UTF-16) dosyasının belirtilen konumdan sonraki kısmını okur.</summary>
    private static string? ReadLogFrom(long offset)
    {
        try
        {
            if (!File.Exists(LogPath)) return null;
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) offset = 0; // günlük döndürülmüş / yeniden oluşturulmuş
            if (offset % 2 == 1) offset--;
            fs.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[fs.Length - offset];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
            return Encoding.Unicode.GetString(bytes, 0, read - read % 2).TrimStart('﻿');
        }
        catch
        {
            return null;
        }
    }

    private async Task HeartbeatAsync(DateTime started, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                var minutes = (int)(DateTime.Now - started).TotalMinutes;
                logger.Info($"[MRT] Tarama devam ediyor... ({minutes} dk)");
                Report($"Hızlı tarama sürüyor... ({minutes} dk)", null);
            }
        }
        catch (OperationCanceledException) { }
    }

    private ModuleResult Done(ModuleResult result)
    {
        SfcManager.LogResult(logger, "MRT", result);
        return result;
    }

    private ModuleResult AdminRequired() => new()
    {
        Key = Key,
        Status = ComponentStatus.AdminRequired,
        Summary = "Yönetici izni gerekli",
        Reason = "MRT yalnızca yönetici yetkisiyle çalışır."
    };

    private ModuleResult Failed(bool detectOnly, string reason) => new()
    {
        Key = Key,
        Status = detectOnly ? ComponentStatus.CheckFailed : ComponentStatus.Failed,
        Summary = "Tarama başarısız",
        Reason = reason
    };

    private void Report(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(Key, text, percent)); } catch { /* UI bildirimi */ }
    }
}

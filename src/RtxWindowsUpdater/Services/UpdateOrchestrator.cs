using System.Net.Http;
using System.Net.NetworkInformation;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Kontrol ve güncelleme akışını sırayla yürütür:
/// Winget → Windows Update → Microsoft Store → NVIDIA → Defender → Çöp Kutusu.
/// Her modül birbirinden bağımsızdır; biri başarısız olursa diğerleri devam eder.
/// Arayüz ile yalnızca IProgress üzerinden konuşur (UI kodu içermez).
/// </summary>
public sealed class UpdateOrchestrator : IDisposable
{
    private readonly Logger _logger;
    private readonly HttpClient _http;

    public IReadOnlyList<IUpdateModule> Modules { get; }

    public UpdateOrchestrator(Logger logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RTXWindowsUpdater/1.0");

        Modules =
        [
            new WingetManager(logger, "winget", ComponentKeys.Winget, "Winget"),
            new WindowsUpdateManager(logger),
            new MicrosoftStoreManager(logger),
            new NvidiaDriverManager(logger, _http),
            new DefenderManager(logger, _http),
            new RecycleBinManager(logger)
        ];
    }

    private static readonly Dictionary<string, string> CheckTexts = new()
    {
        [ComponentKeys.Winget] = "Winget kontrol ediliyor...",
        [ComponentKeys.WindowsUpdate] = "Windows Update kontrol ediliyor...",
        [ComponentKeys.Store] = "Microsoft Store güncellemeleri kontrol ediliyor...",
        [ComponentKeys.Nvidia] = "NVIDIA sürücüleri kontrol ediliyor...",
        [ComponentKeys.Defender] = "Microsoft Defender güncellemeleri kontrol ediliyor...",
        [ComponentKeys.RecycleBin] = "Çöp kutusu kontrol ediliyor..."
    };

    private static readonly Dictionary<string, string> UpdateTexts = new()
    {
        [ComponentKeys.Winget] = "Winget paketleri güncelleniyor...",
        [ComponentKeys.WindowsUpdate] = "Windows güncelleştirmeleri indiriliyor ve kuruluyor...",
        [ComponentKeys.Store] = "Microsoft Store uygulamaları güncelleniyor...",
        [ComponentKeys.Nvidia] = "NVIDIA sürücüsü indiriliyor ve kuruluyor...",
        [ComponentKeys.Defender] = "Microsoft Defender tanımları güncelleniyor...",
        [ComponentKeys.RecycleBin] = "Çöp kutusu temizleniyor..."
    };

    public async Task<Dictionary<string, ModuleResult>> RunChecksAsync(
        IProgress<StepProgress> step, IProgress<ModuleResult> moduleState, CancellationToken ct)
    {
        var results = new Dictionary<string, ModuleResult>();

        step.Report(new StepProgress("Sistem hazırlanıyor...", 2));
        _logger.Info("Güncelleme kontrolü başlatıldı.");

        step.Report(new StepProgress("Yönetici izinleri kontrol ediliyor...", 4));
        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error("Yönetici yetkisi yok – sistem üzerinde işlem yapılmayacak.");
            foreach (var m in Modules)
            {
                var r = new ModuleResult
                {
                    Key = m.Key,
                    Status = ComponentStatus.AdminRequired,
                    Summary = "Yönetici izni gerekli",
                    Reason = "Uygulama yönetici yetkisiyle çalışmıyor."
                };
                results[m.Key] = r;
                moduleState.Report(r);
            }
            step.Report(new StepProgress("Yönetici izni gerekli", 100));
            return results;
        }
        _logger.Success("Yönetici yetkisi doğrulandı.");

        step.Report(new StepProgress("İnternet bağlantısı kontrol ediliyor...", 6));
        await CheckNetworkAsync(ct);

        for (var i = 0; i < Modules.Count; i++)
        {
            var m = Modules[i];
            if (ct.IsCancellationRequested)
            {
                MarkSkipped(m, results, moduleState, "Kontrol iptal edildi.");
                continue;
            }

            step.Report(new StepProgress(CheckTexts[m.Key], 8 + 92.0 * i / Modules.Count));
            moduleState.Report(new ModuleResult { Key = m.Key, Status = ComponentStatus.Checking, Summary = "Kontrol ediliyor..." });

            var r = await SafeRunAsync(() => m.CheckAsync(ct), m, isCheck: true, ct);
            results[m.Key] = r;
            moduleState.Report(r);
        }

        step.Report(new StepProgress(ct.IsCancellationRequested ? "Kontrol iptal edildi" : "Kontrol tamamlandı", 100));
        _logger.Info(ct.IsCancellationRequested ? "Kontrol kullanıcı tarafından iptal edildi." : "Tüm kontroller tamamlandı.");
        return results;
    }

    public async Task<Dictionary<string, ModuleResult>> RunUpdatesAsync(
        IReadOnlyDictionary<string, ModuleResult> checks, bool cleanRecycleBin,
        IProgress<StepProgress> step, IProgress<ModuleResult> moduleState, CancellationToken ct)
    {
        var results = new Dictionary<string, ModuleResult>(checks);

        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error("Yönetici yetkisi olmadan güncelleme yapılamaz.");
            return results;
        }

        var targets = Modules.Where(m =>
                checks.TryGetValue(m.Key, out var c) && c.HasActionableUpdates &&
                (m.Key != ComponentKeys.RecycleBin || cleanRecycleBin))
            .ToList();

        _logger.Info($"Güncelleme başlatıldı: {string.Join(", ", targets.Select(t => t.DisplayName))}");

        for (var i = 0; i < targets.Count; i++)
        {
            var m = targets[i];
            if (ct.IsCancellationRequested)
            {
                MarkSkipped(m, results, moduleState, "Kullanıcı kalan işlemleri iptal etti.");
                continue;
            }

            step.Report(new StepProgress(UpdateTexts[m.Key], 100.0 * i / Math.Max(1, targets.Count)));
            moduleState.Report(new ModuleResult { Key = m.Key, Status = ComponentStatus.Updating, Summary = "Güncelleniyor..." });

            // Güncelleme başladıktan sonra yarıda kesilmez; iptal yalnızca adımlar arasında uygulanır.
            var check = checks[m.Key];
            var r = await SafeRunAsync(() => m.UpdateAsync(check, CancellationToken.None), m, isCheck: false, CancellationToken.None);
            results[m.Key] = r;
            moduleState.Report(r);
        }

        if (!cleanRecycleBin && checks.TryGetValue(ComponentKeys.RecycleBin, out var rb) && rb.HasActionableUpdates)
        {
            var skipped = new ModuleResult
            {
                Key = ComponentKeys.RecycleBin,
                Status = ComponentStatus.Skipped,
                Summary = "Temizlenmedi (seçilmedi)",
                Details = rb.Details,
                Reason = "Çöp kutusu temizliği seçilmediği için dokunulmadı."
            };
            results[ComponentKeys.RecycleBin] = skipped;
            moduleState.Report(skipped);
        }

        step.Report(new StepProgress("İşlem tamamlandı", 100));
        _logger.Info("Güncelleme işlemleri tamamlandı.");
        return results;
    }

    private async Task<ModuleResult> SafeRunAsync(Func<Task<ModuleResult>> action, IUpdateModule m, bool isCheck, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warning($"{m.DisplayName}: işlem iptal edildi.");
            return new ModuleResult
            {
                Key = m.Key,
                Status = ComponentStatus.Skipped,
                Summary = "İptal edildi",
                Reason = "İşlem kullanıcı tarafından iptal edildi."
            };
        }
        catch (Exception ex)
        {
            var reason = $"Beklenmeyen hata: {ex.Message}";
            _logger.Error($"{m.DisplayName}: {reason}");
            return isCheck ? ModuleResult.CheckFailed(m.Key, reason) : ModuleResult.Failed(m.Key, reason);
        }
    }

    private void MarkSkipped(IUpdateModule m, Dictionary<string, ModuleResult> results, IProgress<ModuleResult> state, string reason)
    {
        var r = new ModuleResult { Key = m.Key, Status = ComponentStatus.Skipped, Summary = "Atlandı", Reason = reason };
        results[m.Key] = r;
        state.Report(r);
    }

    private async Task CheckNetworkAsync(CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _logger.Error("Ağ bağlantısı bulunamadı. Çevrimiçi kontroller başarısız olacaktır.");
            return;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var text = await _http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt", cts.Token);
            if (text.Contains("Microsoft Connect Test", StringComparison.Ordinal))
                _logger.Success("İnternet bağlantısı doğrulandı.");
            else
                _logger.Warning("İnternet bağlantısı sınırlı görünüyor (yakalama portalı / proxy olabilir).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Warning($"İnternet bağlantısı doğrulanamadı: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}

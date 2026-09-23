using System.Net.Http;
using System.Net.NetworkInformation;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>Orkestratörün arayüze ilerleme bildirdiği kanallar.</summary>
public sealed record OrchestratorReporters(
    IProgress<StepProgress> Step,
    IProgress<ModuleResult> ModuleState,
    IProgress<ModuleProgress>? Activity = null);

/// <summary>
/// Kontrol, güncelleme ve bakım akışlarını güvenli sırayla yürütür:
/// Winget → Windows Update → Microsoft Store → NVIDIA → Defender → SFC → DISM → MRT → Çöp Kutusu.
///
/// İki çalışma şekli vardır:
///   TÜMÜ       : RunAllChecksAsync / RunAllUpdatesAsync        – kullanıcı seçimine bakılmaz.
///   SEÇİLENLER : RunSelectedChecksAsync / RunSelectedUpdatesAsync – yalnızca verilen anahtarlar çalışır;
///                seçilmeyen hiçbir modüle dokunulmaz.
/// Her modül bağımsızdır; biri başarısız olursa diğerleri devam eder.
/// Arayüz ile yalnızca IProgress üzerinden konuşur (UI kodu içermez).
/// </summary>
public sealed class UpdateOrchestrator : IDisposable
{
    private readonly Logger _logger;
    private readonly HttpClient _http;
    private IProgress<ModuleProgress>? _activity;

    public IReadOnlyList<IUpdateModule> Modules { get; }

    /// <summary>Güvenli işlem sırası (modül anahtarları).</summary>
    public IReadOnlyList<string> ModuleOrder { get; }

    private static readonly HashSet<string> OnlineModules =
    [
        ComponentKeys.Winget, ComponentKeys.WindowsUpdate, ComponentKeys.Store,
        ComponentKeys.Nvidia, ComponentKeys.Defender
    ];

    public UpdateOrchestrator(Logger logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RTXWindowsUpdater/1.1");

        Modules =
        [
            new WingetManager(logger, "winget", ComponentKeys.Winget, "Winget"),
            new WindowsUpdateManager(logger),
            new MicrosoftStoreManager(logger),
            new NvidiaDriverManager(logger, _http),
            new DefenderManager(logger, _http),
            new SfcManager(logger),
            new DismManager(logger),
            new MrtManager(logger),
            new RecycleBinManager(logger)
        ];
        ModuleOrder = Modules.Select(m => m.Key).ToList();

        foreach (var m in Modules.OfType<IProgressReportingModule>())
            m.ProgressChanged += p => _activity?.Report(p);
    }

    public IUpdateModule? Find(string key) => Modules.FirstOrDefault(m => m.Key == key);

    public string NameOf(string key) => Find(key)?.DisplayName ?? key;

    private static readonly Dictionary<string, string> CheckTexts = new()
    {
        [ComponentKeys.Winget] = "Winget kontrol ediliyor...",
        [ComponentKeys.WindowsUpdate] = "Windows Update kontrol ediliyor...",
        [ComponentKeys.Store] = "Microsoft Store güncellemeleri kontrol ediliyor...",
        [ComponentKeys.Nvidia] = "NVIDIA sürücüleri kontrol ediliyor...",
        [ComponentKeys.Defender] = "Microsoft Defender güncellemeleri kontrol ediliyor...",
        [ComponentKeys.Sfc] = "Sistem dosyaları doğrulanıyor (sfc /verifyonly)...",
        [ComponentKeys.Dism] = "Windows image sağlık durumu kontrol ediliyor (DISM CheckHealth)...",
        [ComponentKeys.Mrt] = "MRT hızlı tarama yapılıyor (yalnızca tespit)...",
        [ComponentKeys.RecycleBin] = "Çöp kutusu kontrol ediliyor..."
    };

    private static readonly Dictionary<string, string> UpdateTexts = new()
    {
        [ComponentKeys.Winget] = "Winget paketleri güncelleniyor...",
        [ComponentKeys.WindowsUpdate] = "Windows güncelleştirmeleri indiriliyor ve kuruluyor...",
        [ComponentKeys.Store] = "Microsoft Store uygulamaları güncelleniyor...",
        [ComponentKeys.Nvidia] = "NVIDIA sürücüsü indiriliyor ve kuruluyor...",
        [ComponentKeys.Defender] = "Microsoft Defender tanımları güncelleniyor...",
        [ComponentKeys.Sfc] = "Sistem dosyaları onarılıyor (sfc /scannow)...",
        [ComponentKeys.Dism] = "DISM CheckHealth...",
        [ComponentKeys.Mrt] = "Tespit edilen tehditler temizleniyor (MRT hızlı tarama)...",
        [ComponentKeys.RecycleBin] = "Çöp kutusu temizleniyor..."
    };

    private static readonly Dictionary<string, string> ActionTexts = new()
    {
        [ComponentKeys.Sfc] = "Sistem dosyası taraması sürüyor (sfc /scannow)...",
        [ComponentKeys.Dism] = "Windows image sağlık kontrolü sürüyor (DISM CheckHealth)...",
        [ComponentKeys.Mrt] = "MRT hızlı tarama sürüyor..."
    };

    /// <summary>İşlemin nasıl başlatıldığı: tüm kartlar, seçilen kartlar veya tek bir kartın kendi butonu.</summary>
    private enum RunMode { All, Selected, Single }

    /// <summary>Kartta işlem sürerken gösterilen, işlem türüne uygun metin.</summary>
    private static string RunningText(string key, bool check) => key switch
    {
        ComponentKeys.Sfc or ComponentKeys.Mrt => "Tarama devam ediyor...",
        ComponentKeys.Dism => "Kontrol ediliyor...",
        ComponentKeys.RecycleBin when !check => "Temizleniyor...",
        _ => check ? "Kontrol ediliyor..." : "Güncelleniyor..."
    };

    // ================================================================= KONTROL

    /// <summary>Tüm kartları kontrol eder (seçimlere bakılmaz).</summary>
    public Task<Dictionary<string, ModuleResult>> RunAllChecksAsync(OrchestratorReporters rep, CancellationToken ct) =>
        RunChecksCoreAsync(ModuleOrder, RunMode.All, rep, ct);

    /// <summary>Yalnızca seçilen kartları kontrol eder; seçilmeyenlere dokunmaz.</summary>
    public Task<Dictionary<string, ModuleResult>> RunSelectedChecksAsync(
        IReadOnlyCollection<string> selectedKeys, OrchestratorReporters rep, CancellationToken ct) =>
        RunChecksCoreAsync(selectedKeys, RunMode.Selected, rep, ct);

    /// <summary>Tek bir kartı kendi "Kontrol Et" butonuyla kontrol eder.</summary>
    public Task<Dictionary<string, ModuleResult>> RunSingleCheckAsync(
        string key, OrchestratorReporters rep, CancellationToken ct) =>
        RunChecksCoreAsync([key], RunMode.Single, rep, ct);

    private async Task<Dictionary<string, ModuleResult>> RunChecksCoreAsync(
        IReadOnlyCollection<string> keys, RunMode mode, OrchestratorReporters rep, CancellationToken ct)
    {
        var selectedMode = mode == RunMode.Selected;
        var results = new Dictionary<string, ModuleResult>();
        var targets = Modules.Where(m => keys.Contains(m.Key)).ToList();
        if (targets.Count == 0) return results;

        _activity = rep.Activity;
        try
        {
            rep.Step.Report(new StepProgress("Sistem hazırlanıyor...", 2));
            if (selectedMode) LogSelection(keys, "Seçilen işlemler hazırlanıyor...");
            else if (mode == RunMode.Single) _logger.Info($"{targets[0].DisplayName}: kontrol başlatıldı.");
            else _logger.Info("Tüm kartların kontrolü başlatıldı.");

            rep.Step.Report(new StepProgress("Yönetici izinleri kontrol ediliyor...", 4));
            if (!AdminPrivilegeManager.IsElevated)
            {
                _logger.Error("Yönetici yetkisi yok – sistem üzerinde işlem yapılmayacak.");
                foreach (var m in targets)
                {
                    var r = AdminRequiredResult(m.Key);
                    results[m.Key] = r;
                    rep.ModuleState.Report(r);
                }
                rep.Step.Report(new StepProgress("Yönetici izni gerekli", 100));
                return results;
            }
            _logger.Success("Yönetici yetkisi doğrulandı.");

            if (targets.Any(t => OnlineModules.Contains(t.Key)))
            {
                rep.Step.Report(new StepProgress("İnternet bağlantısı kontrol ediliyor...", 6));
                await CheckNetworkAsync(ct);
            }

            if (targets.Any(t => t.Key is ComponentKeys.Sfc or ComponentKeys.Mrt))
                _logger.Info("Not: SFC doğrulaması ve MRT hızlı taraması birkaç dakika ile yarım saat arasında sürebilir.");

            if (selectedMode) _logger.Info("Seçilen işlemler başlatılıyor...");

            for (var i = 0; i < targets.Count; i++)
            {
                var m = targets[i];
                if (ct.IsCancellationRequested)
                {
                    MarkSkipped(m, results, rep.ModuleState, "Kontrol iptal edildi.");
                    continue;
                }

                rep.Step.Report(new StepProgress(CheckTexts[m.Key], 8 + 92.0 * i / targets.Count));
                rep.ModuleState.Report(new ModuleResult { Key = m.Key, Status = ComponentStatus.Checking, Summary = RunningText(m.Key, check: true) });

                var r = await SafeRunAsync(() => m.CheckAsync(ct), m, isCheck: true, ct);
                results[m.Key] = r;
                rep.ModuleState.Report(r);
            }

            rep.Step.Report(new StepProgress(ct.IsCancellationRequested ? "Kontrol iptal edildi" : "Kontrol tamamlandı", 100));
            _logger.Info(ct.IsCancellationRequested
                ? "Kontrol kullanıcı tarafından iptal edildi."
                : mode switch
                {
                    RunMode.Selected => "Seçilen kontroller tamamlandı.",
                    RunMode.Single => $"{targets[0].DisplayName}: kontrol tamamlandı.",
                    _ => "Tüm kontroller tamamlandı."
                });
            return results;
        }
        finally
        {
            _activity = null;
        }
    }

    // ================================================================= GÜNCELLEME / ÇALIŞTIRMA

    /// <summary>Kontrolde işlem gerektirdiği görülen TÜM uygun kartları işler.</summary>
    public Task<Dictionary<string, ModuleResult>> RunAllUpdatesAsync(
        IReadOnlyDictionary<string, ModuleResult> checks, bool cleanRecycleBin, OrchestratorReporters rep, CancellationToken ct) =>
        RunUpdatesCoreAsync(checks, ModuleOrder, cleanRecycleBin, RunMode.All, rep, ct);

    /// <summary>Yalnızca seçilen kartlardan, kontrolde gerçekten işlem gerektirenleri işler.</summary>
    public Task<Dictionary<string, ModuleResult>> RunSelectedUpdatesAsync(
        IReadOnlyDictionary<string, ModuleResult> checks, IReadOnlyCollection<string> selectedKeys,
        OrchestratorReporters rep, CancellationToken ct) =>
        RunUpdatesCoreAsync(checks, selectedKeys, cleanRecycleBin: selectedKeys.Contains(ComponentKeys.RecycleBin),
            RunMode.Selected, rep, ct);

    /// <summary>
    /// Tek bir kartın kontrolünde bulunan işlemi, kullanıcı kart üzerinden onay verdikten sonra uygular
    /// (ör. Winget "Kontrol Et" → güncelleme bulundu → "Şimdi güncelle").
    /// </summary>
    public Task<Dictionary<string, ModuleResult>> RunSingleUpdateAsync(
        IReadOnlyDictionary<string, ModuleResult> checks, string key, OrchestratorReporters rep, CancellationToken ct) =>
        RunUpdatesCoreAsync(checks, [key], cleanRecycleBin: key == ComponentKeys.RecycleBin, RunMode.Single, rep, ct);

    private async Task<Dictionary<string, ModuleResult>> RunUpdatesCoreAsync(
        IReadOnlyDictionary<string, ModuleResult> checks, IReadOnlyCollection<string> keys, bool cleanRecycleBin,
        RunMode mode, OrchestratorReporters rep, CancellationToken ct)
    {
        var selectedMode = mode == RunMode.Selected;
        var results = new Dictionary<string, ModuleResult>();

        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error("Yönetici yetkisi olmadan güncelleme yapılamaz.");
            return results;
        }

        if (selectedMode) LogSelection(keys, "Seçilen işlemler hazırlanıyor...");

        var targets = new List<IUpdateModule>();
        foreach (var m in Modules.Where(m => keys.Contains(m.Key)))
        {
            if (!checks.TryGetValue(m.Key, out var c))
            {
                _logger.Info($"{m.DisplayName}: kontrol sonucu yok, atlandı.");
                continue;
            }
            if (m.Key == ComponentKeys.RecycleBin && !cleanRecycleBin) continue;
            if (c.HasActionableUpdates) targets.Add(m);
            else if (mode != RunMode.All) _logger.Info($"{m.DisplayName}: işlem gerekmiyor ({c.Summary}), atlandı.");
        }

        _logger.Info(targets.Count == 0
            ? "İşlem gerektiren bileşen yok."
            : $"{mode switch { RunMode.Selected => "Seçilen işlemler başlatılıyor", RunMode.Single => "İşlem başlatılıyor", _ => "Güncelleme başlatıldı" }}: {string.Join(", ", targets.Select(t => t.DisplayName))}");

        _activity = rep.Activity;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var m = targets[i];
                if (ct.IsCancellationRequested)
                {
                    MarkSkipped(m, results, rep.ModuleState, "Kullanıcı kalan işlemleri iptal etti.");
                    continue;
                }

                rep.Step.Report(new StepProgress(UpdateTexts[m.Key], 100.0 * i / Math.Max(1, targets.Count)));
                rep.ModuleState.Report(new ModuleResult { Key = m.Key, Status = ComponentStatus.Updating, Summary = RunningText(m.Key, check: false) });

                // Güncelleme/onarım başladıktan sonra yarıda kesilmez; iptal yalnızca adımlar arasında uygulanır.
                var check = checks[m.Key];
                var r = await SafeRunAsync(() => m.UpdateAsync(check, CancellationToken.None), m, isCheck: false, CancellationToken.None);
                results[m.Key] = r;
                rep.ModuleState.Report(r);
            }
        }
        finally
        {
            _activity = null;
        }

        if (mode == RunMode.All && !cleanRecycleBin && keys.Contains(ComponentKeys.RecycleBin) &&
            checks.TryGetValue(ComponentKeys.RecycleBin, out var rb) && rb.HasActionableUpdates)
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
            rep.ModuleState.Report(skipped);
        }

        rep.Step.Report(new StepProgress("İşlem tamamlandı", 100));
        _logger.Info(mode switch
        {
            RunMode.Selected => "Seçilen işlemler tamamlandı.",
            RunMode.Single => "İşlem tamamlandı.",
            _ => "Güncelleme işlemleri tamamlandı."
        });
        return results;
    }

    // ================================================================= KART EYLEMİ (SFC / DISM / MRT)

    /// <summary>Bir bakım kartının kendi butonundaki işlemi çalıştırır.</summary>
    public async Task<ModuleResult> RunMaintenanceActionAsync(string key, OrchestratorReporters rep, CancellationToken ct)
    {
        if (Find(key) is not IMaintenanceModule m)
            return ModuleResult.CheckFailed(key, "Bu kart için çalıştırılabilir bir işlem yok.");

        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error($"{m.DisplayName}: yönetici yetkisi yok – işlem yapılmadı.");
            var denied = AdminRequiredResult(key);
            rep.ModuleState.Report(denied);
            return denied;
        }

        _activity = rep.Activity;
        try
        {
            rep.Step.Report(new StepProgress(ActionTexts[key], 5));
            rep.ModuleState.Report(new ModuleResult { Key = key, Status = ComponentStatus.Updating, Summary = RunningText(key, check: key != ComponentKeys.Sfc) });
            var r = await SafeRunAsync(() => m.RunActionAsync(ct), m, isCheck: key != ComponentKeys.Sfc, ct);
            rep.ModuleState.Report(r);
            rep.Step.Report(new StepProgress("İşlem tamamlandı", 100));
            return r;
        }
        finally
        {
            _activity = null;
        }
    }

    // ================================================================= yardımcılar

    private void LogSelection(IReadOnlyCollection<string> keys, string header)
    {
        _logger.Info(header);
        foreach (var m in Modules)
        {
            if (keys.Contains(m.Key)) _logger.Info($"{m.DisplayName} seçildi.");
            else _logger.Output($"{m.DisplayName} seçilmedi, atlandı.");
        }
    }

    private static ModuleResult AdminRequiredResult(string key) => new()
    {
        Key = key,
        Status = ComponentStatus.AdminRequired,
        Summary = "Yönetici izni gerekli",
        Reason = "Uygulama yönetici yetkisiyle çalışmıyor."
    };

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

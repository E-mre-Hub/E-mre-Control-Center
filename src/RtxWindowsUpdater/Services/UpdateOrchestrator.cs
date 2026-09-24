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
/// Winget → Windows Update → Microsoft Store → NVIDIA → Defender → DISM → SFC → MRT → Geçici Dosyalar → Çöp Kutusu.
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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) E-mreControlCenter/" + AppInfo.Version);

        Modules =
        [
            new WingetManager(logger, "winget", ComponentKeys.Winget, "Winget"),
            new WindowsUpdateManager(logger),
            new MicrosoftStoreManager(logger),
            new NvidiaDriverManager(logger, _http),
            new DefenderManager(logger, _http),
            // Microsoft'un önerdiği onarım sırası: önce bileşen deposu (DISM), sonra sistem dosyaları (SFC).
            new DismManager(logger),
            new SfcManager(logger),
            new MrtManager(logger),
            new TemporaryFilesManager(logger),
            new RecycleBinManager(logger)
        ];
        ModuleOrder = Modules.Select(m => m.Key).ToList();

        foreach (var m in Modules.OfType<IProgressReportingModule>())
            m.ProgressChanged += p => _activity?.Report(p);
    }

    /// <summary>Bu sistemde kullanım dışı modüller (ör. RTX yoksa NVIDIA). Hiçbir kontrol / güncelleme akışına girmez.</summary>
    private readonly HashSet<string> _unavailable = [];

    public void SetUnavailable(string key)
    {
        lock (_unavailable) _unavailable.Add(key);
    }

    public bool IsUnavailable(string key)
    {
        lock (_unavailable) return _unavailable.Contains(key);
    }

    /// <summary>Anahtarlardaki modüller, güvenli işlem sırasıyla ve kullanım dışı olanlar hariç.</summary>
    private List<IUpdateModule> AvailableModules(IReadOnlyCollection<string> keys) =>
        Modules.Where(m => keys.Contains(m.Key) && !IsUnavailable(m.Key)).ToList();

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
        [ComponentKeys.TempFiles] = "Windows geçici dosyaları ölçülüyor...",
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
        [ComponentKeys.Dism] = "Windows bileşen deposu onarılıyor (DISM /RestoreHealth)...",
        [ComponentKeys.Mrt] = "Tespit edilen tehditler temizleniyor (MRT hızlı tarama)...",
        [ComponentKeys.TempFiles] = "Windows geçici dosyaları temizleniyor...",
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
        ComponentKeys.Dism => check ? "Kontrol ediliyor..." : "Onarılıyor...",
        ComponentKeys.RecycleBin or ComponentKeys.TempFiles when !check => "Temizleniyor...",
        ComponentKeys.TempFiles => "Ölçülüyor...",
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
        var targets = AvailableModules(keys);
        if (targets.Count == 0) return results;

        _activity = rep.Activity;
        try
        {
            rep.Step.Report(new StepProgress("Sistem hazırlanıyor...", 2));
            if (selectedMode) LogSelection(keys, "Seçilen işlemler hazırlanıyor...");
            else if (mode == RunMode.Single) _logger.Info($"{targets[0].DisplayName}: kontrol başlatıldı.");
            else _logger.Info("Tüm kartların kontrolü başlatıldı.");
            foreach (var k in keys.Where(IsUnavailable))
                _logger.Info($"{NameOf(k)}: bu sistemde kullanım dışı, kontrol edilmedi.");

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
            // Yönetici yetkisi uygulama açılışında doğrulanıp günlüğe yazıldı; burada yalnızca (önbellekten) denetlenir.

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

                var r = await SafeRunAsync(() => m.CheckAsync(ct), m, OperationKind.Check, isCheck: true, ct);
                results[m.Key] = r;
                rep.ModuleState.Report(r);
            }

            rep.Step.Report(new StepProgress(ct.IsCancellationRequested ? "Kontrol iptal edildi" : "Kontrol tamamlandı", 100));
            if (ct.IsCancellationRequested)
            {
                _logger.Warning("Kontrol kullanıcı tarafından iptal edildi.");
            }
            else
            {
                var failed = results.Values.Count(r => r.Status is ComponentStatus.CheckFailed or ComponentStatus.Failed or ComponentStatus.AdminRequired);
                var text = mode switch
                {
                    RunMode.Selected => "Seçilen kontroller tamamlandı",
                    RunMode.Single => $"{targets[0].DisplayName}: kontrol tamamlandı",
                    _ => "Tüm kontroller tamamlandı"
                };
                if (failed > 0) _logger.Warning($"{text} – {failed} kontrol başarısız.");
                else _logger.Info(text + ".");
            }
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
        foreach (var m in AvailableModules(keys))
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
                var r = await SafeRunAsync(() => m.UpdateAsync(check, CancellationToken.None), m, OperationKind.Update, isCheck: false, CancellationToken.None);
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
        LogUpdateOutcome(mode switch
        {
            RunMode.Selected => "Seçilen işlemler tamamlandı",
            RunMode.Single => "İşlem tamamlandı",
            _ => "Güncelleme işlemleri tamamlandı"
        }, results);
        return results;
    }

    /// <summary>Güncelleme bitiş mesajını GERÇEK sonuçlara göre yazar (hata/uyarı varken yalnızca "tamamlandı" denmez).</summary>
    private void LogUpdateOutcome(string text, IReadOnlyDictionary<string, ModuleResult> results)
    {
        var errors = results.Values.Count(r => r.Status is ComponentStatus.Failed or ComponentStatus.CheckFailed or ComponentStatus.AdminRequired);
        var warnings = results.Values.Count(r => r.Status is ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired or ComponentStatus.Attention);
        if (errors > 0 || warnings > 0) _logger.Warning($"{text} – {errors} hata, {warnings} uyarı.");
        else _logger.Success(text + ".");
    }

    // ================================================================= MANUEL (OTOMATİK UYGULANMAYAN) GÜNCELLEMELER

    /// <summary>
    /// Kullanıcının ayrıca seçtiği, otomatik uygulanmayan güncellemeleri uygular (açık hedefleme / kaldır + yeniden kur).
    /// </summary>
    public async Task<ModuleResult?> RunManualUpdatesAsync(ModuleResult check, IReadOnlyCollection<string> ids,
        OrchestratorReporters rep, CancellationToken ct)
    {
        if (Find(check.Key) is not { } module || module is not IManualUpdateModule manual) return null;
        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error("Yönetici yetkisi olmadan güncelleme yapılamaz.");
            return null;
        }

        _activity = rep.Activity;
        try
        {
            _logger.Info($"{module.DisplayName}: seçilen manuel güncellemeler başlatıldı ({ids.Count} paket).");
            rep.Step.Report(new StepProgress($"{module.DisplayName}: seçilen manuel güncellemeler uygulanıyor...", 5));
            rep.ModuleState.Report(new ModuleResult { Key = module.Key, Status = ComponentStatus.Updating, Summary = "Manuel güncelleme uygulanıyor..." });
            // Kaldırma/kurulum başladıktan sonra yarıda kesilmez.
            var r = await SafeRunAsync(() => manual.UpdateManualAsync(check, ids, CancellationToken.None),
                module, OperationKind.Update, isCheck: false, CancellationToken.None);
            rep.ModuleState.Report(r);
            rep.Step.Report(new StepProgress("İşlem tamamlandı", 100));
            LogUpdateOutcome($"{module.DisplayName}: manuel güncellemeler tamamlandı", new Dictionary<string, ModuleResult> { [r.Key] = r });
            return r;
        }
        finally
        {
            _activity = null;
        }
    }

    // ================================================================= ÇALIŞAN UYGULAMAYI KAPATIP YENİDEN DENEME

    /// <summary>
    /// "Uygulama çalışıyor / dosyalar kullanımda" nedeniyle güncellenemeyen paketler için, kullanıcının onayladığı
    /// engelleyen uygulamaları kapatır ve güncellemeyi yeniden dener (Winget / Microsoft Store).
    /// </summary>
    public async Task<ModuleResult?> RetryInUseAsync(ModuleResult previous, IReadOnlyCollection<RunningProcessInfo> approved,
        OrchestratorReporters rep, CancellationToken ct)
    {
        if (Find(previous.Key) is not { } module || module is not IInUseRetryModule retry) return null;
        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error("Yönetici yetkisi olmadan güncelleme yapılamaz.");
            return null;
        }

        _activity = rep.Activity;
        try
        {
            rep.Step.Report(new StepProgress($"{module.DisplayName}: çalışan uygulamalar kapatılıp güncelleme yeniden deneniyor...", 5));
            rep.ModuleState.Report(new ModuleResult { Key = module.Key, Status = ComponentStatus.Updating, Summary = "Yeniden deneniyor..." });
            // Kapatma ve kurulum başladıktan sonra yarıda kesilmez.
            var r = await SafeRunAsync(() => retry.RetryAfterClosingAsync(previous, approved, CancellationToken.None),
                module, OperationKind.Update, isCheck: false, CancellationToken.None);
            rep.ModuleState.Report(r);
            rep.Step.Report(new StepProgress("İşlem tamamlandı", 100));
            LogUpdateOutcome($"{module.DisplayName}: yeniden deneme tamamlandı", new Dictionary<string, ModuleResult> { [r.Key] = r });
            return r;
        }
        finally
        {
            _activity = null;
        }
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
            var r = await SafeRunAsync(() => m.RunActionAsync(ct), m, OperationKind.Action, isCheck: key != ComponentKeys.Sfc, ct);
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

    /// <summary>
    /// Modül işlemini hatalara karşı korunmuş şekilde çalıştırır; gerçek bitiş zamanı, süre ve
    /// işlem sırasında çalıştırılan komutların kayıtlarını (stdout/stderr/çıkış kodu) sonuca ekler.
    /// </summary>
    private async Task<ModuleResult> SafeRunAsync(Func<Task<ModuleResult>> action, IUpdateModule m, OperationKind op, bool isCheck, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var trace = ExecutionTrace.Begin();
        ModuleResult result;
        try
        {
            // Modül kodunun TAMAMI arka plan iş parçacığında çalışır: WMI sorguları, XML/JSON ayrıştırma,
            // imza doğrulama, dosya okuma ve indirme döngüleri arayüz iş parçacığını asla bloklamaz.
            // (Task.Run ExecutionContext'i taşır; ExecutionTrace'in AsyncLocal kaydı korunur.)
            result = await Task.Run(action, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warning($"{m.DisplayName}: işlem iptal edildi.");
            result = new ModuleResult
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
            result = isCheck ? ModuleResult.CheckFailed(m.Key, reason) : ModuleResult.Failed(m.Key, reason);
        }
        finally
        {
            ExecutionTrace.End();
        }

        result.Operation = op;
        result.CompletedAt = DateTime.Now;
        result.Duration = watch.Elapsed;
        result.Commands = trace.Commands;
        result.Notes = trace.Notes;
        _logger.Info($"{m.DisplayName}: işlem süresi {FormatDuration(watch.Elapsed)}.");
        return result;
    }

    /// <summary>Süreyi "4 dk 21 sn" biçiminde yazar.</summary>
    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} sa {d.Minutes} dk"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} dk {d.Seconds} sn"
        : d.TotalSeconds >= 1 ? $"{(int)d.TotalSeconds} sn"
        : $"{Math.Max(1, (int)d.TotalMilliseconds)} ms";

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

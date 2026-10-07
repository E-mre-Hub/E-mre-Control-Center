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
            m.ProgressChanged += p =>
            {
                _activity?.Report(p);
                ForwardToStep(p);
            };
    }

    /// <summary>
    /// Çalışan modülün genel ilerlemedeki payı (Start → Start + Span). Modülün kendi canlı ilerlemesi (Winget n/toplam paket,
    /// SFC / DISM / MRT yüzdesi, geçici dosya kategorileri) genel çubuğa ve adım metnine yansır. 2026-10-07 KULLANICI SORUNU:
    /// 4 adımlı güncellemede Winget 7 paketi yaklaşık 25 dk kurarken genel çubuk %0'da kaldı ve uygulama "dondu" sanıldı.
    /// </summary>
    private sealed class StepContext(string key, string name, IProgress<StepProgress> step, double start, double span)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public IProgress<StepProgress> Step { get; } = step;
        public double Start { get; } = start;
        public double Span { get; } = span;
        public double LastFraction; // yüzdesiz (belirsiz) bildirimde çubuk geri gitmesin
    }

    private volatile StepContext? _stepContext;

    private void ForwardToStep(ModuleProgress p)
    {
        if (_stepContext is not { } c || c.Key != p.Key || string.IsNullOrWhiteSpace(p.Text)) return;
        if (p.Percent is double percent) c.LastFraction = Math.Clamp(percent, 0, 100) / 100;
        c.Step.Report(new StepProgress($"{c.Name}: {p.Text}", c.Start + c.Span * c.LastFraction));
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
        [ComponentKeys.Winget] = L.T("Winget kontrol ediliyor...", "Checking winget..."),
        [ComponentKeys.WindowsUpdate] = L.T("Windows Update kontrol ediliyor...", "Checking Windows Update..."),
        [ComponentKeys.Store] = L.T("Microsoft Store güncellemeleri kontrol ediliyor...", "Checking Microsoft Store updates..."),
        [ComponentKeys.Nvidia] = L.T("NVIDIA sürücüleri kontrol ediliyor...", "Checking NVIDIA drivers..."),
        [ComponentKeys.Defender] = L.T("Microsoft Defender güncellemeleri kontrol ediliyor...", "Checking Microsoft Defender updates..."),
        [ComponentKeys.Sfc] = L.T("Sistem dosyaları doğrulanıyor (sfc /verifyonly)...", "Verifying system files (sfc /verifyonly)..."),
        [ComponentKeys.Dism] = L.T("Windows image sağlık durumu kontrol ediliyor (DISM CheckHealth)...", "Checking Windows image health (DISM CheckHealth)..."),
        [ComponentKeys.Mrt] = L.T("MRT hızlı tarama yapılıyor (yalnızca tespit)...", "Running MRT quick scan (detection only)..."),
        [ComponentKeys.TempFiles] = L.T("Windows geçici dosyaları ölçülüyor...", "Measuring Windows temporary files..."),
        [ComponentKeys.RecycleBin] = L.T("Çöp kutusu kontrol ediliyor...", "Checking the Recycle Bin...")
    };

    private static readonly Dictionary<string, string> UpdateTexts = new()
    {
        [ComponentKeys.Winget] = L.T("Winget paketleri güncelleniyor...", "Updating winget packages..."),
        [ComponentKeys.WindowsUpdate] = L.T("Windows güncelleştirmeleri indiriliyor ve kuruluyor...", "Downloading and installing Windows updates..."),
        [ComponentKeys.Store] = L.T("Microsoft Store uygulamaları güncelleniyor...", "Updating Microsoft Store apps..."),
        [ComponentKeys.Nvidia] = L.T("NVIDIA sürücüsü indiriliyor ve kuruluyor...", "Downloading and installing the NVIDIA driver..."),
        [ComponentKeys.Defender] = L.T("Microsoft Defender tanımları güncelleniyor...", "Updating Microsoft Defender definitions..."),
        [ComponentKeys.Sfc] = L.T("Sistem dosyaları onarılıyor (sfc /scannow)...", "Repairing system files (sfc /scannow)..."),
        [ComponentKeys.Dism] = L.T("Windows bileşen deposu onarılıyor (DISM /RestoreHealth)...", "Repairing the Windows component store (DISM /RestoreHealth)..."),
        [ComponentKeys.Mrt] = L.T("Tespit edilen tehditler temizleniyor (MRT hızlı tarama)...", "Removing detected threats (MRT quick scan)..."),
        [ComponentKeys.TempFiles] = L.T("Windows geçici dosyaları temizleniyor...", "Cleaning Windows temporary files..."),
        [ComponentKeys.RecycleBin] = L.T("Çöp kutusu temizleniyor...", "Emptying the Recycle Bin...")
    };

    private static readonly Dictionary<string, string> ActionTexts = new()
    {
        [ComponentKeys.Sfc] = L.T("Sistem dosyası taraması sürüyor (sfc /scannow)...", "System file scan in progress (sfc /scannow)..."),
        [ComponentKeys.Dism] = L.T("Windows image sağlık kontrolü sürüyor (DISM CheckHealth)...", "Windows image health check in progress (DISM CheckHealth)..."),
        [ComponentKeys.Mrt] = L.T("MRT hızlı tarama sürüyor...", "MRT quick scan in progress...")
    };

    /// <summary>İşlemin nasıl başlatıldığı: tüm kartlar, seçilen kartlar veya tek bir kartın kendi butonu.</summary>
    private enum RunMode { All, Selected, Single }

    /// <summary>Kartta işlem sürerken gösterilen, işlem türüne uygun metin.</summary>
    private static string RunningText(string key, bool check) => key switch
    {
        ComponentKeys.Sfc or ComponentKeys.Mrt => L.T("Tarama devam ediyor...", "Scan in progress..."),
        ComponentKeys.Dism => check ? L.T("Kontrol ediliyor...", "Checking...") : L.T("Onarılıyor...", "Repairing..."),
        ComponentKeys.RecycleBin or ComponentKeys.TempFiles when !check => L.T("Temizleniyor...", "Cleaning..."),
        ComponentKeys.TempFiles => L.T("Ölçülüyor...", "Measuring..."),
        _ => check ? L.T("Kontrol ediliyor...", "Checking...") : L.T("Güncelleniyor...", "Updating...")
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
            rep.Step.Report(new StepProgress(L.T("Sistem hazırlanıyor...", "Preparing the system..."), 2));
            if (selectedMode) LogSelection(keys, L.T("Seçilen işlemler hazırlanıyor...", "Preparing the selected operations..."));
            else if (mode == RunMode.Single) _logger.Info(L.T($"{targets[0].DisplayName}: kontrol başlatıldı.", $"{targets[0].DisplayName}: check started."));
            else _logger.Info(L.T("Tüm kartların kontrolü başlatıldı.", "Check of all cards started."));
            foreach (var k in keys.Where(IsUnavailable))
                _logger.Info(L.T($"{NameOf(k)}: bu sistemde kullanım dışı, kontrol edilmedi.", $"{NameOf(k)}: unavailable on this system, not checked."));

            rep.Step.Report(new StepProgress(L.T("Yönetici izinleri kontrol ediliyor...", "Checking administrator permissions..."), 4));
            if (!AdminPrivilegeManager.IsElevated)
            {
                _logger.Error(L.T("Yönetici yetkisi yok – sistem üzerinde işlem yapılmayacak.", "No administrator rights – no operation will be performed on the system."));
                foreach (var m in targets)
                {
                    var r = AdminRequiredResult(m.Key);
                    results[m.Key] = r;
                    rep.ModuleState.Report(r);
                }
                rep.Step.Report(new StepProgress(L.T("Yönetici izni gerekli", "Administrator permission required"), 100));
                return results;
            }
            // Yönetici yetkisi uygulama açılışında doğrulanıp günlüğe yazıldı; burada yalnızca (önbellekten) denetlenir.

            var network = NetworkState.Verified;
            if (targets.Any(t => OnlineModules.Contains(t.Key)))
            {
                rep.Step.Report(new StepProgress(L.T("İnternet bağlantısı kontrol ediliyor...", "Checking the internet connection..."), 6));
                network = await CheckNetworkAsync(ct);
            }

            if (targets.Any(t => t.Key is ComponentKeys.Sfc or ComponentKeys.Mrt))
                _logger.Info(L.T("Not: SFC doğrulaması ve MRT hızlı taraması birkaç dakika ile yarım saat arasında sürebilir.", "Note: the SFC verification and the MRT quick scan can take from a few minutes to half an hour."));

            if (selectedMode) _logger.Info(L.T("Seçilen işlemler başlatılıyor...", "Starting the selected operations..."));

            for (var i = 0; i < targets.Count; i++)
            {
                var m = targets[i];
                if (ct.IsCancellationRequested)
                {
                    MarkSkipped(m, results, rep.ModuleState, L.T("Kontrol iptal edildi.", "Check cancelled."));
                    continue;
                }

                rep.Step.Report(new StepProgress(CheckTexts[m.Key], 8 + 92.0 * i / targets.Count));
                rep.ModuleState.Report(new ModuleResult { Key = m.Key, Status = ComponentStatus.Checking, Summary = RunningText(m.Key, check: true) });

                _stepContext = new StepContext(m.Key, m.DisplayName, rep.Step, 8 + 92.0 * i / targets.Count, 92.0 / targets.Count);
                var r = await SafeRunAsync(() => m.CheckAsync(ct), m, OperationKind.Check, isCheck: true, ct);
                _stepContext = null;
                var labeled = ApplyNetworkCaveat(r, network);
                if (!ReferenceEquals(labeled, r))
                    _logger.Warning(L.T($"{m.DisplayName}: internet bağlantısı doğrulanamadığı için sonuç \"Dikkat\" olarak işaretlendi (winget önbellekteki listeyi kullanmış olabilir).", $"{m.DisplayName}: the result was marked \"Attention\" because the internet connection could not be verified (winget may have used its cached list)."));
                results[m.Key] = labeled;
                rep.ModuleState.Report(labeled);
            }

            rep.Step.Report(new StepProgress(ct.IsCancellationRequested ? L.T("Kontrol iptal edildi", "Check cancelled") : L.T("Kontrol tamamlandı", "Check completed"), 100));
            if (ct.IsCancellationRequested)
            {
                _logger.Warning(L.T("Kontrol kullanıcı tarafından iptal edildi.", "The check was cancelled by the user."));
            }
            else
            {
                var failed = results.Values.Count(r => r.Status is ComponentStatus.CheckFailed or ComponentStatus.Failed or ComponentStatus.AdminRequired);
                var text = mode switch
                {
                    RunMode.Selected => L.T("Seçilen kontroller tamamlandı", "Selected checks completed"),
                    RunMode.Single => L.T($"{targets[0].DisplayName}: kontrol tamamlandı", $"{targets[0].DisplayName}: check completed"),
                    _ => L.T("Tüm kontroller tamamlandı", "All checks completed")
                };
                if (failed > 0) _logger.Warning(L.T($"{text} – {failed} kontrol başarısız.", $"{text} – {failed} check(s) failed."));
                else _logger.Info(text + ".");
            }
            return results;
        }
        finally
        {
            _activity = null;
            _stepContext = null;
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
            _logger.Error(L.T("Yönetici yetkisi olmadan güncelleme yapılamaz.", "Updates cannot be run without administrator rights."));
            return results;
        }

        if (selectedMode) LogSelection(keys, L.T("Seçilen işlemler hazırlanıyor...", "Preparing the selected operations..."));

        var targets = new List<IUpdateModule>();
        foreach (var m in AvailableModules(keys))
        {
            if (!checks.TryGetValue(m.Key, out var c))
            {
                _logger.Info(L.T($"{m.DisplayName}: kontrol sonucu yok, atlandı.", $"{m.DisplayName}: no check result, skipped."));
                continue;
            }
            if (m.Key == ComponentKeys.RecycleBin && !cleanRecycleBin) continue;
            if (c.HasActionableUpdates) targets.Add(m);
            else if (mode != RunMode.All) _logger.Info(L.T($"{m.DisplayName}: işlem gerekmiyor ({c.Summary}), atlandı.", $"{m.DisplayName}: no action needed ({c.Summary}), skipped."));
        }

        _logger.Info(targets.Count == 0
            ? L.T("İşlem gerektiren bileşen yok.", "No component needs action.")
            : L.T($"{mode switch { RunMode.Selected => "Seçilen işlemler başlatılıyor", RunMode.Single => "İşlem başlatılıyor", _ => "Güncelleme başlatıldı" }}: {string.Join(", ", targets.Select(t => t.DisplayName))}", $"{mode switch { RunMode.Selected => "Starting the selected operations", RunMode.Single => "Starting the operation", _ => "Update started" }}: {string.Join(", ", targets.Select(t => t.DisplayName))}"));

        _activity = rep.Activity;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var m = targets[i];
                if (ct.IsCancellationRequested)
                {
                    MarkSkipped(m, results, rep.ModuleState, L.T("Kullanıcı kalan işlemleri iptal etti.", "The user cancelled the remaining operations."));
                    continue;
                }

                rep.Step.Report(new StepProgress(UpdateTexts[m.Key], 100.0 * i / Math.Max(1, targets.Count)));
                rep.ModuleState.Report(new ModuleResult { Key = m.Key, Status = ComponentStatus.Updating, Summary = RunningText(m.Key, check: false) });

                // Güncelleme/onarım başladıktan sonra yarıda kesilmez; iptal adımlar arasında uygulanır. Bağımsız öğeleri sırayla
                // kuran modüller (Winget) belirteci alır: başlamış kurulum yine kesilmez, yalnızca KALAN paketler başlatılmaz.
                var check = checks[m.Key];
                var moduleToken = m is IStopsBetweenItems ? ct : CancellationToken.None;
                _stepContext = new StepContext(m.Key, m.DisplayName, rep.Step, 100.0 * i / Math.Max(1, targets.Count), 100.0 / Math.Max(1, targets.Count));
                var r = await SafeRunAsync(() => m.UpdateAsync(check, moduleToken), m, OperationKind.Update, isCheck: false, CancellationToken.None);
                _stepContext = null;
                results[m.Key] = r;
                rep.ModuleState.Report(r);
            }
        }
        finally
        {
            _activity = null;
            _stepContext = null;
        }

        if (mode == RunMode.All && !cleanRecycleBin && keys.Contains(ComponentKeys.RecycleBin) &&
            checks.TryGetValue(ComponentKeys.RecycleBin, out var rb) && rb.HasActionableUpdates)
        {
            var skipped = new ModuleResult
            {
                Key = ComponentKeys.RecycleBin,
                Status = ComponentStatus.Skipped,
                Summary = L.T("Temizlenmedi (seçilmedi)", "Not emptied (not selected)"),
                Details = rb.Details,
                Reason = L.T("Çöp kutusu temizliği seçilmediği için dokunulmadı.", "Not touched because emptying the Recycle Bin was not selected.")
            };
            results[ComponentKeys.RecycleBin] = skipped;
            rep.ModuleState.Report(skipped);
        }

        rep.Step.Report(new StepProgress(L.T("İşlem tamamlandı", "Operation completed"), 100));
        LogUpdateOutcome(mode switch
        {
            RunMode.Selected => L.T("Seçilen işlemler tamamlandı", "Selected operations completed"),
            RunMode.Single => L.T("İşlem tamamlandı", "Operation completed"),
            _ => L.T("Güncelleme işlemleri tamamlandı", "Update operations completed")
        }, results);
        return results;
    }

    /// <summary>Güncelleme bitiş mesajını GERÇEK sonuçlara göre yazar (hata/uyarı varken yalnızca "tamamlandı" denmez).</summary>
    private void LogUpdateOutcome(string text, IReadOnlyDictionary<string, ModuleResult> results)
    {
        var errors = results.Values.Count(r => r.Status is ComponentStatus.Failed or ComponentStatus.CheckFailed or ComponentStatus.AdminRequired);
        var warnings = results.Values.Count(r => r.Status is ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired or ComponentStatus.Attention);
        if (errors > 0 || warnings > 0) _logger.Warning(L.T($"{text} – {errors} hata, {warnings} uyarı.", $"{text} – {errors} error(s), {warnings} warning(s)."));
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
            _logger.Error(L.T("Yönetici yetkisi olmadan güncelleme yapılamaz.", "Updates cannot be run without administrator rights."));
            return null;
        }

        _activity = rep.Activity;
        try
        {
            _logger.Info(L.T($"{module.DisplayName}: seçilen manuel güncellemeler başlatıldı ({ids.Count} paket).", $"{module.DisplayName}: selected manual updates started ({ids.Count} package(s))."));
            rep.Step.Report(new StepProgress(L.T($"{module.DisplayName}: seçilen manuel güncellemeler uygulanıyor...", $"{module.DisplayName}: applying the selected manual updates..."), 5));
            rep.ModuleState.Report(new ModuleResult { Key = module.Key, Status = ComponentStatus.Updating, Summary = L.T("Manuel güncelleme uygulanıyor...", "Applying manual update...") });
            // Kaldırma/kurulum başladıktan sonra yarıda kesilmez.
            _stepContext = new StepContext(module.Key, module.DisplayName, rep.Step, 5, 90);
            var r = await SafeRunAsync(() => manual.UpdateManualAsync(check, ids, CancellationToken.None),
                module, OperationKind.Update, isCheck: false, CancellationToken.None);
            _stepContext = null;
            rep.ModuleState.Report(r);
            rep.Step.Report(new StepProgress(L.T("İşlem tamamlandı", "Operation completed"), 100));
            LogUpdateOutcome(L.T($"{module.DisplayName}: manuel güncellemeler tamamlandı", $"{module.DisplayName}: manual updates completed"), new Dictionary<string, ModuleResult> { [r.Key] = r });
            return r;
        }
        finally
        {
            _activity = null;
            _stepContext = null;
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
            _logger.Error(L.T("Yönetici yetkisi olmadan güncelleme yapılamaz.", "Updates cannot be run without administrator rights."));
            return null;
        }

        _activity = rep.Activity;
        try
        {
            rep.Step.Report(new StepProgress(L.T($"{module.DisplayName}: çalışan uygulamalar kapatılıp güncelleme yeniden deneniyor...", $"{module.DisplayName}: closing running apps and retrying the update..."), 5));
            rep.ModuleState.Report(new ModuleResult { Key = module.Key, Status = ComponentStatus.Updating, Summary = L.T("Yeniden deneniyor...", "Retrying...") });
            // Kapatma ve kurulum başladıktan sonra yarıda kesilmez.
            _stepContext = new StepContext(module.Key, module.DisplayName, rep.Step, 5, 90);
            var r = await SafeRunAsync(() => retry.RetryAfterClosingAsync(previous, approved, CancellationToken.None),
                module, OperationKind.Update, isCheck: false, CancellationToken.None);
            _stepContext = null;
            rep.ModuleState.Report(r);
            rep.Step.Report(new StepProgress(L.T("İşlem tamamlandı", "Operation completed"), 100));
            LogUpdateOutcome(L.T($"{module.DisplayName}: yeniden deneme tamamlandı", $"{module.DisplayName}: retry completed"), new Dictionary<string, ModuleResult> { [r.Key] = r });
            return r;
        }
        finally
        {
            _activity = null;
            _stepContext = null;
        }
    }

    // ================================================================= KART EYLEMİ (SFC / DISM / MRT)

    /// <summary>Bir bakım kartının kendi butonundaki işlemi çalıştırır.</summary>
    public async Task<ModuleResult> RunMaintenanceActionAsync(string key, OrchestratorReporters rep, CancellationToken ct)
    {
        if (Find(key) is not IMaintenanceModule m)
            return ModuleResult.CheckFailed(key, L.T("Bu kart için çalıştırılabilir bir işlem yok.", "There is no runnable operation for this card."));

        if (!AdminPrivilegeManager.IsElevated)
        {
            _logger.Error(L.T($"{m.DisplayName}: yönetici yetkisi yok – işlem yapılmadı.", $"{m.DisplayName}: no administrator rights – no operation performed."));
            var denied = AdminRequiredResult(key);
            rep.ModuleState.Report(denied);
            return denied;
        }

        _activity = rep.Activity;
        try
        {
            rep.Step.Report(new StepProgress(ActionTexts[key], 5));
            rep.ModuleState.Report(new ModuleResult { Key = key, Status = ComponentStatus.Updating, Summary = RunningText(key, check: key != ComponentKeys.Sfc) });
            _stepContext = new StepContext(key, m.DisplayName, rep.Step, 5, 90);
            var r = await SafeRunAsync(() => m.RunActionAsync(ct), m, OperationKind.Action, isCheck: key != ComponentKeys.Sfc, ct);
            _stepContext = null;
            rep.ModuleState.Report(r);
            rep.Step.Report(new StepProgress(L.T("İşlem tamamlandı", "Operation completed"), 100));
            return r;
        }
        finally
        {
            _activity = null;
            _stepContext = null;
        }
    }

    // ================================================================= yardımcılar

    private void LogSelection(IReadOnlyCollection<string> keys, string header)
    {
        _logger.Info(header);
        foreach (var m in Modules)
        {
            if (keys.Contains(m.Key)) _logger.Info(L.T($"{m.DisplayName} seçildi.", $"{m.DisplayName} selected."));
            else _logger.Output(L.T($"{m.DisplayName} seçilmedi, atlandı.", $"{m.DisplayName} not selected, skipped."));
        }
    }

    private static ModuleResult AdminRequiredResult(string key) => new()
    {
        Key = key,
        Status = ComponentStatus.AdminRequired,
        Summary = L.T("Yönetici izni gerekli", "Administrator permission required"),
        Reason = L.T("Uygulama yönetici yetkisiyle çalışmıyor.", "The app is not running with administrator rights.")
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
            _logger.Warning(L.T($"{m.DisplayName}: işlem iptal edildi.", $"{m.DisplayName}: operation cancelled."));
            result = new ModuleResult
            {
                Key = m.Key,
                Status = ComponentStatus.Skipped,
                Summary = L.T("İptal edildi", "Cancelled"),
                Reason = L.T("İşlem kullanıcı tarafından iptal edildi.", "The operation was cancelled by the user.")
            };
        }
        catch (Exception ex)
        {
            var reason = L.T($"Beklenmeyen hata: {ex.Message}", $"Unexpected error: {ex.Message}");
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
        _logger.Info(L.T($"{m.DisplayName}: işlem süresi {FormatDuration(watch.Elapsed)}.", $"{m.DisplayName}: operation took {FormatDuration(watch.Elapsed)}."));
        return result;
    }

    /// <summary>Süreyi "4 dk 21 sn" biçiminde yazar.</summary>
    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? L.T($"{(int)d.TotalHours} sa {d.Minutes} dk", $"{(int)d.TotalHours} h {d.Minutes} min")
        : d.TotalMinutes >= 1 ? L.T($"{(int)d.TotalMinutes} dk {d.Seconds} sn", $"{(int)d.TotalMinutes} min {d.Seconds} sec")
        : d.TotalSeconds >= 1 ? L.T($"{(int)d.TotalSeconds} sn", $"{(int)d.TotalSeconds} sec")
        : $"{Math.Max(1, (int)d.TotalMilliseconds)} ms";

    private void MarkSkipped(IUpdateModule m, Dictionary<string, ModuleResult> results, IProgress<ModuleResult> state, string reason)
    {
        var r = new ModuleResult { Key = m.Key, Status = ComponentStatus.Skipped, Summary = L.T("Atlandı", "Skipped"), Reason = reason };
        results[m.Key] = r;
        state.Report(r);
    }

    internal enum NetworkState { Verified, Limited, NotVerified, NoNetwork }

    /// <summary>Windows'un kendi bağlantı testi adresiyle (msftconnecttest) internet bağlantısını doğrular; sonucu döndürür.</summary>
    private async Task<NetworkState> CheckNetworkAsync(CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _logger.Error(L.T("Ağ bağlantısı bulunamadı. Çevrimiçi kontroller başarısız olacaktır.", "No network connection found. Online checks will fail."));
            return NetworkState.NoNetwork;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var text = await _http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt", cts.Token);
            if (text.Contains("Microsoft Connect Test", StringComparison.Ordinal))
            {
                _logger.Success(L.T("İnternet bağlantısı doğrulandı.", "Internet connection verified."));
                return NetworkState.Verified;
            }
            _logger.Warning(L.T("İnternet bağlantısı sınırlı görünüyor (yakalama portalı / proxy olabilir).", "The internet connection looks limited (possibly a captive portal / proxy)."));
            return NetworkState.Limited;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Warning(L.T($"İnternet bağlantısı doğrulanamadı: {ex.Message}", $"The internet connection could not be verified: {ex.Message}"));
            return NetworkState.NotVerified;
        }
    }

    /// <summary>
    /// İnternet doğrulanamadıysa winget sonucunu dürüstçe etiketler: winget kaynağı güncellenemediğinde kendi ÖNBELLEĞİNDEKİ paket
    /// listesiyle sonuç verebilir (eski olabilir). "Güncel" böyle bir durumda "Dikkat: güncel görünüyor" olur; neden yazılır. Diğer
    /// çevrimiçi kartlar (Windows Update, Store, NVIDIA) internetsiz zaten gerçek hatayla "kontrol edilemedi" olur; Defender kendi
    /// sonucunu ayrıca etiketler. Değişiklik yoksa aynı nesne döner.
    /// </summary>
    internal static ModuleResult ApplyNetworkCaveat(ModuleResult r, NetworkState network)
    {
        if (network == NetworkState.Verified || r.Key != ComponentKeys.Winget ||
            r.Status is not (ComponentStatus.UpToDate or ComponentStatus.UpdateAvailable or ComponentStatus.Attention))
            return r;
        var why = network switch
        {
            NetworkState.NoNetwork => L.T("ağ bağlantısı yok", "no network connection"),
            NetworkState.Limited => L.T("internet bağlantısı sınırlı", "internet connection limited"),
            _ => L.T("internet bağlantısı doğrulanamadı", "internet connection could not be verified")
        };
        var note = L.T($"Dikkat: {why}. Winget bu durumda kendi önbelleğindeki paket listesini kullanabilir; sonuç güncel olmayabilir. ", $"Attention: {why}. In this case winget may use the package list from its own cache; the result may be out of date. ") +
                   L.T("Bağlantıyı kontrol edip yeniden kontrol edin.", "Check the connection and check again.");
        var upToDate = r.Status == ComponentStatus.UpToDate;
        return new ModuleResult
        {
            Key = r.Key,
            Status = upToDate ? ComponentStatus.Attention : r.Status,
            Summary = upToDate ? L.T($"Güncel görünüyor – {why}", $"Looks up to date – {why}") : r.Summary,
            Details = r.Details,
            Reason = string.IsNullOrEmpty(r.Reason) ? note : r.Reason + "\n" + note,
            Items = r.Items,
            ActionableCount = r.ActionableCount,
            RebootRequired = r.RebootRequired,
            Operation = r.Operation,
            CompletedAt = r.CompletedAt,
            Duration = r.Duration,
            Commands = r.Commands,
            Notes = r.Notes
        };
    }

    public void Dispose() => _http.Dispose();
}

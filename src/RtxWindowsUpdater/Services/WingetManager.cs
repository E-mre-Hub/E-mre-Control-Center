using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows Paket Yöneticisi (winget) entegrasyonu. Komutlar cmd.exe üzerinden çalıştırılır (bkz. <see cref="CmdCommand"/>).
/// Kontrol : winget upgrade --source &lt;kaynak&gt;   (+ winget list ile güncel paketler)
/// Güncelle: winget upgrade --id &lt;Id&gt; --exact --silent ...  (yalnızca kontrolde bulunan paketler, tek tek)
///           ardından TEK bir "winget upgrade" ile gerçek doğrulama.
/// Aynı sınıf "msstore" kaynağı ile Microsoft Store uygulamaları için de kullanılır.
/// Hata kodları winget'in resmi dönüş kodu belgesine (APPINSTALLER_CLI_ERROR_*) göre yorumlanır;
/// winget'in kendi mesajı ve (varsa) kurulum programının çıkış kodu her zaman korunur.
/// </summary>
public sealed class WingetManager(Logger logger, string source, string key, string displayName)
    : IUpdateModule, IInUseRetryModule, IManualUpdateModule, IProgressReportingModule, IStopsBetweenItems
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Doğrulamada hâlâ görünen paket için ek doğrulama sayısı ve aralığı (arka planda kuran programlar; test kısaltır).</summary>
    internal static int VerifyRetries = 4;
    internal static TimeSpan VerifyRetryDelay = TimeSpan.FromSeconds(15);

    public string Key => key;
    public string DisplayName => displayName;
    public string Source => source;

    private static readonly string[] CommonArgs = ["--accept-source-agreements", "--disable-interactivity"];

    /// <summary>cmd.exe'ye güvenle verilebilecek paket kimliği biçimi.</summary>
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]*$", RegexOptions.Compiled);

    /// <summary>winget çıktısındaki "Installer failed with exit code: 6" gibi gerçek kurulum programı çıkış kodu.</summary>
    private static readonly Regex InstallerExitCodeRegex = new(@"exit code:?\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly object VersionLock = new();
    private static string? _cachedVersion;

    /// <summary>
    /// Winget'in GERÇEKTEN 0x8A15008E (kurulum teknolojisi uyuşmazlığı) döndürdüğü paketler: "kaynak|Id" → hedef sürüm.
    /// Aynı sürüm yeniden listelendiğinde otomatik güncellemeye alınmaz (her denemede kesin başarısız olacağı için); yeni bir
    /// sürüm çıkarsa eşleşmez ve yeniden denenir. Kayıt state.json'da saklanır (uygulama yeniden açılınca da geçerli).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TechnologyMismatch =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kayıtlı uyuşmazlıkları (önceki oturumlardan) yükler.</summary>
    public static void LoadKnownTechnologyMismatches(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (k, v) in entries)
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) TechnologyMismatch[k] = v;
    }

    /// <summary>Şu anki uyuşmazlık kayıtlarının kopyası (kalıcı saklama için).</summary>
    public static IReadOnlyDictionary<string, string> KnownTechnologyMismatches =>
        new Dictionary<string, string>(TechnologyMismatch, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Winget'in başarı bildirdiği ama doğrulamada kurulu sürümün HİÇ değişmediği (ve dosyaları kullanan açık uygulama olmayan) paketler:
    /// "kaynak|Id" → "kurulu|sunulan". 2026-10-07 KULLANICI GÜNLÜĞÜ: Google Play Games iki kez "Successfully installed" dedi, kayıtlı sürüm
    /// 26.9.555.1'de kaldı (winget kataloğundaki 156.0.8067.0 Google güncelleyicisinin sürümü) → her güncellemede yeniden kurulup 60 sn
    /// doğrulama bekleniyordu. Aynı çift otomatik denenmez (manuel "yeniden dene"); kurulu ya da sunulan sürüm değişince kayıt geçersizdir.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> NoVersionChange =
        new(StringComparer.OrdinalIgnoreCase);

    private static string NoChangeValue(string installed, string available) => installed + "|" + available;

    public static void LoadKnownNoVersionChange(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (k, v) in entries)
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) NoVersionChange[k] = v;
    }

    public static IReadOnlyDictionary<string, string> KnownNoVersionChange =>
        new Dictionary<string, string>(NoVersionChange, StringComparer.OrdinalIgnoreCase);

    private const uint InstallTechnologyMismatch = 0x8A15008E;

    /// <param name="Symbol">Resmi sembol (APPINSTALLER_CLI_ERROR_ öneki olmadan).</param>
    /// <param name="Text">Kullanıcıya gösterilen Türkçe açıklama.</param>
    /// <param name="Short">Detaylı Sonuç panelindeki kısa sonuç başlığı.</param>
    private sealed record WingetCode(string Symbol, string Text, string Short);

    // winget resmi dönüş kodları (doc/windows/package-manager/winget/returnCodes.md)
    private const uint UpdateNotApplicable = 0x8A15002B;
    private const uint NoApplicationsFound = 0x8A150014;
    private const uint RebootRequiredToFinish = 0x8A150109;
    private const uint RebootInitiated = 0x8A15010B;
    private const uint InstallPackageInUse = 0x8A150101;
    private const uint InstallFileInUse = 0x8A150103;
    private const uint InstallPackageInUseByApplication = 0x8A150111;

    private static readonly Dictionary<uint, WingetCode> Codes = new()
    {
        [InstallTechnologyMismatch] = new("UPDATE_INSTALL_TECHNOLOGY_MISMATCH",
            "Güncelleme bulundu ancak mevcut kurulum teknolojisi ile yeni sürümün kurulum teknolojisi farklı. " +
            "Winget otomatik yükseltemiyor; paketi kaldırıp yeni sürümü kurmak gerekir.",
            "Kurulum teknolojisi uyuşmazlığı"),
        [InstallPackageInUseByApplication] = new("INSTALL_PACKAGE_IN_USE_BY_APPLICATION",
            "Kurulum programı, uygulamanın başka bir uygulama tarafından kullanıldığını bildirdi. İlgili uygulamaları kapatıp tekrar deneyin.",
            "Uygulama/kurulum programının kullandığı dosyalar kullanımda"),
        [InstallPackageInUse] = new("INSTALL_PACKAGE_IN_USE", "Uygulama şu anda çalışıyor. Uygulamayı kapatıp tekrar deneyin.",
            "Uygulama çalışıyor"),
        [0x8A150102] = new("INSTALL_INSTALL_IN_PROGRESS", "Başka bir kurulum sürüyor. Daha sonra tekrar deneyin.",
            "Başka bir kurulum sürüyor"),
        [InstallFileInUse] = new("INSTALL_FILE_IN_USE", "Bir veya daha fazla dosya kullanımda. Uygulamayı kapatıp tekrar deneyin.",
            "Dosyalar kullanımda"),
        [0x8A150104] = new("INSTALL_MISSING_DEPENDENCY", "Paketin sistemde eksik bir bağımlılığı var.", "Eksik bağımlılık"),
        [0x8A150105] = new("INSTALL_DISK_FULL", "Diskte yeterli alan yok.", "Disk dolu"),
        [0x8A150106] = new("INSTALL_INSUFFICIENT_MEMORY", "Kurulum için yeterli bellek yok.", "Yetersiz bellek"),
        [0x8A150107] = new("INSTALL_NO_NETWORK", "Kurulum internet bağlantısı gerektiriyor.", "İnternet bağlantısı gerekli"),
        [0x8A150108] = new("INSTALL_CONTACT_SUPPORT", "Kurulum sırasında hata oluştu; üretici desteğine başvurulmalı.",
            "Kurulum hatası (üretici desteği gerekli)"),
        [0x8A150109] = new("INSTALL_REBOOT_REQUIRED_TO_FINISH", "Kurulumun tamamlanması için yeniden başlatma gerekiyor.",
            "Yeniden başlatma gerekli"),
        [0x8A15010A] = new("INSTALL_REBOOT_REQUIRED_FOR_INSTALL", "Kurulum başarısız: bilgisayarı yeniden başlatıp tekrar deneyin.",
            "Kurulumdan önce yeniden başlatma gerekli"),
        [0x8A15010B] = new("INSTALL_REBOOT_INITIATED", "Kurulum programı, kurulumu tamamlamak için yeniden başlatma bildirdi.",
            "Kurulum yeniden başlatma bildirdi"),
        [0x8A15010C] = new("INSTALL_CANCELLED_BY_USER", "Kurulum iptal edildi.", "Kurulum iptal edildi"),
        [0x8A15010D] = new("INSTALL_ALREADY_INSTALLED", "Uygulamanın başka bir sürümü zaten kurulu.", "Başka bir sürüm kurulu"),
        [0x8A15010E] = new("INSTALL_DOWNGRADE", "Daha yüksek bir sürüm zaten kurulu.", "Daha yüksek sürüm kurulu"),
        [0x8A15010F] = new("INSTALL_BLOCKED_BY_POLICY", "Kurum ilkeleri kurulumu engelliyor.", "Kurum ilkesi engelliyor"),
        [0x8A150110] = new("INSTALL_DEPENDENCIES", "Paket bağımlılıkları kurulamadı.", "Bağımlılıklar kurulamadı"),
        [0x8A150112] = new("INSTALL_INVALID_PARAMETER", "Kurulum programı geçersiz parametre bildirdi.", "Geçersiz kurulum parametresi"),
        [0x8A150113] = new("INSTALL_SYSTEM_NOT_SUPPORTED", "Paket bu sistemi desteklemiyor.", "Sistem desteklenmiyor"),
        [0x8A150114] = new("INSTALL_UPGRADE_NOT_SUPPORTED", "Kurulum programı mevcut paketin yükseltilmesini desteklemiyor.",
            "Yükseltme desteklenmiyor"),
        [0x8A150115] = new("INSTALL_CUSTOM_ERROR", "Kurulum programı özel bir hata kodu döndürdü.", "Kurulum programı özel hata"),
        [0x8A15002B] = new("UPDATE_NOT_APPLICABLE", "Uygulanabilir güncelleme bulunamadı.", "Uygulanabilir güncelleme yok"),
        [0x8A150014] = new("NO_APPLICATIONS_FOUND", "Paket bulunamadı.", "Paket bulunamadı"),
        [0x8A150011] = new("INSTALLER_HASH_MISMATCH", "İndirilen kurulum dosyasının karması manifestle eşleşmiyor; güvenlik nedeniyle kurulmadı.",
            "Kurulum dosyası karması eşleşmiyor"),
        [0x8A150010] = new("NO_APPLICABLE_INSTALLER", "Bu sistem için uygun kurulum programı yok.", "Uygun kurulum programı yok")
    };

    private static string? _packagedWinget;

    /// <summary>
    /// winget'in yolu. Önce App Installer paketinin KORUMALI kurulum klasörü (C:\Program Files\WindowsApps\…; yalnızca TrustedInstaller
    /// yazabilir; yer Windows'un paket API'sinden okunur), olmazsa Windows'un standart uygulama yürütme takma adı
    /// (%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe). PATH'teki diğer klasörler ARANMAZ: uygulama yönetici olarak çalışırken
    /// kullanıcının yazabildiği bir klasöre konmuş sahte bir "winget.exe" yönetici yetkisiyle çalıştırılmasın.
    /// </summary>
    public static string? LocateWinget()
    {
        var cached = _packagedWinget;
        if (cached is not null && File.Exists(cached)) return cached;
        var packaged = PackagedWingetPath();
        if (packaged is not null)
        {
            _packagedWinget = packaged;
            return packaged;
        }
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    /// <summary>winget korumalı paket klasöründen mi bulundu (günlük için).</summary>
    public static bool IsPackagedPath(string path) =>
        path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\",
            StringComparison.OrdinalIgnoreCase);

    private static string? PackagedWingetPath()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            var best = manager.FindPackagesForUser(string.Empty, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe")
                .Where(p => !p.IsFramework && !p.IsResourcePackage)
                .OrderByDescending(p => new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision))
                .FirstOrDefault();
            if (best is null) return null;
            var exe = Path.Combine(best.InstalledPath, "winget.exe");
            return IsPackagedPath(exe) && File.Exists(exe) ? exe : null;
        }
        catch (Exception)
        {
            return null; // paket API'si okunamadı: standart takma ada düşülür
        }
    }

    private static string[] Args(params string[] args) => [.. args, .. CommonArgs];

    private Task<ProcessResult> RunWingetAsync(string winget, string[] args, TimeSpan timeout, CancellationToken ct, bool forward = false,
        Action<string>? onLine = null)
    {
        Action<string>? callback = forward || onLine is not null
            ? line =>
            {
                if (forward) ForwardOutput(line);
                onLine?.Invoke(line);
            }
            : null;
        return ProcessRunner.RunCmdAsync(winget, args, timeout, ct, onStdOut: callback, onStdErr: callback);
    }

    /// <summary>Güncelleme sürerken kartta ve genel ilerleme çubuğunda gösterilen canlı durum (paket sırası + aşama).</summary>
    public event Action<ModuleProgress>? ProgressChanged;

    private void ReportProgress(string text, double? percent)
    {
        try { ProgressChanged?.Invoke(new ModuleProgress(key, text, percent)); } catch { /* UI bildirimi */ }
    }

    /// <summary>
    /// Tek bir paketin güncellemesi sürerken ilerleme bildirir (2026-10-07 KULLANICI SORUNU: 7 paketlik güncelleme yaklaşık 25 dk sürdü, çubuk
    /// bu sürede %0'da kaldı ve uygulama "dondu" sanıldı). Gösterilenler YALNIZCA gerçek bilgiler: paket sırası (n/toplam), winget'in
    /// kendi aşama satırları (indiriliyor / kurulum dosyası doğrulandı / kuruluyor), winget boyut veya yüzde yazdıysa o değer, aksi hâlde
    /// aşamada geçen süre (5 sn'de bir). Yüzde = tamamlanan paket sayısı; tahmini süre veya uydurma yüzde yok.
    /// </summary>
    private sealed class PackageProgress : IDisposable
    {
        private static readonly Regex Sizes = new(@"(\d+(?:[.,]\d+)?)\s*(B|KB|MB|GB)\s*/\s*(\d+(?:[.,]\d+)?)\s*(B|KB|MB|GB)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Percent = new(@"(?<!\d)(\d{1,3})\s*%", RegexOptions.CultureInvariant);

        private readonly WingetManager _owner;
        private readonly string _prefix;
        private readonly double _percent;
        private readonly Stopwatch _phaseClock = Stopwatch.StartNew();
        private readonly Timer _timer;
        private readonly object _lock = new();
        private string _phase = "başlatılıyor";
        private string? _detail;
        private bool _disposed;

        public PackageProgress(WingetManager owner, int index, int total, string name)
        {
            _owner = owner;
            _prefix = $"{index + 1}/{total} · {name}";
            _percent = 100.0 * index / Math.Max(1, total);
            Publish();
            _timer = new Timer(_ => Publish(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void SetPhase(string phase)
        {
            lock (_lock)
            {
                if (_phase == phase) return;
                _phase = phase;
                _detail = null;
                _phaseClock.Restart();
            }
            try { _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)); } // yeni aşamanın süresi 5 sn sonra görünür
            catch (ObjectDisposedException) { return; }
            Publish();
        }

        /// <summary>Winget'in çıktı satırı: aşama satırları ve (yazıldıysa) indirme / kurulum ilerlemesi.</summary>
        public void OnLine(string line)
        {
            var cut = line.LastIndexOf('\r');
            var s = (cut >= 0 ? line[(cut + 1)..] : line).Trim();
            if (s.Length == 0) return;
            if (s.Contains('█') || s.Contains('▒'))
            {
                var size = Sizes.Match(s);
                var detail = size.Success
                    ? $"{FormatSize(size.Groups[1].Value)} {size.Groups[2].Value.ToUpperInvariant()} / {FormatSize(size.Groups[3].Value)} {size.Groups[4].Value.ToUpperInvariant()}"
                    : Percent.Match(s) is { Success: true } p ? "%" + p.Groups[1].Value : null;
                if (detail is null) return;
                lock (_lock) _detail = detail;
                return; // zamanlayıcı en geç 5 sn içinde gösterir (her ilerleme karesinde bildirim yapılmaz)
            }
            if (s.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)) SetPhase("indiriliyor");
            else if (s.Contains("verified installer hash", StringComparison.OrdinalIgnoreCase)) SetPhase("kurulum dosyası doğrulandı");
            else if (s.StartsWith("Starting package install", StringComparison.OrdinalIgnoreCase)) SetPhase("kuruluyor");
            else if (s.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase)) SetPhase("kuruldu");
        }

        private static string FormatSize(string value) =>
            double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v.ToString("0.0", CultureInfo.CurrentCulture)
                : value;

        private void Publish()
        {
            string text;
            lock (_lock)
            {
                if (_disposed) return;
                var elapsed = _phaseClock.Elapsed;
                var extra = _detail ?? (elapsed.TotalSeconds >= 1 ? FormatElapsed(elapsed) : null);
                text = extra is null ? $"{_prefix}: {_phase}" : $"{_prefix}: {_phase} ({extra})";
            }
            _owner.ReportProgress(text, _percent);
        }

        private static string FormatElapsed(TimeSpan t) =>
            t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} dk {t.Seconds} sn" : $"{t.Seconds} sn";

        public void Dispose()
        {
            lock (_lock) _disposed = true;
            _timer.Dispose();
        }
    }

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info($"{displayName}: winget kontrol ediliyor...");
        var winget = LocateWinget();
        if (winget is null)
        {
            const string reason = "Winget (Windows Paket Yöneticisi) bulunamadı. Microsoft Store'dan 'Uygulama Yükleyicisi' (App Installer) kurulmalı.";
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }

        // Sürüm bilgisi oturum başına bir kez okunur (her kontrolde gereksiz süreç başlatılmaz).
        string? version;
        lock (VersionLock) version = _cachedVersion;
        if (version is null)
        {
            var ver = await ProcessRunner.RunCmdAsync(winget, ["--version"], TimeSpan.FromSeconds(30), ct);
            if (!ver.Succeeded)
            {
                var reason = "Winget çalıştırılamadı: " + ProcessRunner.Describe(ver, "winget");
                logger.Error(reason);
                return ModuleResult.CheckFailed(key, reason);
            }
            version = ver.StdOut.Trim();
            lock (VersionLock) _cachedVersion = version;
        }
        logger.Success($"Winget bulundu ({version}; {(IsPackagedPath(winget) ? "korumalı paket klasörü" : "Windows uygulama takma adı")}).");

        logger.Info($"{displayName}: '{source}' kaynağında güncellemeler aranıyor...");
        var up = await RunWingetAsync(winget, Args("upgrade", "--source", source), ListTimeout, ct);
        if (!up.Started || up.TimedOut || up.Cancelled)
        {
            var reason = ProcessRunner.Describe(up, "winget");
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }

        var tables = WingetTableParser.Parse(up.StdOut);

        // Önceki sürümlerin Edge / WebView2 için sakladığı "kaldır + yeniden kur" kaydı geçersiz (kaldırılamazlar): Edge
        // güncel olsa bile silinir, CompleteOperation kalıcı kayıttan da kaldırır.
        if (source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            foreach (var id in EdgeUpdateService.WingetIds)
                TechnologyMismatch.TryRemove(source + "|" + id, out _);
        var exit = unchecked((uint)up.ExitCode);
        if (tables.Count == 0 && up.ExitCode != 0 && exit != NoApplicationsFound && exit != UpdateNotApplicable)
        {
            var reason = $"'{source}' kaynağı sorgulanamadı: " + DescribeFailure(up);
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }
        if (up.ExitCode != 0 && tables.Count > 0)
            logger.Warning($"winget uyarı koduyla döndü ({up.ExitCodeHex}); bulunan liste kullanılıyor.");

        var items = new List<UpdateItem>();
        var edgeNotes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                if (string.IsNullOrEmpty(row.Available) || !seen.Add(row.Id)) continue;
                if (IsEdgeManaged(row.Id, out var edge))
                {
                    items.Add(await CheckEdgeAsync(edge, row, edgeNotes, ct));
                    continue;
                }
                var mismatch = TechnologyMismatch.TryGetValue(source + "|" + row.Id, out var failedVersion) &&
                               string.Equals(failedVersion, row.Available, StringComparison.OrdinalIgnoreCase);
                var noChangeKey = source + "|" + row.Id;
                var noChange = false;
                if (NoVersionChange.TryGetValue(noChangeKey, out var recorded))
                {
                    noChange = !mismatch && string.Equals(recorded, NoChangeValue(row.Version, row.Available), StringComparison.OrdinalIgnoreCase);
                    if (!noChange) NoVersionChange.TryRemove(noChangeKey, out _); // kurulu ya da sunulan sürüm değişti: kayıt geçersiz
                }
                items.Add(new UpdateItem
                {
                    Name = row.Name,
                    Id = row.Id,
                    CurrentVersion = row.Version,
                    NewVersion = row.Available,
                    UpdateAvailable = true,
                    AutoUpdatable = !table.RequiresExplicitTargeting && !mismatch && !noChange,
                    Manual = mismatch ? ManualUpdateKind.TechnologyMismatch
                        : noChange ? ManualUpdateKind.NoVersionChange
                        : table.RequiresExplicitTargeting ? ManualUpdateKind.ExplicitTargeting
                        : ManualUpdateKind.None,
                    StatusText = mismatch
                        ? "Güncelleme mevcut – winget otomatik yükseltemiyor (kurulum teknolojisi farklı, 0x8A15008E); paketi kaldırıp yeni sürümü kurun"
                        : noChange
                            ? $"Güncelleme listeleniyor – önceki güncellemede winget başarı bildirdi ama kurulu sürüm değişmedi ({row.Version}); otomatik denenmez"
                            : table.RequiresExplicitTargeting
                                ? "Güncelleme mevcut (açık hedefleme gerekli – otomatik güncellenmez)"
                                : "Güncelleme mevcut"
                });
            }
        }

        // Artık güncelleme listesinde olmayan (güncellenmiş / kaldırılmış) paketlerin "sürüm değişmedi" kaydı silinir.
        foreach (var k in NoVersionChange.Keys.Where(k => k.StartsWith(source + "|", StringComparison.OrdinalIgnoreCase) &&
                                                          !seen.Contains(k[(source.Length + 1)..])).ToList())
            NoVersionChange.TryRemove(k, out _);

        var actionable = items.Count(i => i.AutoUpdatable);
        var mismatchCount = items.Count(i => i.Manual == ManualUpdateKind.TechnologyMismatch);
        var explicitCount = items.Count(i => i.Manual == ManualUpdateKind.ExplicitTargeting);
        var noChangeCount = items.Count(i => i.Manual == ManualUpdateKind.NoVersionChange);
        foreach (var i in items)
            logger.Info($"  {i.Name}: {i.CurrentVersion} → {i.NewVersion} ({i.StatusText})");

        // Güncel paketleri de göstermek için (yalnızca bilgi amaçlı, başarısız olması kontrolü bozmaz).
        var upToDate = new List<UpdateItem>();
        var list = await RunWingetAsync(winget, Args("list", "--source", source), ListTimeout, ct);
        if (list.Started && !list.TimedOut && !list.Cancelled)
        {
            foreach (var row in WingetTableParser.Parse(list.StdOut).SelectMany(t => t.Rows))
            {
                if (seen.Contains(row.Id)) continue;
                if (!string.IsNullOrEmpty(row.Available) &&
                    !row.Available.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(row.Id)) continue;
                upToDate.Add(new UpdateItem
                {
                    Name = row.Name,
                    Id = row.Id,
                    CurrentVersion = row.Version,
                    NewVersion = row.Version,
                    UpdateAvailable = false,
                    AutoUpdatable = false,
                    StatusText = "Güncel"
                });
            }
        }
        else
        {
            logger.Warning("Kurulu paket listesi alınamadı: " + ProcessRunner.Describe(list, "winget list"));
        }

        if (actionable > 0) logger.Warning($"{displayName}: {actionable} güncelleme bulundu.");
        else logger.Success($"{displayName}: otomatik uygulanabilir güncelleme bulunamadı.");
        if (explicitCount > 0)
            logger.Info($"{displayName}: {explicitCount} paket yalnızca açık hedeflemeyle güncellenebilir (sabitlenmiş/özel paket); otomatik güncellenmeyecek.");
        if (mismatchCount > 0)
            logger.Warning($"{displayName}: {mismatchCount} paket bu oturumda kurulum teknolojisi uyuşmazlığı (0x8A15008E) bildirdiği için " +
                           "otomatik güncellemeye alınmadı; bu paketler kaldırılıp yeni sürüm kurularak güncellenebilir.");
        if (noChangeCount > 0)
            logger.Info($"{displayName}: {noChangeCount} paket önceki güncellemede sürüm değiştirmediği için otomatik denenmeyecek (manuel olarak yeniden denenebilir).");

        // Otomatik uygulanabilir ve manuel (açık hedefleme / teknoloji uyuşmazlığı) güncellemeler ayrı sayılır;
        // manuel olanlar "güncelleme bulundu" sayısına ve "Tümünü Güncelle"ye dahil edilmez.
        var manual = explicitCount + mismatchCount + noChangeCount;
        var details = $"Otomatik uygulanabilir: {actionable}";
        var edgeCount = items.Count(i => i.UpdateAvailable && IsEdgeManaged(i.Id, out _));
        if (edgeCount > 0) details += $"\nMicrosoft Edge Update ile güncellenecek: {edgeCount}";
        if (explicitCount > 0) details += $"\nManuel / açık hedefleme gerekli: {explicitCount}";
        if (mismatchCount > 0) details += $"\nManuel – kurulum teknolojisi farklı (0x8A15008E): {mismatchCount}";
        if (noChangeCount > 0) details += $"\nManuel – önceki güncellemede sürüm değişmedi: {noChangeCount}";
        if (upToDate.Count > 0) details += $"\nGüncel paket: {upToDate.Count}";
        var reasonText = string.Join("\n", new[] { ManualReason(items, mismatchCount, explicitCount, noChangeCount) }.Concat(edgeNotes)
            .Where(l => !string.IsNullOrEmpty(l)));

        return new ModuleResult
        {
            Key = key,
            // Yalnızca manuel güncellemeler varken "Güncel" denmez.
            Status = actionable > 0 ? ComponentStatus.UpdateAvailable
                : manual > 0 ? ComponentStatus.Attention : ComponentStatus.UpToDate,
            Summary = actionable > 0
                ? (manual > 0 ? $"{actionable} güncelleme mevcut (+{manual} manuel)" : $"{actionable} güncelleme mevcut")
                : manual > 0 ? $"Dikkat: {manual} güncelleme otomatik uygulanamıyor" : "Güncel",
            Reason = reasonText.Length > 0 ? reasonText : null,
            Details = details,
            Items = items.Concat(upToDate).ToList(),
            ActionableCount = actionable
        };
    }

    /// <summary>Manuel güncellemelerin (otomatik uygulanmayan) paket bazında nedeni.</summary>
    private static string? ManualReason(List<UpdateItem> items, int mismatchCount, int explicitCount, int noChangeCount = 0)
    {
        var lines = new List<string>();
        if (noChangeCount > 0)
            lines.Add("Önceki güncellemede winget kurulumun başarılı olduğunu bildirdi ama kurulu sürüm değişmedi; bu paketler otomatik denenmiyor: " +
                      string.Join(", ", items.Where(i => i.Manual == ManualUpdateKind.NoVersionChange)
                                             .Select(i => $"{i.Name} ({i.CurrentVersion} → {i.NewVersion})")) +
                      ". Winget kataloğundaki sürüm numarası uygulamanın kendi bildirdiği sürümden farklı olabilir (ör. kurulum programı yalnızca " +
                      "uygulamanın güncelleyicisini çalıştırıyor). Uygulama kendini güncelliyorsa ek işlem gerekmez; isterseniz manuel olarak yeniden deneyebilirsiniz.");
        if (mismatchCount > 0)
            lines.Add("Kurulum teknolojisi farklı olduğu için winget bu paketleri yerinde yükseltemiyor (0x8A15008E): " +
                      string.Join(", ", items.Where(i => i.Manual == ManualUpdateKind.TechnologyMismatch)
                                             .Select(i => $"{i.Name} ({i.CurrentVersion} → {i.NewVersion})")) +
                      ". Çözüm: mevcut sürümü kaldırıp yeni sürümü kurmak (kartın \"Kontrol Et\" butonundaki manuel güncelleme seçeneği).");
        if (explicitCount > 0)
            lines.Add("Yalnızca açık hedeflemeyle güncellenen paketler (genelde kendini güncelleyen uygulamalar) otomatik güncellenmez: " +
                      string.Join(", ", items.Where(i => i.Manual == ManualUpdateKind.ExplicitTargeting)
                                             .Select(i => $"{i.Name} ({i.CurrentVersion} → {i.NewVersion})")) +
                      ". Çözüm: uygulamayı açmak (kendini günceller) veya kartın \"Kontrol Et\" butonundaki manuel güncelleme seçeneği.");
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    // ------------------------------------------------------------------ MICROSOFT EDGE / WEBVIEW2 (Microsoft Edge Update)

    private static readonly TimeSpan EdgeCheckTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EdgeInstallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Kurulum çağrısı (Harness3, sonuç yorumlamasını gerçek kurulum yapmadan denemek için yansımayla değiştirir).</summary>
    private static Func<EdgeUpdateService.EdgeApp, Action<string>?, TimeSpan, Task<EdgeUpdateService.EdgeUpdateResult>> _edgeInstall =
        EdgeUpdateService.InstallAsync;

    /// <summary>
    /// Microsoft Edge ve WebView2 Çalışma Zamanı winget ile değil Microsoft Edge Update ile güncellenir: Windows 11'de
    /// kaldırılamazlar (Edge kurulum programı çıkış kodu 93) ve winget yerinde yükseltemez (0x8A15008E).
    /// </summary>
    private bool IsEdgeManaged(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EdgeUpdateService.EdgeApp? edge)
    {
        edge = source.Equals("winget", StringComparison.OrdinalIgnoreCase) ? EdgeUpdateService.Find(id) : null;
        return edge is not null;
    }

    /// <summary>
    /// winget'in listelediği Edge / WebView2 güncellemesini Microsoft Edge Update'e sorar (yalnızca denetim, sistem değişmez):
    /// yeni sürüm bu cihaza gerçekten sunuluyorsa güncellenebilir olarak, sunulmuyorsa güncel olarak (nedeniyle) listelenir.
    /// </summary>
    private async Task<UpdateItem> CheckEdgeAsync(EdgeUpdateService.EdgeApp edge, WingetRow row, List<string> notes, CancellationToken ct)
    {
        // Önceki sürümlerin "kaldır + yeniden kur" kaydı bu paketlerde geçersiz (kaldırılamazlar); kalıcı kayıttan da silinir.
        TechnologyMismatch.TryRemove(source + "|" + row.Id, out _);

        logger.Info($"{row.Name}: winget {row.Version} → {row.Available} listeliyor; Microsoft Edge Update'e soruluyor...");
        var r = await EdgeUpdateService.CheckAsync(edge, EdgeCheckTimeout, ct);
        ExecutionTrace.Note($"{row.Name}: Microsoft Edge Update denetimi – {r.Outcome}" +
                            (r.AvailableVersion is null ? "" : $", sunulan sürüm {r.AvailableVersion}") +
                            (r.Outcome is EdgeUpdateService.EdgeUpdateOutcome.Error or EdgeUpdateService.EdgeUpdateOutcome.Unavailable
                                ? " – " + r.Describe() : ""));
        switch (r.Outcome)
        {
            case EdgeUpdateService.EdgeUpdateOutcome.UpdateAvailable:
            {
                var target = string.IsNullOrWhiteSpace(r.AvailableVersion) ? row.Available : r.AvailableVersion;
                logger.Info($"{row.Name}: Microsoft Edge Update bu cihaza {target} sürümünü sunuyor.");
                return new UpdateItem
                {
                    Name = row.Name, Id = row.Id, CurrentVersion = row.Version, NewVersion = target,
                    UpdateAvailable = true, AutoUpdatable = true,
                    StatusText = "Güncelleme mevcut – Microsoft Edge Update ile güncellenecek (Edge Windows bileşenidir; " +
                                 "kaldırılamaz ve winget yerinde yükseltemez)"
                };
            }
            case EdgeUpdateService.EdgeUpdateOutcome.NoUpdate:
                logger.Info($"{row.Name}: Microsoft Edge Update bu cihaz için yeni sürüm sunmuyor (winget {row.Available} listeliyor).");
                notes.Add($"{row.Name}: winget {row.Available} sürümünü listeliyor ancak Microsoft Edge Update bu sürümü bu cihaza henüz " +
                          "sunmuyor (Microsoft güncellemeleri kademeli dağıtır); sunulduğunda Edge kendini günceller.");
                return new UpdateItem
                {
                    Name = row.Name, Id = row.Id, CurrentVersion = row.Version, NewVersion = row.Version,
                    UpdateAvailable = false, AutoUpdatable = false,
                    StatusText = $"Güncel (Microsoft Edge Update'e göre) – winget {row.Available} listeliyor, Microsoft bu cihaza henüz sunmadı"
                };
            default:
            {
                var reason = r.Describe();
                logger.Warning($"{row.Name}: Microsoft Edge Update'e sorulamadı: {reason}");
                return new UpdateItem
                {
                    Name = row.Name, Id = row.Id, CurrentVersion = row.Version, NewVersion = row.Available,
                    UpdateAvailable = true, AutoUpdatable = true,
                    StatusText = $"Güncelleme mevcut (winget) – Microsoft Edge Update ile güncellenecek; Edge Update denetlenemedi: {reason}"
                };
            }
        }
    }

    /// <summary>
    /// Edge / WebView2'yi Microsoft Edge Update ile günceller (Edge'in "Hakkında" sayfasıyla aynı resmi akış) ve sonucu kayıt
    /// defterindeki kurulu sürümle doğrular. Doğrulanmayan kurulum başarılı sayılmaz. Edge kaldırılmaz, hiçbir işlem kapatılmaz.
    /// </summary>
    private async Task UpdateEdgeAsync(EdgeUpdateService.EdgeApp edge, UpdateItem target)
    {
        var before = EdgeUpdateService.ReadInstalledVersion(edge) ?? target.CurrentVersion;
        logger.Info($"{target.Name}: Microsoft Edge Update ile güncelleniyor ({before} → {target.NewVersion})...");
        var r = await _edgeInstall(edge, line => logger.Output("  Edge Update> " + line), EdgeInstallTimeout);
        var after = EdgeUpdateService.ReadInstalledVersion(edge);

        target.InUse = false;
        target.BlockingProcesses = [];
        target.ResultCode = r.Outcome is EdgeUpdateService.EdgeUpdateOutcome.Error or EdgeUpdateService.EdgeUpdateOutcome.Unavailable &&
                            r.ErrorCode != 0 ? r.ErrorCodeHex : null;
        target.InstallerExitCode = r.InstallerResultCode != 0 ? r.InstallerResultCode.ToString() : null;
        target.ResultSymbol = target.ResultCode is null ? null : "Microsoft Edge Update";
        target.ToolMessage = string.IsNullOrWhiteSpace(r.Message) ? null : "Microsoft Edge Update: " + r.Message.Trim();
        ExecutionTrace.Note($"{target.Name}: Microsoft Edge Update – {r.Outcome}, kayıtlı sürüm {before} → {after ?? "okunamadı"}" +
                            (r.AvailableVersion is null ? "" : $", sunulan {r.AvailableVersion}"));

        var expected = r.AvailableVersion ?? target.NewVersion;
        switch (r.Outcome)
        {
            case EdgeUpdateService.EdgeUpdateOutcome.Installed when EdgeUpdateService.IsAtLeast(after, expected):
            {
                var pending = EdgeUpdateService.IsRestartPending(edge);
                target.Outcome = ItemOutcome.Updated;
                target.OutcomeText = pending ? $"Güncellendi – {edge.Name} yeniden açılınca etkinleşir" : "Güncellendi (Microsoft Edge Update)";
                target.StatusText = $"Güncellendi: Microsoft Edge Update {before} → {after} kurdu (kayıt defterinden doğrulandı)" +
                                    (pending ? $"; {edge.Name} açık olduğu için yeni sürüm uygulama kapatılıp açılınca etkinleşir" : "");
                logger.Success($"{target.Name} güncellendi: {before} → {after} (Microsoft Edge Update, kayıt defterinden doğrulandı).");
                if (pending) logger.Info($"{target.Name}: yeni sürüm, uygulama kapatılıp yeniden açılınca etkinleşecek.");
                return;
            }
            case EdgeUpdateService.EdgeUpdateOutcome.Installed:
                target.Outcome = ItemOutcome.Unverified;
                target.OutcomeText = "Doğrulanamadı";
                target.StatusText = after is not null && !string.Equals(after, before, StringComparison.OrdinalIgnoreCase)
                    ? $"Microsoft Edge Update {before} → {after} kurdu; beklenen {expected} sürümü kayıt defterinde görünmüyor"
                    : $"Microsoft Edge Update kurulumun tamamlandığını bildirdi ancak kayıtlı sürüm hâlâ {after ?? "okunamadı"}";
                logger.Warning($"{target.Name}: {target.StatusText}.");
                return;
            case EdgeUpdateService.EdgeUpdateOutcome.NoUpdate:
                target.Outcome = ItemOutcome.Failed;
                target.OutcomeText = "Microsoft bu cihaza henüz sunmadı";
                target.StatusText = FailedPrefix + $"Microsoft Edge Update bu cihaz için yeni sürüm sunmuyor (winget {target.NewVersion} " +
                                    $"listeliyor; Microsoft güncellemeleri kademeli dağıtır). Kurulu sürüm {after ?? before}; " +
                                    "sürüm sunulduğunda Edge kendini günceller.";
                logger.Warning($"{target.Name}: Microsoft Edge Update bu cihaz için yeni sürüm sunmuyor; güncellenmedi.");
                return;
            default:
            {
                var reason = r.Describe();
                target.Outcome = ItemOutcome.Failed;
                target.OutcomeText = r.Outcome switch
                {
                    EdgeUpdateService.EdgeUpdateOutcome.TimedOut => "Zaman aşımı",
                    EdgeUpdateService.EdgeUpdateOutcome.Unavailable => "Microsoft Edge Update kullanılamadı",
                    _ => "Microsoft Edge Update hatası"
                };
                target.StatusText = FailedPrefix + reason + $" Kurulu sürüm: {after ?? before}." +
                                    (r.Outcome == EdgeUpdateService.EdgeUpdateOutcome.TimedOut
                                        ? " Güncelleme arka planda sürüyor olabilir; bir süre sonra yeniden kontrol edin."
                                        : " Edge'de Ayarlar → Microsoft Edge hakkında sayfasından da güncellenebilir.");
                logger.Error($"{target.Name} güncellenemedi: {reason}");
                return;
            }
        }
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var winget = LocateWinget();
        if (winget is null)
            return ModuleResult.Failed(key, "Winget bulunamadı.");

        // Güncelleme, kontrolde gerçekten bulunan paket listesiyle yapılır (tekrar liste sorgusu yapılmaz).
        var targets = check.Items.Where(i => i.UpdateAvailable && i.AutoUpdatable).ToList();
        if (targets.Count == 0)
            return check;

        var resultItems = check.Items.Select(Clone).ToList();
        var attempted = new List<UpdateItem>();
        var batch = new List<UpdateItem>();
        var skipped = new List<UpdateItem>();

        for (var n = 0; n < targets.Count; n++)
        {
            var item = targets[n];
            var target = resultItems.First(r => r.Id == item.Id);

            // İptal / çıkış istendi: başlamış paket yarıda kesilmez (kurulumlar iptal edilemez başlatılır); KALAN paketler başlatılmaz.
            if (ct.IsCancellationRequested)
            {
                skipped.Add(target);
                continue;
            }

            // Edge / WebView2: Microsoft Edge Update (doğrulaması kayıt defterinden; winget doğrulamasına girmez).
            if (IsEdgeManaged(item.Id, out var edge))
            {
                using var edgeProgress = new PackageProgress(this, n, targets.Count, item.Name);
                edgeProgress.SetPhase("Microsoft Edge Update ile güncelleniyor");
                await UpdateEdgeAsync(edge, target);
                continue;
            }

            // Çıktı genişliği nedeniyle kısaltılmış ("…") veya cmd.exe'ye güvenle verilemeyecek kimlikler tek tek hedeflenemez.
            if (!SafeId.IsMatch(item.Id))
            {
                batch.Add(target);
                continue;
            }

            logger.Info($"{item.Name} güncelleniyor ({item.CurrentVersion} → {item.NewVersion})...");
            using var progress = new PackageProgress(this, n, targets.Count, item.Name);
            var r = await RunWingetAsync(winget, UpgradeArgs(item.Id), PackageTimeout, CancellationToken.None, forward: true, onLine: progress.OnLine);
            Evaluate(r, target);
            attempted.Add(target);
        }

        if (batch.Count > 0 && ct.IsCancellationRequested)
        {
            skipped.AddRange(batch);
        }
        else if (batch.Count > 0)
        {
            // Aynı listeyi (winget'in kendi "otomatik güncellenebilir" kümesini) toplu komutla güncelle.
            logger.Info($"Kimliği tek tek hedeflenemeyen {batch.Count} paket için toplu winget güncellemesi çalıştırılıyor...");
            using var progress = new PackageProgress(this, targets.Count - batch.Count, targets.Count, $"{batch.Count} paket (toplu güncelleme)");
            var r = await RunWingetAsync(winget,
                Args("upgrade", "--all", "--source", source, "--silent", "--accept-package-agreements"),
                PackageTimeout * 2, CancellationToken.None, forward: true, onLine: progress.OnLine);
            foreach (var t in batch)
                Evaluate(r, t);
            attempted.AddRange(batch);
        }

        foreach (var t in skipped)
            t.StatusText = "Atlandı: işlem iptal edildi / uygulama kapatılıyor (kurulum başlatılmadı); bir sonraki güncellemede yeniden sunulur";
        if (skipped.Count > 0)
            logger.Warning($"{displayName}: iptal istendiği için {skipped.Count} paket başlatılmadı: {string.Join(", ", skipped.Select(s => s.Name))}.");

        // "Uygulama / dosyalar kullanımda" hatalarında güncellemeyi engelleyen GERÇEK işlemler tespit edilir (kapatılmaz).
        foreach (var t in attempted.Where(i => i.InUse))
            DetectBlockers(t);

        if (attempted.Count > 0)
            ReportProgress($"{attempted.Count} paket işlendi · sonuçlar doğrulanıyor (winget upgrade)", 100.0 * (targets.Count - skipped.Count) / targets.Count);
        var verifyNote = await VerifyAsync(winget, attempted, ct);
        return Summarize(resultItems, verifyNote, [], skipped);
    }

    /// <summary>
    /// "Uygulama çalışıyor / dosyalar kullanımda" nedeniyle güncellenemeyen paketler için: kullanıcının onayladığı
    /// engelleyen uygulamaları kapatır, gerçekten kapandıklarını denetler ve güncellemeyi YENİDEN dener.
    /// Onaylanmamış veya sonradan açılmış işlemlere dokunulmaz.
    /// </summary>
    public async Task<ModuleResult> RetryAfterClosingAsync(ModuleResult previous, IReadOnlyCollection<RunningProcessInfo> approved,
        CancellationToken ct)
    {
        var winget = LocateWinget();
        if (winget is null)
            return ModuleResult.Failed(key, "Winget bulunamadı.");

        var approvedKeys = approved.Select(p => (p.ProcessId, p.StartTime)).ToHashSet();
        var items = previous.Items.Select(Clone).ToList();
        // Başarısız veya "başarılı dendi ama doğrulanamadı" olup çalışan uygulama tespit edilen paketler.
        // (Kurulum teknolojisi farklı paketler bu yolla değil, manuel "kaldır + yeniden kur" ile güncellenir.)
        var targets = items
            .Where(i => i.InUse && i.Outcome is ItemOutcome.Failed or ItemOutcome.Unverified && SafeId.IsMatch(i.Id) &&
                        i.Manual != ManualUpdateKind.TechnologyMismatch &&
                        i.BlockingProcesses.Any(p => p.CanClose && approvedKeys.Contains((p.ProcessId, p.StartTime))))
            .ToList();
        if (targets.Count == 0)
        {
            logger.Info($"{displayName}: yeniden denenecek paket yok.");
            return previous;
        }

        var notes = new List<string>();
        var anyClosed = false;
        foreach (var t in targets)
        {
            var toClose = t.BlockingProcesses.Where(p => p.CanClose && approvedKeys.Contains((p.ProcessId, p.StartTime))).ToList();
            logger.Info($"{t.Name}: güncellemeyi engelleyen uygulamalar kapatılıyor: {string.Join(", ", toClose.Select(p => $"{p.Name} (PID {p.ProcessId})"))}");
            var report = await RunningAppManager.CloseAsync(toClose, logger);
            foreach (var line in report)
            {
                notes.Add($"{t.Name}: {line}");
                ExecutionTrace.Note($"{t.Name}: {line}");
            }
            anyClosed |= report.Any(l => l.Contains("kapatıldı", StringComparison.Ordinal) || l.Contains("sonlandırıldı", StringComparison.Ordinal));

            // Kapatmadan sonra dosyaları hâlâ kullanan işlem var mı? (gerçek Restart Manager denetimi)
            var dir = SafeFindInstallDirectory(t);
            if (dir is not null)
            {
                try
                {
                    var still = RunningAppManager.FindBlockingProcesses(dir);
                    if (still.Count > 0)
                    {
                        var text = $"{t.Name}: kapatmadan sonra dosyaları hâlâ kullanan işlemler: {string.Join(", ", still.Select(p => p.DisplayText))}";
                        logger.Warning(text);
                        notes.Add(text);
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                {
                    logger.Warning($"{t.Name}: kapatma sonrası denetim yapılamadı: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2)); // dosya tanıtıcılarının serbest kalması için
            logger.Info($"{t.Name} güncellemesi yeniden deneniyor ({t.CurrentVersion} → {t.NewVersion})...");
            var r = await RunWingetAsync(winget, UpgradeArgs(t.Id), PackageTimeout, CancellationToken.None, forward: true);
            Evaluate(r, t);
            if (t.InUse) DetectBlockers(t);
        }

        if (anyClosed)
            notes.Add("Kapatılan uygulamalar otomatik olarak yeniden açılmaz; gerekirse kendiniz açabilirsiniz.");

        var verifyNote = await VerifyAsync(winget, targets);
        return Summarize(items, verifyNote, notes);
    }

    private string[] UpgradeArgs(string id) =>
        Args("upgrade", "--id", id, "--exact", "--source", source, "--silent", "--accept-package-agreements");

    /// <summary>
    /// Kullanıcının AYRICA seçtiği, otomatik uygulanmayan güncellemeleri uygular:
    ///  - Açık hedefleme gerekli: yalnızca bu paketi hedefleyen "winget upgrade --id" (winget'in izin verdiği yol).
    ///  - Kurulum teknolojisi farklı (0x8A15008E): winget'in kendi önerisi – önce "winget uninstall", başarılıysa
    ///    "winget install" ile yeni sürüm. Kaldırma başarısızsa hiçbir değişiklik yapılmaz; kurulum başarısızsa paketin
    ///    kurulu olmadan kaldığı açıkça bildirilir. Sonuç her durumda gerçek winget sorgusuyla doğrulanır.
    /// </summary>
    public async Task<ModuleResult> UpdateManualAsync(ModuleResult check, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var winget = LocateWinget();
        if (winget is null)
            return ModuleResult.Failed(key, "Winget bulunamadı.");

        var items = check.Items.Select(Clone).ToList();
        var targets = items
            .Where(i => ids.Contains(i.Id) && i.UpdateAvailable && i.Manual != ManualUpdateKind.None && SafeId.IsMatch(i.Id))
            .ToList();
        if (targets.Count == 0)
        {
            return new ModuleResult
            {
                Key = key, Status = ComponentStatus.Skipped, Summary = "Manuel güncelleme seçilmedi",
                Reason = "Uygulanacak manuel güncelleme seçilmedi; hiçbir pakete dokunulmadı."
            };
        }

        var notes = new List<string>();
        var wingetTargets = new List<UpdateItem>();
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            // Edge / WebView2 kaldırılamaz: hiçbir koşulda "kaldır + kur" yoluna girmez (eski bir kontrol sonucundan gelse bile).
            if (IsEdgeManaged(t.Id, out var edge))
            {
                await UpdateEdgeAsync(edge, t);
                continue;
            }
            wingetTargets.Add(t);
            if (t.Manual is ManualUpdateKind.ExplicitTargeting or ManualUpdateKind.NoVersionChange)
            {
                logger.Info(t.Manual == ManualUpdateKind.NoVersionChange
                    ? $"{t.Name} yeniden deneniyor ({t.CurrentVersion} → {t.NewVersion}; önceki denemede kurulu sürüm değişmemişti)..."
                    : $"{t.Name} açık hedeflemeyle güncelleniyor ({t.CurrentVersion} → {t.NewVersion})...");
                var r = await RunWingetAsync(winget, UpgradeArgs(t.Id), PackageTimeout, CancellationToken.None, forward: true);
                Evaluate(r, t);
                if (t.InUse) DetectBlockers(t);
                continue;
            }

            // Kurulum teknolojisi farklı: kaldır → kur.
            logger.Info($"{t.Name}: kurulum teknolojisi farklı olduğu için mevcut sürüm kaldırılıyor ({t.CurrentVersion})...");
            var un = await RunWingetAsync(winget, Args("uninstall", "--id", t.Id, "--exact", "--source", source, "--silent"),
                PackageTimeout, CancellationToken.None, forward: true);
            if (!un.Succeeded)
            {
                Evaluate(un, t);
                t.Outcome = ItemOutcome.Failed; // kaldırma başarısızsa güncelleme hiçbir koşulda başarılı sayılmaz
                t.OutcomeText = "Mevcut sürüm kaldırılamadı – hiçbir değişiklik yapılmadı";
                t.StatusText = FailedPrefix + "Mevcut sürüm kaldırılamadı; yeni sürüm kurulmadı, paket olduğu gibi kaldı. " + DescribeFailure(un);
                logger.Error($"{t.Name}: mevcut sürüm kaldırılamadı; hiçbir değişiklik yapılmadı.");
                if (t.InUse) DetectBlockers(t);
                continue;
            }
            logger.Success($"{t.Name} {t.CurrentVersion} kaldırıldı.");
            notes.Add($"{t.Name}: eski sürüm ({t.CurrentVersion}) winget ile kaldırıldı.");

            logger.Info($"{t.Name} {t.NewVersion} kuruluyor...");
            var ins = await RunWingetAsync(winget,
                Args("install", "--id", t.Id, "--exact", "--source", source, "--silent", "--accept-package-agreements"),
                PackageTimeout, CancellationToken.None, forward: true);
            Evaluate(ins, t);
            if (t.Outcome == ItemOutcome.Failed)
            {
                var reason = FailureReason(t);
                t.OutcomeText = "Eski sürüm kaldırıldı, yeni sürüm kurulamadı";
                t.StatusText = FailedPrefix + $"Eski sürüm kaldırıldı ancak {t.NewVersion} kurulamadı – paket şu anda KURULU DEĞİL. " +
                               $"Yeniden kurmak için: winget install --id {t.Id} --exact. {reason}";
                logger.Error($"{t.Name}: eski sürüm kaldırıldı ancak yeni sürüm kurulamadı; paket şu anda kurulu değil.");
                if (t.InUse) DetectBlockers(t);
            }
            else
            {
                TechnologyMismatch.TryRemove(source + "|" + t.Id, out _);
                t.OutcomeText = "Kaldırılıp yeni sürüm kuruldu";
                t.StatusText = $"Kaldırılıp yeniden kuruldu ({t.CurrentVersion} → {t.NewVersion})";
            }
        }

        var verifyNote = await VerifyAsync(winget, wingetTargets);
        return Summarize(items, verifyNote, notes);
    }

    /// <summary>
    /// Başarı bildirilen paketler için TEK bir "winget upgrade" ile gerçek doğrulama: aynı yeni sürüm hâlâ listeleniyorsa
    /// paket "doğrulanamadı" olur. Doğrulama yapılamazsa nedeni döner.
    /// </summary>
    /// <param name="stopRetries">İptal / çıkış istenince arka plan kurulumları için yapılan bekleme-yeniden doğrulama kısaltılır
    /// (doğrulama yine bir kez daha yapılır; sonuç beklenmeden "başarılı" sayılmaz).</param>
    private async Task<string?> VerifyAsync(string winget, IEnumerable<UpdateItem> attempted, CancellationToken stopRetries = default)
    {
        var claimed = attempted.Where(i => i.Outcome is ItemOutcome.Updated or ItemOutcome.UpdatedReboot).ToList();
        if (claimed.Count == 0) return null;

        logger.Info($"{displayName}: güncelleme sonrası doğrulama yapılıyor (winget upgrade)...");
        var stillPending = new Dictionary<string, WingetRow>(StringComparer.OrdinalIgnoreCase);
        bool Pending(UpdateItem o) => stillPending.TryGetValue(o.Id, out var r) &&
                                      string.Equals(r.Available, o.NewVersion, StringComparison.OrdinalIgnoreCase);

        // Bazı kurulum programları (ör. Discord / Squirrel) "başarılı" deyip kurulumu ARKA PLANDA sürdürür (2026-09-30: Discord
        // kurulum programı 1 sn'de 0 döndürdü, sürüm ~2 dk içinde yükseldi; anında yapılan doğrulama "doğrulanamadı" deyip uygulamayı
        // gereksiz yere kapattırmıştı). Hâlâ görünen paketler için doğrulama VerifyRetryDelay arayla en fazla VerifyRetries kez tekrarlanır.
        for (var attempt = 0; ; attempt++)
        {
            var verify = await RunWingetAsync(winget, Args("upgrade", "--source", source), ListTimeout, CancellationToken.None);
            if (!verify.Started || verify.TimedOut || verify.Cancelled)
            {
                var note = "Güncelleme sonrası doğrulama yapılamadı: " + ProcessRunner.Describe(verify, "winget");
                logger.Warning(note);
                return note;
            }

            stillPending.Clear();
            foreach (var row in WingetTableParser.Parse(verify.StdOut).SelectMany(t => t.Rows).Where(r => !string.IsNullOrEmpty(r.Available)))
                stillPending.TryAdd(row.Id, row);

            var waiting = claimed.Where(Pending).ToList();
            if (waiting.Count == 0 || attempt >= VerifyRetries || stopRetries.IsCancellationRequested) break;
            logger.Info($"{string.Join(", ", waiting.Select(o => o.Name))}: kurulum programı arka planda sürüyor olabilir; doğrulama " +
                        $"{VerifyRetryDelay.TotalSeconds:0} sn sonra tekrarlanacak ({attempt + 1}/{VerifyRetries}).");
            try
            {
                await Task.Delay(VerifyRetryDelay, stopRetries);
            }
            catch (OperationCanceledException)
            {
                // çıkış istendi: son bir doğrulama yapılıp döngüden çıkılır
            }
        }

        foreach (var o in claimed)
        {
            if (stillPending.TryGetValue(o.Id, out var row) &&
                string.Equals(row.Available, o.NewVersion, StringComparison.OrdinalIgnoreCase))
            {
                o.Outcome = ItemOutcome.Unverified;
                o.OutcomeText = "Doğrulanamadı";
                o.StatusText = $"Winget başarı bildirdi ancak doğrulamada {row.Version} → {row.Available} güncellemesi hâlâ görünüyor";
                logger.Warning($"{o.Name}: winget başarı bildirdi ancak doğrulamada güncelleme hâlâ görünüyor.");

                // Bazı kurulum programları (ör. Discord/Squirrel) uygulama açıkken dosyaları değiştiremeyip yine de 0 döndürür.
                // Paketin dosyalarını kullanan çalışan işlem varsa gerçek olarak tespit edilir ve "kapat ve tekrar dene" sunulur.
                DetectBlockers(o);
                if (o.BlockingProcesses.Any(p => p.CanClose))
                {
                    o.InUse = true;
                    o.OutcomeText = "Doğrulanamadı – uygulama çalışıyor";
                    o.StatusText += "; uygulamanın dosyalarını kullanan çalışan işlemler var (" +
                                    string.Join(", ", o.BlockingProcesses.Select(p => p.DisplayText)) +
                                    "). Kurulum programı açık uygulamanın dosyalarını değiştirememiş olabilir – uygulamayı kapatıp tekrar deneyin";
                }
                else if (string.Equals(row.Version, o.CurrentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    // Açık uygulama yok ve kurulu sürüm hiç değişmedi: aynı sürüm çifti bir dahaki kontrolde otomatik denenmez.
                    NoVersionChange[source + "|" + o.Id] = NoChangeValue(row.Version, row.Available);
                    o.OutcomeText = "Doğrulanamadı – kurulu sürüm değişmedi";
                    o.StatusText += $"; kurulu sürüm hiç değişmedi ({row.Version}). Winget kataloğundaki sürüm numarası uygulamanın kendi " +
                                    "sürümünden farklı olabilir; bu sürüm bir dahaki kontrolde otomatik denenmez (manuel olarak yeniden denenebilir)";
                    logger.Info($"{o.Name}: kurulu sürüm değişmedi ({row.Version}); {row.Available} bir dahaki kontrolde otomatik denenmeyecek.");
                }
            }
        }
        return null;
    }

    /// <summary>Öğelerin gerçek sonuçlarından (Outcome) modül sonucunu üretir.</summary>
    /// <param name="skipped">İptal / çıkış istendiği için HİÇ başlatılmayan paketler (başarı sayılmaz; sonuç "kısmen" veya "atlandı").</param>
    private ModuleResult Summarize(List<UpdateItem> items, string? verifyNote, IReadOnlyList<string> extraNotes,
        IReadOnlyCollection<UpdateItem>? skipped = null)
    {
        skipped ??= [];
        var attempted = items.Where(i => i.Outcome is not null).ToList();
        var ok = attempted.Count(i => i.Outcome is ItemOutcome.Updated or ItemOutcome.UpdatedReboot);
        var reboot = attempted.Count(i => i.Outcome == ItemOutcome.UpdatedReboot);
        var failed = attempted.Where(i => i.Outcome == ItemOutcome.Failed).ToList();
        var unverified = attempted.Where(i => i.Outcome == ItemOutcome.Unverified).ToList();

        ComponentStatus status;
        string summary;
        if (skipped.Count > 0 && ok == 0 && failed.Count == 0 && unverified.Count == 0)
        {
            status = ComponentStatus.Skipped;
            summary = $"{skipped.Count} paket atlandı (iptal edildi)";
        }
        else if (skipped.Count > 0 && failed.Count == 0 && unverified.Count == 0)
        {
            status = ComponentStatus.PartiallyUpdated;
            summary = $"{ok} güncellendi, {skipped.Count} atlandı (iptal edildi)" + (reboot > 0 ? " – yeniden başlatma gerekli" : "");
        }
        else if (failed.Count == 0 && unverified.Count == 0)
        {
            status = reboot > 0 ? ComponentStatus.RebootRequired : ComponentStatus.Updated;
            summary = reboot > 0 ? $"{ok} paket güncellendi – yeniden başlatma gerekli" : $"{ok} paket güncellendi";
        }
        else if (ok > 0 || unverified.Count > 0)
        {
            status = ComponentStatus.PartiallyUpdated;
            var parts = new List<string> { $"{ok} güncellendi" };
            if (failed.Count > 0) parts.Add($"{failed.Count} güncellenemedi");
            if (unverified.Count > 0) parts.Add($"{unverified.Count} doğrulanamadı");
            if (skipped.Count > 0) parts.Add($"{skipped.Count} atlandı (iptal edildi)");
            summary = string.Join(", ", parts);
        }
        else
        {
            status = ComponentStatus.Failed;
            summary = failed.Count == 1 ? "1 paket güncellenemedi" : $"{failed.Count} paket güncellenemedi";
        }

        if (failed.Count == 0 && unverified.Count == 0 && skipped.Count == 0) logger.Success($"{displayName}: {summary}.");
        else logger.Warning($"{displayName}: {summary}.");

        var reasons = new List<string>();
        reasons.AddRange(failed.Select(i => $"{i.Name} ({i.CurrentVersion} → {i.NewVersion}): {FailureReason(i)}"));
        reasons.AddRange(failed.Where(i => i.InUse && i.BlockingProcesses.Count > 0)
            .Select(i => $"{i.Name}: güncellemeyi engelleyen çalışan uygulamalar – {string.Join(", ", i.BlockingProcesses.Select(p => p.DisplayText))}"));
        reasons.AddRange(unverified.Select(i => $"{i.Name}: {i.StatusText}."));
        if (verifyNote is not null) reasons.Add(verifyNote);
        if (reboot > 0) reasons.Add("Bazı paketlerin tamamlanması için yeniden başlatma gerekiyor.");
        if (skipped.Count > 0)
            reasons.Add($"İşlem iptal edildiği / uygulama kapatıldığı için başlatılmayan paketler: {string.Join(", ", skipped.Select(s => s.Name))}. " +
                        "Bir sonraki kontrolde yeniden sunulur.");
        reasons.AddRange(extraNotes);

        var details = $"Güncellenen: {ok}\nGüncellenemeyen: {failed.Count}";
        if (unverified.Count > 0) details += $"\nDoğrulanamayan: {unverified.Count}";
        if (skipped.Count > 0) details += $"\nAtlanan (iptal edildi): {skipped.Count}";
        if (reboot > 0) details += $"\nYeniden başlatma bekleyen: {reboot}";
        var blocked = failed.Count(i => i.InUse && i.BlockingProcesses.Any(p => p.CanClose));
        if (blocked > 0) details += $"\nÇalışan uygulama nedeniyle güncellenemeyen: {blocked}";

        return new ModuleResult
        {
            Key = key,
            Status = status,
            Summary = summary,
            Details = details,
            Reason = reasons.Count > 0 ? string.Join("\n", reasons) : null,
            Items = items,
            RebootRequired = reboot > 0
        };
    }

    private const string FailedPrefix = "Güncellenemedi: ";

    private static string FailureReason(UpdateItem i) =>
        i.StatusText.StartsWith(FailedPrefix, StringComparison.Ordinal) ? i.StatusText[FailedPrefix.Length..] : i.StatusText;

    /// <summary>
    /// Tek bir paket güncellemesinin gerçek sonucunu (winget çıkış kodu + çıktısı) yorumlar ve öğeye paket bazlı
    /// sonuç alanlarını (sonuç, kod, sembol, kurulum programı çıkış kodu, winget mesajı) yazar.
    /// </summary>
    private void Evaluate(ProcessResult r, UpdateItem target)
    {
        var code = unchecked((uint)r.ExitCode);
        var ran = r.Started && !r.TimedOut && !r.Cancelled;
        var installer = InstallerExitCodeRegex.Match(r.StdOut + "\n" + r.StdErr);
        Codes.TryGetValue(code, out var known);

        target.ResultCode = ran ? r.ExitCodeHex : null;
        target.ResultSymbol = ran && r.ExitCode != 0 ? known?.Symbol : null;
        target.InstallerExitCode = installer.Success ? installer.Groups[1].Value : null;
        target.ToolMessage = ToolMessage(r, target.Id);
        target.InUse = false;
        target.BlockingProcesses = [];

        if (r.Succeeded)
        {
            target.Outcome = ItemOutcome.Updated;
            target.OutcomeText = "Güncellendi";
            target.StatusText = "Güncellendi (winget çıkış kodu 0)";
            logger.Success($"{target.Name} güncellendi.");
            return;
        }

        if (ran && code is RebootRequiredToFinish or RebootInitiated)
        {
            target.Outcome = ItemOutcome.UpdatedReboot;
            target.OutcomeText = "Güncellendi – yeniden başlatma gerekli";
            target.StatusText = "Güncellendi – yeniden başlatma gerekli";
            logger.Warning($"{target.Name}: {Codes[code].Text}");
            return;
        }

        var reason = DescribeFailure(r);
        if (ran && code == InstallTechnologyMismatch && !string.IsNullOrEmpty(target.NewVersion))
            TechnologyMismatch[source + "|" + target.Id] = target.NewVersion;

        target.Outcome = ItemOutcome.Failed;
        target.OutcomeText = !r.Started ? "Winget başlatılamadı"
            : r.TimedOut ? "Zaman aşımı"
            : r.Cancelled ? "İptal edildi"
            : known?.Short ?? "Winget işlemi başarısız oldu";
        target.InUse = ran && code is InstallPackageInUse or InstallFileInUse or InstallPackageInUseByApplication;
        target.StatusText = FailedPrefix + reason;
        logger.Error($"{target.Name} güncellenemedi: {reason}");
    }

    /// <summary>Winget'in bu paket için verdiği son anlamlı mesaj satırları (başlık/lisans satırları hariç).</summary>
    private static string? ToolMessage(ProcessResult r, string id)
    {
        var lines = (r.StdOut + "\n" + r.StdErr).Replace("\r", "\n").Split('\n')
            .Select(WingetTableParser.CleanLine)
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !l!.Contains("[" + id + "]", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("http", StringComparison.OrdinalIgnoreCase))
            .Select(l => l!.Trim())
            .Distinct()
            .ToList();
        return lines.Count == 0 ? null : string.Join(" ", lines.TakeLast(2));
    }

    /// <summary>Güncellemeyi engelleyen çalışan işlemleri Restart Manager ile tespit eder ve öğeye yazar (hiçbirini kapatmaz).</summary>
    private void DetectBlockers(UpdateItem item)
    {
        var dir = SafeFindInstallDirectory(item);
        if (dir is null)
        {
            logger.Warning($"{item.Name}: kurulum klasörü Programlar ve Özellikler kaydında güvenli şekilde bulunamadı; " +
                           "güncellemeyi engelleyen uygulama tespit edilemedi.");
            return;
        }
        try
        {
            var found = RunningAppManager.FindBlockingProcesses(dir);
            item.BlockingProcesses = found.ToList();
            if (found.Count == 0)
            {
                logger.Info($"{item.Name}: '{dir}' klasöründeki dosyaları kullanan çalışan işlem bulunamadı.");
                ExecutionTrace.Note($"{item.Name}: Restart Manager – '{dir}' dosyalarını kullanan işlem yok.");
            }
            else
            {
                var text = string.Join(", ", found.Select(p => p.DisplayText));
                logger.Warning($"{item.Name}: güncellemeyi engelleyen çalışan işlemler: {text}");
                ExecutionTrace.Note($"{item.Name}: Restart Manager – '{dir}' dosyalarını kullanan işlemler: {text}");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.Warning($"{item.Name}: çalışan uygulamalar tespit edilemedi: {ex.Message}");
        }
    }

    private string? SafeFindInstallDirectory(UpdateItem item)
    {
        try
        {
            return RunningAppManager.FindInstallDirectory(item.Name, item.CurrentVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.Warning($"{item.Name}: kurulum kaydı okunamadı: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Başarısız bir winget çağrısını açıklar: resmi koda karşılık gelen Türkçe açıklama, kurulum programının
    /// gerçek çıkış kodu (winget çıktısında varsa) ve winget'in kendi son mesajı birlikte verilir.
    /// </summary>
    /// <summary>Başarısız winget çağrısının gerçek açıklaması (resmi kod sözlüğü + winget mesajı); başka servisler de kullanır.</summary>
    internal static string DescribeFailure(ProcessResult r)
    {
        if (!r.Started || r.TimedOut || r.Cancelled)
            return ProcessRunner.Describe(r, "winget");

        var code = unchecked((uint)r.ExitCode);
        var wingetMessage = ProcessRunner.LastMeaningfulLine(r.StdErr) ?? ProcessRunner.LastMeaningfulLine(r.StdOut);
        var installer = InstallerExitCodeRegex.Match(r.StdOut + "\n" + r.StdErr);

        var parts = new List<string>();
        parts.Add(Codes.TryGetValue(code, out var known) ? known.Text : "Winget işlemi başarısız oldu.");
        if (installer.Success) parts.Add($"Kurulum programı çıkış kodu: {installer.Groups[1].Value}.");
        var codeText = known is null ? r.ExitCodeHex : $"{r.ExitCodeHex} ({known.Symbol})";
        parts.Add(wingetMessage is null ? $"Winget: {codeText}." : $"Winget: {codeText} – \"{wingetMessage}\".");
        return string.Join(" ", parts);
    }

    private void ForwardOutput(string line)
    {
        var clean = WingetTableParser.CleanLine(line);
        if (clean is not null)
            logger.Output("  winget> " + clean);
    }

    private static UpdateItem Clone(UpdateItem i) => new()
    {
        Name = i.Name,
        Id = i.Id,
        CurrentVersion = i.CurrentVersion,
        NewVersion = i.NewVersion,
        UpdateAvailable = i.UpdateAvailable,
        AutoUpdatable = i.AutoUpdatable,
        Manual = i.Manual,
        StatusText = i.StatusText,
        Tag = i.Tag,
        Selected = i.Selected,
        Outcome = i.Outcome,
        OutcomeText = i.OutcomeText,
        ResultCode = i.ResultCode,
        ResultSymbol = i.ResultSymbol,
        InstallerExitCode = i.InstallerExitCode,
        ToolMessage = i.ToolMessage,
        InUse = i.InUse,
        BlockingProcesses = [.. i.BlockingProcesses]
    };
}

public sealed record WingetRow(string Name, string Id, string Version, string Available);

public sealed class WingetTable
{
    public bool RequiresExplicitTargeting { get; init; }
    public List<WingetRow> Rows { get; } = [];
}

/// <summary>
/// winget'in metin tablosu çıktısını ayrıştırır. Başlık satırı, altındaki "-----" çizgisinden
/// tanınır; sütun başlangıçları başlıktaki kelime konumlarından alınır (dil bağımsız).
/// </summary>
public static class WingetTableParser
{
    public static List<WingetTable> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l =>
            {
                var idx = l.LastIndexOf('\r');
                return idx >= 0 ? l[(idx + 1)..] : l;
            })
            .ToList();

        var tables = new List<WingetTable>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (!IsDashLine(lines[i])) continue;

            var header = lines[i - 1];
            var starts = ColumnStarts(header);
            if (starts.Count < 3) continue;

            // Açıklama cümlesi (':' ile biten) tablonun hemen üstündeyse bu "açık hedefleme" tablosudur.
            // Normal güncelleme tablosunun üstünde metin bulunmaz.
            var prev = lines.Take(i - 1).LastOrDefault(l => CleanLine(l) is not null);
            var table = new WingetTable
            {
                RequiresExplicitTargeting = prev is not null && prev.TrimEnd().EndsWith(':')
            };

            for (var j = i + 1; j < lines.Count; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line) || line.Length <= starts[2]) break;

                var name = Field(line, starts, 0);
                var id = Field(line, starts, 1);
                var version = Field(line, starts, 2);
                var available = starts.Count >= 4 ? Field(line, starts, 3) : string.Empty;

                if (id.Length == 0 || id.Contains(' ') || version.Length == 0) break;
                if (starts[1] > 0 && line.Length > starts[1] && line[starts[1] - 1] != ' ') break; // hizalama bozuk

                table.Rows.Add(new WingetRow(name, id, version, available));
            }
            tables.Add(table);
        }
        return tables;
    }

    private static bool IsDashLine(string line)
    {
        var t = line.Trim();
        return t.Length >= 10 && t.All(c => c == '-');
    }

    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (var p = 0; p < header.Length; p++)
        {
            if (header[p] != ' ' && (p == 0 || header[p - 1] == ' '))
                starts.Add(p);
        }
        return starts;
    }

    private static string Field(string line, List<int> starts, int index)
    {
        var start = starts[index];
        if (start >= line.Length) return string.Empty;
        var end = index + 1 < starts.Count ? Math.Min(starts[index + 1], line.Length) : line.Length;
        return line[start..end].Trim();
    }

    /// <summary>İlerleme çubuğu / dönen imleç satırlarını ayıklar; anlamlı satırı döndürür.</summary>
    public static string? CleanLine(string line)
    {
        var idx = line.LastIndexOf('\r');
        var s = (idx >= 0 ? line[(idx + 1)..] : line).Trim();
        if (s.Length == 0) return null;
        if (s.Contains('█') || s.Contains('▒')) return null;
        if (s.All(c => c is '-' or '\\' or '|' or '/' or ' ')) return null;
        return s;
    }
}

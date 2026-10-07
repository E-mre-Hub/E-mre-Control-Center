using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Kurulumun Windows'taki yerleri. Gerçek kurulum <see cref="Machine"/> kullanır; testler geçici klasörler ve HKCU altında bir test
/// anahtarı verir (gerçek Program Files / HKLM'e dokunmadan aynı kod sınanır).
/// </summary>
public sealed record InstallLayout(
    string InstallDir,
    string StartMenuShortcut,
    string DesktopShortcut,
    RegistryHive Hive,
    string UninstallKeyPath,
    string StagingRoot,
    IReadOnlyList<string> UserDataDirs,
    IReadOnlyList<string> CacheDirs,
    string NotificationKeyPath,
    bool ProtectStaging = false)
{
    public const string ExeName = AppInfo.Name + ".exe";

    public string ExePath => Path.Combine(InstallDir, ExeName);

    /// <summary>
    /// Tüm kullanıcılar için: C:\Program Files\E-mre Control Center (yalnızca yöneticinin yazabildiği klasör; uygulama yönetici olarak
    /// çalıştığı için EXE'nin standart kullanıcı yetkisiyle değiştirilememesi gerekir), ortak Başlat menüsü / masaüstü, HKLM Uninstall kaydı.
    /// </summary>
    public static InstallLayout Machine { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppInfo.Name + ".lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppInfo.Name + ".lnk"),
        RegistryHive.LocalMachine,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Name,
        // Kaldırıcının geçici kopyası: C:\ProgramData altında yalnızca Yöneticiler + SYSTEM erişimli klasör. Standart kullanıcı içine
        // yazamaz, klasörü silemez / yeniden adlandıramaz: yönetici işlemi oradan çalışırken dosyası değiştirilemez.
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        [AppInfo.DataDirectory, .. AppInfo.LegacyDataDirectories],
        // .NET tek dosya EXE'lerinin yerel kitaplıklarını çıkardığı önbellek (%TEMP%\.net\<uygulama adı>; yalnızca bu uygulamanın adları)
        // ve yönetici olmadan indirilen güncellemeler (%TEMP%\E-mre Control Center Güncelleme).
        [.. new[] { AppInfo.Name }.Concat(AppInfo.LegacyNames).Select(n => Path.Combine(Path.GetTempPath(), ".net", n)),
            Path.Combine(Path.GetTempPath(), UpdateService.DownloadFolderName)],
        $@"Software\Classes\AppUserModelId\{NotificationService.AppId}",
        ProtectStaging: true);
}

/// <summary>Kurulu sürüm bilgisi (Uninstall kaydı + dosya).</summary>
public sealed record InstalledInfo(string? Version, bool Registered, bool ExeExists, bool HasDesktopShortcut);

/// <summary>Adım anahtarları (arayüz aynı anahtarlarla adım listesini gösterir).</summary>
public static class InstallSteps
{
    public const string Running = "running";
    public const string Copy = "copy";
    public const string Verify = "verify";
    public const string StartMenu = "startmenu";
    public const string Desktop = "desktop";
    public const string Registry = "registry";

    public const string Feedback = "feedback";
    public const string Shortcuts = "shortcuts";
    public const string Files = "files";
    public const string Unregister = "unregister";
    public const string Cache = "cache";
    public const string UserData = "data";

    public static IReadOnlyDictionary<string, string> Titles { get; } = new Dictionary<string, string>
    {
        [Running] = L.T("Çalışan uygulama denetleniyor", "Checking for the running app"),
        [Copy] = L.T("Program dosyası kopyalanıyor", "Copying the program file"),
        [Verify] = L.T("Dosya doğrulanıyor (SHA-256)", "Verifying the file (SHA-256)"),
        [StartMenu] = L.T("Başlat menüsü kısayolu", "Start menu shortcut"),
        [Desktop] = L.T("Masaüstü kısayolu", "Desktop shortcut"),
        [Registry] = L.T("Windows Uygulamalar kaydı", "Windows Apps entry"),
        [Feedback] = L.T("Geri bildirim gönderiliyor", "Sending feedback"),
        [Shortcuts] = L.T("Kısayollar kaldırılıyor", "Removing shortcuts"),
        [Files] = L.T("Program dosyaları siliniyor", "Deleting program files"),
        [Unregister] = L.T("Windows Uygulamalar kaydı siliniyor", "Deleting the Windows Apps entry"),
        [Cache] = L.T("Uygulamanın geçici dosyaları siliniyor", "Deleting the app's temporary files"),
        [UserData] = L.T("Ayarlar, geçmiş ve günlükler siliniyor", "Deleting settings, history and logs")
    };
}

public sealed record InstallStepResult(string Key, bool Success, string Detail)
{
    public string Title => InstallSteps.Titles.GetValueOrDefault(Key, Key);
}

/// <summary>Adım başladı / ilerledi bildirimi. Fraction yalnızca gerçek bayt ilerlemesi olan adımda (kopyalama) doludur.</summary>
public sealed record InstallProgress(string Step, double? Fraction = null, string? Detail = null);

public sealed class InstallReport
{
    public List<InstallStepResult> Steps { get; } = [];

    /// <summary>Kullanımda olduğu için Windows'un bir sonraki yeniden başlatmada sileceği öğeler.</summary>
    public List<string> PendingReboot { get; } = [];

    /// <summary>Bilerek bırakılan öğeler (bu uygulamaya ait olmayan dosyalar / kısayollar).</summary>
    public List<string> Kept { get; } = [];

    /// <summary>Silinemeyen ve yeniden başlatmaya da bırakılamayan öğeler (neden ile).</summary>
    public List<string> Failed { get; } = [];

    /// <summary>Başarısız kurulumda geri alınanların özeti.</summary>
    public string? Rollback { get; set; }

    public bool Success => Steps.Count > 0 && Steps.All(s => s.Success) && Failed.Count == 0;

    public InstallStepResult? FirstFailure => Steps.FirstOrDefault(s => !s.Success);
}

/// <summary>
/// Kurulum ve kaldırma. Her adımın sonucu gerçektir: kopyalanan dosya SHA-256 ile kaynağa karşı doğrulanır, kısayollar geri okunur,
/// Uninstall kaydı yeniden açılıp okunur; başarısız kurulum önceki duruma geri alınır. Kaldırma yalnızca bu uygulamanın bilinen
/// yollarına dokunur (hedefi başka bir dosya olan kısayol veya program klasörüne sonradan konmuş dosya bırakılır ve raporlanır);
/// kullanıcının yazabildiği klasörlerde bağlantı (junction / symlink) izlenmez, yalnızca bağlantının kendisi silinir.
/// </summary>
public sealed class InstallerService(InstallLayout layout, Action<string> log)
{
    public InstallLayout Layout => layout;

    // ------------------------------------------------------------------ durum

    public InstalledInfo? ReadInstalled()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(layout.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(layout.UninstallKeyPath);
            var exeExists = File.Exists(layout.ExePath);
            if (key is null && !exeExists) return null;
            return new InstalledInfo(key?.GetValue("DisplayVersion") as string, key is not null, exeExists, IsOurShortcut(layout.DesktopShortcut));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log(L.T($"Kurulum bilgisi okunamadı: {ex.Message}", $"Could not read the installation information: {ex.Message}"));
            return null;
        }
    }

    /// <summary>Kurulu EXE'den çalışan işlemler (bu işlem hariç). Yol, sınırlı sorgu hakkıyla okunur (yükseltilmiş işlemler dahil).</summary>
    public IReadOnlyList<Process> FindRunning()
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallLayout.ExeName)))
        {
            var path = p.Id == Environment.ProcessId ? null : RunningAppManager.QueryImagePath(p.Id);
            if (path is not null && SamePath(path, layout.ExePath)) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Çalışan uygulamadan normal kapanmasını ister (pencereyi kapat) ve bekler. Zorla sonlandırmaz: uygulamada bir işlem sürüyorsa
    /// kullanıcıya kendi onay sorusunu gösterir; kapanmazsa false döner ve kullanıcı uygulamayı kendisi kapatıp yeniden dener.
    /// </summary>
    public async Task<bool> CloseRunningAsync(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        foreach (var p in processes)
        {
            try
            {
                if (p.HasExited) continue;
                // v1.8.0+: pencere bildirim alanına gizlenmiş olabilir (kapatma düğmesi uygulamayı kapatmaz); "kapan" iletisi
                // uygulamanın bildirim alanı penceresine gider. Eski sürümlerde bu pencere yoktur → ana pencere kapatılır.
                if (AppSignals.Post(AppSignals.ExitMessage, p.Id))
                {
                    log(L.T($"Kapatma isteği: PID {p.Id} (bildirim alanı penceresine iletildi)", $"Close request: PID {p.Id} (sent to the notification area window)"));
                    continue;
                }
                var sent = p.CloseMainWindow();
                log(L.T($"Kapatma isteği: PID {p.Id} ({(sent ? "pencereye iletildi" : "penceresi yok")})", $"Close request: PID {p.Id} ({(sent ? "sent to the window" : "no window")})"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log(L.T($"Kapatma isteği gönderilemedi (PID {p.Id}): {ex.Message}", $"Could not send the close request (PID {p.Id}): {ex.Message}"));
            }
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (processes.All(HasExited)) break;
            await Task.Delay(250);
        }
        var closed = processes.All(HasExited);
        log(closed ? L.T("Çalışan uygulama kapandı.", "The running app closed.") : L.T("Çalışan uygulama süre içinde kapanmadı.", "The running app did not close in time."));
        return closed;

        static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
        }
    }

    // ------------------------------------------------------------------ kurulum

    public Task<InstallReport> InstallAsync(string sourceExe, bool desktopShortcut, IProgress<InstallProgress>? progress = null) =>
        Task.Run(() => Install(sourceExe, desktopShortcut, progress));

    private InstallReport Install(string sourceExe, bool desktopShortcut, IProgress<InstallProgress>? progress)
    {
        var report = new InstallReport();
        var exe = layout.ExePath;
        var fresh = exe + ".new";
        var previous = exe + ".old";
        var dirCreated = false;
        var hadPrevious = false;
        var placed = false;
        var startMenuExisted = File.Exists(layout.StartMenuShortcut);
        var desktopExisted = File.Exists(layout.DesktopShortcut);
        var registryBefore = SnapshotUninstallKey();
        var registryTouched = false;
        var step = InstallSteps.Running;

        log(L.T($"Kurulum başladı: {sourceExe} → {exe} (sürüm {AppInfo.Version}; masaüstü kısayolu: {(desktopShortcut ? "evet" : "hayır")}; ", $"Setup started: {sourceExe} → {exe} (version {AppInfo.Version}; desktop shortcut: {(desktopShortcut ? "yes" : "no")}; ") +
            L.T($"önceki kayıt: {(registryBefore is null ? "yok" : "var")})", $"previous entry: {(registryBefore is null ? "none" : "present")})"));
        try
        {
            progress?.Report(new(step));
            var running = FindRunning();
            if (running.Count > 0)
            {
                var pids = string.Join(", ", running.Select(p => p.Id));
                foreach (var p in running) p.Dispose();
                report.Steps.Add(new(step, false, L.T($"Uygulama hâlâ açık (PID {pids}). Kapatıp yeniden deneyin.", $"The app is still open (PID {pids}). Close it and try again.")));
                log(L.T($"Kurulum durdu: uygulama açık (PID {pids}).", $"Setup stopped: the app is open (PID {pids})."));
                return report;
            }
            report.Steps.Add(new(step, true, L.T("Kurulu uygulama açık değil", "The installed app is not open")));

            step = InstallSteps.Copy;
            progress?.Report(new(step, 0));
            if (SamePath(sourceExe, exe))
                throw new InvalidOperationException(L.T("Kurulum programı kurulu dosyanın kendisinden çalıştırılıyor; kurulum dosyasını (Setup) kullanın.", "The setup is running from the installed file itself; use the setup file (Setup)."));
            if (!Directory.Exists(layout.InstallDir))
            {
                Directory.CreateDirectory(layout.InstallDir);
                dirCreated = true;
            }
            var sourceHash = CopyWithHash(sourceExe, fresh, (done, total) =>
                progress?.Report(new(InstallSteps.Copy, total > 0 ? (double)done / total : null, $"{Mb(done)} / {Mb(total)}")));
            var size = new FileInfo(fresh).Length;
            report.Steps.Add(new(step, true, $"{Mb(size)} → {layout.InstallDir}"));

            step = InstallSteps.Verify;
            progress?.Report(new(step));
            var writtenHash = HashFile(fresh);
            if (!string.Equals(sourceHash, writtenHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.T($"Kopyalanan dosya kaynakla aynı değil (SHA-256 {sourceHash[..12]}… ≠ {writtenHash[..12]}…).", $"The copied file is not identical to the source (SHA-256 {sourceHash[..12]}… ≠ {writtenHash[..12]}…)."));
            if (File.Exists(exe))
            {
                File.Move(exe, previous, true);
                hadPrevious = true;
            }
            File.Move(fresh, exe);
            placed = true;
            report.Steps.Add(new(step, true, L.T($"SHA-256 eşleşti ({sourceHash[..16]}…)", $"SHA-256 matched ({sourceHash[..16]}…)")));

            step = InstallSteps.StartMenu;
            progress?.Report(new(step));
            CreateVerifiedShortcut(layout.StartMenuShortcut, exe);
            report.Steps.Add(new(step, true, layout.StartMenuShortcut));

            step = InstallSteps.Desktop;
            progress?.Report(new(step));
            if (desktopShortcut)
            {
                CreateVerifiedShortcut(layout.DesktopShortcut, exe);
                report.Steps.Add(new(step, true, layout.DesktopShortcut));
            }
            else if (IsOurShortcut(layout.DesktopShortcut))
            {
                File.Delete(layout.DesktopShortcut);
                report.Steps.Add(new(step, true, L.T("İstenmedi; önceki masaüstü kısayolu kaldırıldı", "Not requested; the previous desktop shortcut was removed")));
            }
            else
            {
                report.Steps.Add(new(step, true, L.T("İstenmedi", "Not requested")));
            }

            step = InstallSteps.Registry;
            progress?.Report(new(step));
            registryTouched = true;
            WriteUninstallKey(exe, size);
            report.Steps.Add(new(step, true, L.T("Ayarlar → Uygulamalar listesinde görünür", "Appears in the Settings → Apps list")));

            if (hadPrevious) DeleteOrSchedule(previous, report);
            log(L.T($"Kurulum tamamlandı: {exe} ({Mb(size)}, SHA-256 {sourceHash}).", $"Setup completed: {exe} ({Mb(size)}, SHA-256 {sourceHash})."));
            return report;
        }
        catch (Exception ex)
        {
            report.Steps.Add(new(step, false, ex.Message));
            log(L.T($"Kurulum başarısız ({step}): {ex.GetType().Name}: {ex.Message}", $"Setup failed ({step}): {ex.GetType().Name}: {ex.Message}"));
            report.Rollback = RollbackInstall(fresh, previous, hadPrevious, placed, dirCreated, startMenuExisted, desktopExisted,
                registryTouched, registryBefore);
            log(L.T("Geri alma: ", "Rollback: ") + report.Rollback);
            return report;
        }
    }

    private string RollbackInstall(string fresh, string previous, bool hadPrevious, bool placed, bool dirCreated, bool startMenuExisted,
        bool desktopExisted, bool registryTouched, Dictionary<string, (object Value, RegistryValueKind Kind)>? registryBefore)
    {
        var done = new List<string>();
        var failed = new List<string>();
        void Try(string what, Action action)
        {
            try { action(); done.Add(what); }
            catch (Exception ex) { failed.Add($"{what}: {ex.Message}"); }
        }

        var exe = layout.ExePath;
        if (File.Exists(fresh)) Try(L.T("yarım kopya silindi", "partial copy deleted"), () => File.Delete(fresh));
        // Yalnızca bu kurulumun yerleştirdiği dosyaya dokunulur; önceki sürüm (.old) geri taşınır.
        if (hadPrevious && File.Exists(previous)) Try(L.T("önceki sürüm geri yüklendi", "previous version restored"), () => File.Move(previous, exe, true));
        else if (placed && File.Exists(exe)) Try(L.T("kopyalanan dosya silindi", "copied file deleted"), () => File.Delete(exe));
        if (!startMenuExisted && File.Exists(layout.StartMenuShortcut)) Try(L.T("Başlat menüsü kısayolu kaldırıldı", "Start menu shortcut removed"), () => File.Delete(layout.StartMenuShortcut));
        if (!desktopExisted && File.Exists(layout.DesktopShortcut)) Try(L.T("masaüstü kısayolu kaldırıldı", "desktop shortcut removed"), () => File.Delete(layout.DesktopShortcut));
        if (registryTouched) Try(L.T("Windows kaydı eski hâline getirildi", "Windows entry restored"), () => RestoreUninstallKey(registryBefore));
        if (dirCreated && Directory.Exists(layout.InstallDir) && !Directory.EnumerateFileSystemEntries(layout.InstallDir).Any())
            Try(L.T("boş program klasörü silindi", "empty program folder deleted"), () => Directory.Delete(layout.InstallDir));

        var text = done.Count == 0 ? L.T("Geri alınacak değişiklik yoktu.", "There were no changes to roll back.") : L.T("Geri alındı: ", "Rolled back: ") + string.Join(", ", done) + ".";
        if (failed.Count > 0) text += L.T(" Geri alınamayan: ", " Could not roll back: ") + string.Join("; ", failed) + ".";
        return text;
    }

    // ------------------------------------------------------------------ kaldırma

    /// <param name="currentExe">
    /// Çalışan kaldırıcının yolu. Normalde <see cref="PrepareStagedCopy"/> ile hazırlanan geçici kopyadır: kurulu EXE serbest kalır ve
    /// hemen silinir, kopyayı Windows yeniden başlatmada siler. Kurulu EXE'nin kendisiyse EXE yeniden başlatmada silinir.
    /// </param>
    public Task<InstallReport> UninstallAsync(bool deleteUserData, string? currentExe, IProgress<InstallProgress>? progress = null) =>
        Task.Run(() => Uninstall(deleteUserData, currentExe, progress));

    private InstallReport Uninstall(bool deleteUserData, string? currentExe, IProgress<InstallProgress>? progress)
    {
        var report = new InstallReport();
        log(L.T($"Kaldırma başladı: {layout.InstallDir} (kullanıcı verileri {(deleteUserData ? "silinecek" : "korunacak")}).", $"Uninstall started: {layout.InstallDir} (user data will be {(deleteUserData ? "deleted" : "kept")})."));

        Run(InstallSteps.Shortcuts, () =>
        {
            var removed = 0;
            foreach (var lnk in new[] { layout.StartMenuShortcut, layout.DesktopShortcut })
            {
                if (!File.Exists(lnk)) continue;
                if (IsOurShortcut(lnk))
                {
                    File.Delete(lnk);
                    removed++;
                }
                else
                {
                    report.Kept.Add(L.T($"{lnk} (hedefi bu uygulama değil)", $"{lnk} (its target is not this app)"));
                }
            }
            return removed == 0 ? L.T("Kısayol yoktu", "There were no shortcuts") : L.T($"{removed} kısayol silindi", $"{removed} shortcut(s) deleted");
        });

        var filesRemoved = Run(InstallSteps.Files, () => RemoveProgramFiles(currentExe, report));

        // Program dosyaları kaldırılamadıysa kayıt korunur: kullanıcı Ayarlar → Uygulamalar'dan yeniden deneyebilir.
        Run(InstallSteps.Unregister, () =>
        {
            if (!filesRemoved)
                throw new IOException(L.T("Program dosyaları kaldırılamadığı için kayıt korundu; kaldırmayı yeniden deneyebilirsiniz.", "The entry was kept because the program files could not be removed; you can try uninstalling again."));
            using var root = RegistryKey.OpenBaseKey(layout.Hive, RegistryView.Registry64);
            root.DeleteSubKeyTree(layout.UninstallKeyPath, false);
            using var check = root.OpenSubKey(layout.UninstallKeyPath);
            if (check is not null) throw new IOException(L.T("Kayıt anahtarı silinemedi.", "The registry key could not be deleted."));
            return L.T("Ayarlar → Uygulamalar listesinden kaldırıldı", "Removed from the Settings → Apps list");
        });

        Run(InstallSteps.Cache, () =>
        {
            var (removed, pending) = (0, 0);
            // + uygulama içi güncellemenin indirdiği kurulum dosyaları (korumalı klasörler; yalnızca bu uygulamanın öneki)
            var updateDownloads = Directory.Exists(layout.StagingRoot)
                ? Directory.EnumerateDirectories(layout.StagingRoot, UpdateService.DownloadFolderName + " *")
                : [];
            foreach (var dir in layout.CacheDirs.Where(Directory.Exists).Concat(updateDownloads))
            {
                var r = DeleteTreeSafe(dir, report);
                removed += r.Removed;
                pending += r.Pending;
            }
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                hkcu.DeleteSubKeyTree(layout.NotificationKeyPath, false);
            return (removed, pending) switch
            {
                (0, 0) => L.T("Geçici dosya yoktu; bildirim kaydı silindi", "There were no temporary files; the notification entry was deleted"),
                (_, 0) => L.T($"{removed} öğe silindi; bildirim kaydı silindi", $"{removed} item(s) deleted; the notification entry was deleted"),
                _ => L.T($"{removed} öğe silindi, {pending} öğe yeniden başlatınca silinecek (kullanımda); bildirim kaydı silindi", $"{removed} item(s) deleted, {pending} item(s) will be deleted after restart (in use); the notification entry was deleted")
            };
        });

        if (deleteUserData)
        {
            Run(InstallSteps.UserData, () =>
            {
                var dirs = layout.UserDataDirs.Where(Directory.Exists).ToList();
                if (dirs.Count == 0) return L.T("Klasör yoktu", "The folder did not exist");
                var pending = 0;
                foreach (var dir in dirs) pending += DeleteTreeSafe(dir, report).Pending;
                var names = string.Join(", ", dirs);
                return pending == 0 ? L.T($"Silindi: {names}", $"Deleted: {names}") : L.T($"Silindi: {names} ({pending} öğe yeniden başlatınca silinecek)", $"Deleted: {names} ({pending} item(s) will be deleted after restart)");
            });
        }

        if (IsStagedCopy(currentExe))
        {
            // Kaldırıcının kendi geçici kopyası (şu an çalışıyor): Windows yeniden başlatmada siler.
            var staging = Path.GetDirectoryName(currentExe)!;
            ScheduleTreeDelete(staging, report);
            log(L.T($"Kaldırıcının geçici kopyası yeniden başlatmada silinecek: {staging}", $"The uninstaller's temporary copy will be deleted at restart: {staging}"));
        }

        log(L.T($"Kaldırma bitti: {(report.Success ? "başarılı" : "sorunlu")}; yeniden başlatmada silinecek {report.PendingReboot.Count}, ", $"Uninstall finished: {(report.Success ? "successful" : "with problems")}; to delete at restart {report.PendingReboot.Count}, ") +
            L.T($"bırakılan {report.Kept.Count}, silinemeyen {report.Failed.Count}.", $"kept {report.Kept.Count}, could not delete {report.Failed.Count}."));
        return report;

        bool Run(string key, Func<string> action)
        {
            progress?.Report(new(key));
            try
            {
                var detail = action();
                report.Steps.Add(new(key, true, detail));
                log($"  {InstallSteps.Titles[key]}: {detail}");
                return true;
            }
            catch (Exception ex)
            {
                report.Steps.Add(new(key, false, ex.Message));
                log(L.T($"  {InstallSteps.Titles[key]}: BAŞARISIZ – {ex.GetType().Name}: {ex.Message}", $"  {InstallSteps.Titles[key]}: FAILED – {ex.GetType().Name}: {ex.Message}"));
                return false;
            }
        }
    }

    private string RemoveProgramFiles(string? currentExe, InstallReport report)
    {
        var exe = layout.ExePath;
        var notes = new List<string>();
        var exeScheduledInPlace = false;

        // Bir işlemin çalışma klasörü silinemez: bu işlemin çalışma klasörü program klasörüyse (veya altındaysa) önce oradan çıkılır.
        var cwd = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\') + "\\";
        if (cwd.StartsWith(Path.GetFullPath(layout.InstallDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            Environment.CurrentDirectory = Path.GetTempPath();
        if (File.Exists(exe))
        {
            if (currentExe is not null && SamePath(currentExe, exe))
            {
                // Kaldırıcı kurulu EXE'nin kendisinden çalışıyor (geçici kopya hazırlanamadıysa): çalışan EXE silinemez, taşınması da
                // güvenli değildir (tek dosya .NET, derlemeleri sonradan dosya yolundan okur; taşınan işlem çöker – denendi).
                // Windows'un yeniden başlatmada silmesi için işaretlenir.
                if (!ScheduleDelete(exe, report)) throw new IOException(L.T("Program dosyası kullanımda ve silinemedi.", "The program file is in use and could not be deleted."));
                exeScheduledInPlace = true;
            }
            else
            {
                File.Delete(exe);
            }
        }
        foreach (var extra in new[] { exe + ".new", exe + ".old" })
            if (File.Exists(extra)) DeleteOrSchedule(extra, report);

        if (Directory.Exists(layout.InstallDir))
        {
            var left = Directory.EnumerateFileSystemEntries(layout.InstallDir).ToList();
            if (left.Count == 0)
            {
                Directory.Delete(layout.InstallDir);
            }
            else if (left.All(e => report.PendingReboot.Contains(e)))
            {
                ScheduleDelete(layout.InstallDir, report);
                notes.Add(L.T("program klasörü yeniden başlatınca silinecek", "the program folder will be deleted after restart"));
            }
            else
            {
                var foreign = left.Where(e => !report.PendingReboot.Contains(e)).ToList();
                report.Kept.AddRange(foreign);
                notes.Add(L.T($"klasörde bu uygulamaya ait olmayan {foreign.Count} öğe olduğu için klasör bırakıldı", $"the folder was kept because it contains {foreign.Count} item(s) that do not belong to this app"));
            }
        }
        if (exeScheduledInPlace) notes.Add(L.T("program dosyası yeniden başlatınca silinecek", "the program file will be deleted after restart"));
        return notes.Count == 0 ? L.T($"{layout.InstallDir} silindi", $"{layout.InstallDir} deleted") : $"{layout.InstallDir}: " + string.Join("; ", notes);
    }

    /// <summary>
    /// Klasörü içeriğiyle siler; bağlantıları (junction / symlink) izlemez, yalnızca bağlantının kendisini siler. Kullanımdaki
    /// dosyalar ve onları içeren klasörler Windows'un yeniden başlatmada silmesine bırakılır.
    /// </summary>
    private (int Removed, int Pending) DeleteTreeSafe(string dir, InstallReport report)
    {
        var removed = 0;
        var pending = 0;
        Visit(new DirectoryInfo(dir));
        return (removed, pending);

        bool Visit(DirectoryInfo d)
        {
            var clean = true;
            if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (var entry in d.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub)
                    {
                        clean &= Visit(sub);
                        continue;
                    }
                    try
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                        entry.Delete();
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        clean = false;
                        if (ScheduleDelete(entry.FullName, report)) pending++;
                    }
                }
            }
            if (clean)
            {
                try
                {
                    d.Delete(false); // bağlantıysa yalnızca bağlantı silinir
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // aşağıda yeniden başlatmaya bırakılır
                }
            }
            if (ScheduleDelete(d.FullName, report)) pending++;
            return false;
        }
    }

    // ------------------------------------------------------------------ kaldırıcının geçici kopyası

    private string StagingPrefix => AppInfo.Name + " Kaldırma ";

    /// <summary>
    /// Kurulu EXE'den çalışan kaldırıcının geçici kopyasını hazırlar (SHA-256 ile doğrulanır). Kaldırma bu kopyadan yapılır: kurulu EXE
    /// kullanımda kalmaz ve hemen silinir. (Çalışan EXE'yi taşımak güvenli değildir: tek dosya .NET, derlemeleri sonradan dosya yolundan
    /// okur ve taşınan işlem çöker – denendi.) Gerçek kurulumda klasör yalnızca Yöneticiler + SYSTEM erişimlidir.
    /// </summary>
    public string PrepareStagedCopy(string currentExe)
    {
        var dir = Path.Combine(layout.StagingRoot, StagingPrefix + Guid.NewGuid().ToString("N"));
        if (layout.ProtectStaging) ProtectedDirectory.Create(dir);
        else Directory.CreateDirectory(dir);
        var staged = Path.Combine(dir, InstallLayout.ExeName);
        var hash = CopyWithHash(currentExe, staged, (_, _) => { });
        if (!string.Equals(hash, HashFile(staged), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("Kaldırıcının geçici kopyası doğrulanamadı (SHA-256 farklı).", "The uninstaller's temporary copy could not be verified (SHA-256 differs)."));
        log(L.T($"Kaldırıcının geçici kopyası hazırlandı: {staged} (SHA-256 {hash[..16]}…)", $"The uninstaller's temporary copy is ready: {staged} (SHA-256 {hash[..16]}…)"));
        return staged;
    }

    /// <summary>
    /// Geçici kopyayı kaldırma ekranıyla başlatır. Yönetici işleminden başlatıldığı için yetki devralınır (yeni UAC sorusu çıkmaz);
    /// yerel kitaplıklar da kullanıcının yazabildiği %TEMP%\.net yerine aynı korumalı klasöre çıkarılır.
    /// </summary>
    public Process StartStagedCopy(string stagedExe)
    {
        var dir = Path.GetDirectoryName(stagedExe)!;
        var psi = new ProcessStartInfo(stagedExe) { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add(LaunchModes.ArgUninstall);
        psi.ArgumentList.Add(AdminPrivilegeManager.ArgElevated);
        psi.ArgumentList.Add(LaunchModes.ArgWaitPid);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var a in LaunchModes.PreferenceArgs()) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(dir, "runtime");
        var process = Process.Start(psi) ?? throw new InvalidOperationException(L.T("Kaldırıcının geçici kopyası başlatılamadı.", "The uninstaller's temporary copy could not be started."));
        log(L.T($"Kaldırıcının geçici kopyası başlatıldı: PID {process.Id}", $"The uninstaller's temporary copy started: PID {process.Id}"));
        return process;
    }

    /// <summary>Bu yol kurulu EXE'nin kendisi mi.</summary>
    public bool IsInstalledExe(string? exe) => exe is not null && SamePath(exe, layout.ExePath);

    /// <summary>Bu yolun <see cref="PrepareStagedCopy"/> ile hazırlanmış bir geçici kopya olup olmadığı.</summary>
    public bool IsStagedCopy(string? exe)
    {
        if (exe is null || !string.Equals(Path.GetFileName(exe), InstallLayout.ExeName, StringComparison.OrdinalIgnoreCase)) return false;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
        return dir is not null && Path.GetFileName(dir).StartsWith(StagingPrefix, StringComparison.Ordinal) &&
               SamePath(Path.GetDirectoryName(dir)!, layout.StagingRoot);
    }

    /// <summary>Klasörü içeriğiyle Windows'un yeniden başlatmada silmesi için işaretler (önce dosyalar, sonra en içten dışa klasörler).</summary>
    private void ScheduleTreeDelete(string dir, InstallReport report)
    {
        if (!Directory.Exists(dir)) return;
        var directories = new List<string>();
        Visit(new DirectoryInfo(dir));
        foreach (var d in directories) ScheduleDelete(d, report);

        void Visit(DirectoryInfo d)
        {
            if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (var entry in d.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub) Visit(sub);
                    else ScheduleDelete(entry.FullName, report);
                }
            }
            directories.Add(d.FullName); // alt klasörler üst klasörden önce eklenir
        }
    }

    // ------------------------------------------------------------------ yardımcılar

    private bool IsOurShortcut(string shortcut)
    {
        var target = ShellLink.ReadTarget(shortcut);
        return target is not null && SamePath(target, layout.ExePath);
    }

    private static void CreateVerifiedShortcut(string shortcut, string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        ShellLink.Create(shortcut, exe, L.T("Windows 11 güncelleme ve bakım merkezi", "Windows 11 update and maintenance center"));
        var target = ShellLink.ReadTarget(shortcut);
        if (target is null || !SamePath(target, exe))
            throw new IOException(L.T($"Kısayol oluşturuldu ama doğrulanamadı (hedef: {target ?? "okunamadı"}).", $"The shortcut was created but could not be verified (target: {target ?? "unreadable"})."));
    }

    private void WriteUninstallKey(string exe, long size)
    {
        var uninstall = $"\"{exe}\" {LaunchModes.ArgUninstall}";
        using (var root = RegistryKey.OpenBaseKey(layout.Hive, RegistryView.Registry64))
        using (var key = root.CreateSubKey(layout.UninstallKeyPath, true))
        {
            key.SetValue("DisplayName", AppInfo.Name);
            key.SetValue("DisplayVersion", AppInfo.Version);
            key.SetValue("Publisher", AppInfo.Name);
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", layout.InstallDir);
            key.SetValue("UninstallString", uninstall);
            key.SetValue("URLInfoAbout", AppInfo.RepositoryUrl);
            key.SetValue("Comments", L.T("Windows 11 için güncelleme ve bakım merkezi", "Update and maintenance center for Windows 11"));
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // Doğrulama: anahtar yeniden açılıp okunur.
        using var verifyRoot = RegistryKey.OpenBaseKey(layout.Hive, RegistryView.Registry64);
        using var verify = verifyRoot.OpenSubKey(layout.UninstallKeyPath)
            ?? throw new IOException(L.T("Kayıt yazıldı ama yeniden açılamadı.", "The entry was written but could not be reopened."));
        if (verify.GetValue("DisplayVersion") as string != AppInfo.Version || verify.GetValue("UninstallString") as string != uninstall)
            throw new IOException(L.T("Kayıt değerleri doğrulanamadı.", "The entry values could not be verified."));
    }

    private Dictionary<string, (object Value, RegistryValueKind Kind)>? SnapshotUninstallKey()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(layout.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(layout.UninstallKeyPath);
            return key?.GetValueNames().ToDictionary(n => n, n => (key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(n)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void RestoreUninstallKey(Dictionary<string, (object Value, RegistryValueKind Kind)>? snapshot)
    {
        using var root = RegistryKey.OpenBaseKey(layout.Hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(layout.UninstallKeyPath, false);
        if (snapshot is null) return;
        using var key = root.CreateSubKey(layout.UninstallKeyPath, true);
        foreach (var (name, (value, kind)) in snapshot) key.SetValue(name, value, kind);
    }

    private void DeleteOrSchedule(string path, InstallReport report)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScheduleDelete(path, report);
        }
    }

    /// <summary>Windows'un bir sonraki yeniden başlatmada silmesi için işaretler (yönetici gerekir). Başarısızsa nedenle raporlanır.</summary>
    private bool ScheduleDelete(string path, InstallReport report)
    {
        if (MoveFileEx(path, null, MoveFileDelayUntilReboot))
        {
            report.PendingReboot.Add(path);
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        report.Failed.Add(L.T($"{path} (Windows hata {error}: {new System.ComponentModel.Win32Exception(error).Message})", $"{path} (Windows error {error}: {new System.ComponentModel.Win32Exception(error).Message})"));
        log(L.T($"Yeniden başlatmada silinmek üzere işaretlenemedi: {path} (hata {error})", $"Could not mark for deletion at restart: {path} (error {error})"));
        return false;
    }

    private static string CopyWithHash(string source, string target, Action<long, long> onProgress)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long total = input.Length, done = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                done += read;
                onProgress(done, total);
            }
            output.Flush(true);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
}

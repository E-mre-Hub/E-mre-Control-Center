using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Bir güncellemeyi engelleyen çalışan uygulamaları bulur ve YALNIZCA kullanıcı onayıyla kapatır.
///
/// Tespit: paketin kurulum klasörü (Programlar ve Özellikler / Uninstall kaydı) → klasördeki .exe/.dll dosyaları →
/// Windows Restart Manager (bu dosyaları açık tutan veya DLL olarak yüklemiş işlemler). Tahmin yapılmaz.
///
/// Asla kapatılmayanlar: Windows hizmetleri, Windows Gezgini, kritik sistem işlemleri, Windows klasöründeki işlemler,
/// başka bir kullanıcı oturumundaki işlemler ve bu uygulamanın kendisi.
///
/// Kapatma sırası (Görev Yöneticisi "Görevi sonlandır" gibi): önce pencereye normal kapatma isteği gönderilir,
/// <see cref="GracefulTimeout"/> içinde kapanmazsa işlem sonlandırılır. Kapatmadan önce PID + başlangıç zamanı
/// doğrulanır; aynı PID'yi yeniden almış başka bir işleme dokunulmaz.
/// </summary>
public static class RunningAppManager
{
    private const int MaxFiles = 4000;
    public static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------ kurulum klasörü

    /// <summary>Programlar ve Özellikler kayıtlarından paketin kurulum klasörünü bulur; güvenli bir klasör bulunamazsa null.</summary>
    public static string? FindInstallDirectory(string displayName, string? version)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var name = displayName.Trim();
        var truncated = name.EndsWith('…');
        if (truncated) name = name.TrimEnd('…').Trim();
        if (name.Length < 3) return null;

        var candidates = new List<(string Dir, int Score)>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64)
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var k = uninstall.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string dn || string.IsNullOrWhiteSpace(dn)) continue;
                    dn = dn.Trim();
                    var match = truncated
                        ? dn.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                        : dn.Equals(name, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;

                    var dir = ResolveDirectory(k);
                    if (dir is null) continue;
                    var score = 1;
                    if (!string.IsNullOrEmpty(version) &&
                        string.Equals((k.GetValue("DisplayVersion") as string)?.Trim(), version, StringComparison.OrdinalIgnoreCase))
                        score += 2;
                    candidates.Add((dir, score));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Bu kayıt görünümü okunamadı; diğerleriyle devam edilir.
            }
        }
        return candidates.OrderByDescending(c => c.Score).Select(c => c.Dir).FirstOrDefault();
    }

    private static string? ResolveDirectory(RegistryKey k)
    {
        var options = new List<string>();
        if (k.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc)) options.Add(loc.Trim().Trim('"'));
        if (ExecutableDirectory(k.GetValue("UninstallString") as string) is { } u) options.Add(u);
        if (ExecutableDirectory(k.GetValue("DisplayIcon") as string) is { } i) options.Add(i);

        foreach (var o in options)
        {
            string full;
            try { full = Path.GetFullPath(o).TrimEnd('\\'); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (Directory.Exists(full) && IsSafeInstallDirectory(full)) return full;
        }
        return null;
    }

    /// <summary>"C:\x\uninstall.exe" /S · C:\x\app.exe,0 · MsiExec.exe /X{…} → exe klasörü (MsiExec hariç).</summary>
    private static string? ExecutableDirectory(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        string path;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return null;
            path = s[1..end];
        }
        else
        {
            var idx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            path = s[..(idx + 4)];
        }
        if (Path.GetFileName(path).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? Path.GetDirectoryName(path) : null;
    }

    /// <summary>
    /// Kurulum klasörü olarak kabul edilebilir mi? Sürücü kökü, Windows, Program Files kökleri, kullanıcı ve uygulama veri
    /// kökleri ile bunların ÜST klasörleri ve bu uygulamanın klasörü reddedilir (geniş bir klasördeki tüm işlemleri
    /// yanlışlıkla hedeflememek için).
    /// </summary>
    public static bool IsSafeInstallDirectory(string dir)
    {
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2) return false;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) return false;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var protectedDirs = new[]
        {
            windows,
            programFiles,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.Combine(programFiles, "WindowsApps"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            local,
            Path.Combine(local, "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty
        };
        foreach (var p in protectedDirs)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var pp = p.TrimEnd('\\');
            if (pp.Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
            if (pp.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false; // korunan bir klasörün üstü
        }
        return true;
    }

    // ------------------------------------------------------------------ tespit

    /// <summary>Kurulum klasöründeki .exe/.dll dosyalarını kullanan çalışan işlemleri Restart Manager ile bulur.</summary>
    public static IReadOnlyList<RunningProcessInfo> FindBlockingProcesses(string installDir)
    {
        if (!IsSafeInstallDirectory(installDir) || !Directory.Exists(installDir)) return [];

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device
        };
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(installDir, "*", options))
        {
            var ext = Path.GetExtension(f);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".node", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(f);
                if (files.Count >= MaxFiles) break;
            }
        }

        var mySession = Process.GetCurrentProcess().SessionId;
        return RestartManager.GetProcessesUsing(files)
            .Where(p => p.ProcessId > 0)
            .GroupBy(p => p.ProcessId)
            .Select(g => ToInfo(g.First(), mySession))
            .ToList();
    }

    private static RunningProcessInfo ToInfo(RmProcess p, int mySession)
    {
        var exe = QueryImagePath(p.ProcessId);
        var exeName = exe is null ? null : Path.GetFileName(exe);
        var name = exeName is null
            ? (string.IsNullOrWhiteSpace(p.AppName) ? $"İşlem {p.ProcessId}" : p.AppName)
            : string.IsNullOrWhiteSpace(p.AppName) || p.AppName.Equals(exeName, StringComparison.OrdinalIgnoreCase)
                ? exeName
                : $"{exeName} – {p.AppName}";

        var type = p.Type switch
        {
            RestartManager.AppType.MainWindow => "Pencereli uygulama",
            RestartManager.AppType.OtherWindow => "Uygulama",
            RestartManager.AppType.Console => "Konsol uygulaması",
            RestartManager.AppType.Service => "Windows hizmeti",
            RestartManager.AppType.Explorer => "Windows Gezgini",
            RestartManager.AppType.Critical => "Kritik sistem işlemi",
            _ => "Arka plan işlemi"
        };

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        string? notClosable =
            p.Type == RestartManager.AppType.Service ? $"Windows hizmeti ({p.ServiceName}) – otomatik kapatılmaz"
            : p.Type == RestartManager.AppType.Explorer ? "Windows Gezgini – otomatik kapatılmaz"
            : p.Type == RestartManager.AppType.Critical ? "kritik sistem işlemi – kapatılmaz"
            : p.ProcessId == Environment.ProcessId ? "bu uygulama"
            : p.SessionId != (uint)mySession ? "başka bir kullanıcı oturumunda – kapatılmaz"
            : exe is null ? "işlem bilgisi okunamadı – kapatılmaz"
            : exe.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ? "Windows bileşeni – kapatılmaz"
            : null;

        return new RunningProcessInfo(p.ProcessId, p.StartTime, name, exe, type, notClosable is null, notClosable);
    }

    // ------------------------------------------------------------------ kapatma

    /// <summary>
    /// Onaylanan işlemleri kapatır ve her biri için gerçek sonucu döndürür. Kapatılamayan (hizmet vb.) işlemlere dokunulmaz.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CloseAsync(IEnumerable<RunningProcessInfo> processes, Logger logger)
    {
        var report = new List<string>();
        foreach (var p in processes)
        {
            var label = $"{p.Name} (PID {p.ProcessId})";
            if (!p.CanClose)
            {
                report.Add($"{label}: kapatılmadı – {p.NotClosableReason}");
                continue;
            }

            Process proc;
            try { proc = Process.GetProcessById(p.ProcessId); }
            catch (ArgumentException)
            {
                report.Add($"{label}: zaten kapanmış");
                logger.Info($"{label} zaten kapanmış.");
                continue;
            }

            using (proc)
            {
                // Aynı PID başka bir işleme geçmiş olabilir: oluşturulma zamanı tespit anındakiyle aynı olmalı.
                try
                {
                    var start = proc.StartTime.ToFileTime();
                    if (Math.Abs(start - p.StartTime) > TimeSpan.TicksPerSecond)
                    {
                        report.Add($"{label}: PID artık başka bir işleme ait – dokunulmadı");
                        logger.Warning($"{label}: PID artık başka bir işleme ait; dokunulmadı.");
                        continue;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    if (SafeHasExited(proc))
                    {
                        report.Add($"{label}: zaten kapanmış");
                        continue;
                    }
                    report.Add($"{label}: işlem doğrulanamadı ({ex.Message}) – dokunulmadı");
                    logger.Warning($"{label}: işlem doğrulanamadı ({ex.Message}); dokunulmadı.");
                    continue;
                }

                var closeRequested = false;
                try { closeRequested = proc.CloseMainWindow(); }
                catch (InvalidOperationException) { /* işlem kapanmış */ }

                if (closeRequested && await WaitForExitAsync(proc, GracefulTimeout))
                {
                    report.Add($"{label}: normal şekilde kapatıldı");
                    logger.Info($"{label} normal şekilde kapatıldı.");
                    continue;
                }
                if (SafeHasExited(proc))
                {
                    report.Add($"{label}: kapandı");
                    continue;
                }

                try
                {
                    proc.Kill();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    if (SafeHasExited(proc))
                    {
                        report.Add($"{label}: kapandı");
                        continue;
                    }
                    report.Add($"{label}: sonlandırılamadı – {ex.Message}");
                    logger.Warning($"{label} sonlandırılamadı: {ex.Message}");
                    continue;
                }

                if (await WaitForExitAsync(proc, TimeSpan.FromSeconds(5)))
                {
                    var how = closeRequested ? "normal kapatma isteğine yanıt vermedi, sonlandırıldı" : "sonlandırıldı (Görevi sonlandır)";
                    report.Add($"{label}: {how}");
                    logger.Warning($"{label} {how}.");
                }
                else
                {
                    report.Add($"{label}: sonlandırma isteğine rağmen kapanmadı");
                    logger.Warning($"{label} sonlandırma isteğine rağmen kapanmadı.");
                }
            }
        }
        return report;
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return false; }
    }

    private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return SafeHasExited(p);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return SafeHasExited(p);
        }
    }

    // ------------------------------------------------------------------ yerel API

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>İşlemin tam EXE yolu (sınırlı sorgu hakkıyla; yükseltilmiş işlemlerde de çalışır). Okunamazsa null.</summary>
    internal static string? QueryImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}

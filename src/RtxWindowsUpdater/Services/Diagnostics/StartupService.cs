using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

public enum StartupSource { RegistryUser, RegistryMachine, RegistryMachine32, FolderUser, FolderCommon, ScheduledTask, StoreApp }

/// <summary>
/// Bir başlangıç kaydı. Enabled: Windows'un Görev Yöneticisi'nde gösterdiği durum (StartupApproved); null = Windows durum kaydı yok (açık kabul edilir).
/// Toggleable: bu uygulama durumu değiştirebilir mi (yalnızca Çalıştır anahtarları ve Başlangıç klasörleri; kayıt / dosya asla silinmez).
/// </summary>
public sealed record StartupEntry(
    string Name,
    string Command,
    string? TargetPath,
    string? Publisher,
    StartupSource Source,
    bool Enabled,
    string? ProtectedReason,
    bool NeedsAdmin,
    string? Note = null)
{
    public string SourceText => Source switch
    {
        StartupSource.RegistryUser => L.T("Kayıt defteri – bu kullanıcı (HKCU Run)", "Registry – this user (HKCU Run)"),
        StartupSource.RegistryMachine => L.T("Kayıt defteri – tüm kullanıcılar (HKLM Run)", "Registry – all users (HKLM Run)"),
        StartupSource.RegistryMachine32 => L.T("Kayıt defteri – tüm kullanıcılar, 32 bit (HKLM Run)", "Registry – all users, 32-bit (HKLM Run)"),
        StartupSource.FolderUser => L.T("Başlangıç klasörü – bu kullanıcı", "Startup folder – this user"),
        StartupSource.FolderCommon => L.T("Başlangıç klasörü – tüm kullanıcılar", "Startup folder – all users"),
        StartupSource.ScheduledTask => L.T("Görev Zamanlayıcı (oturum açılışı / önyükleme)", "Task Scheduler (sign-in / boot)"),
        _ => L.T("Microsoft Store uygulaması", "Microsoft Store app")
    };
    public string StateText => Enabled ? L.T("Etkin", "Enabled") : L.T("Devre dışı", "Disabled");
    public string LocationText => TargetPath ?? "—";
    public string PublisherText => Publisher ?? L.T("Bilinmiyor (dosya sürüm bilgisi yok)", "Unknown (no file version information)");
    public bool Toggleable => ProtectedReason is null && Source is StartupSource.RegistryUser or StartupSource.RegistryMachine
        or StartupSource.RegistryMachine32 or StartupSource.FolderUser or StartupSource.FolderCommon;
}

public sealed record StartupScan(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Errors);

/// <summary>
/// Başlangıç uygulamaları: Çalıştır (Run) anahtarları, Başlangıç klasörleri, oturum açılışı / önyükleme tetikleyicili zamanlanmış
/// görevler (Microsoft görevleri hariç) ve Store uygulamalarının başlangıç görevleri. Durum Windows'un kendi StartupApproved kaydından
/// okunur (Görev Yöneticisi → Başlangıç ile aynı). Devre dışı bırakma yalnızca kullanıcı onayıyla ve Görev Yöneticisi'nin yöntemiyle
/// yapılır: Run değeri / kısayol SİLİNMEZ, yalnızca StartupApproved durum değeri yazılır ve geri okunarak doğrulanır. Windows bileşenleri
/// korunur; bilinmeyen uygulama zararlı sayılmaz (yayıncı yalnızca bilgi olarak gösterilir).
/// </summary>
public sealed class StartupService(Logger logger)
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string StoreTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private const string TasksScript = """
        $list = New-Object System.Collections.ArrayList
        foreach ($t in (Get-ScheduledTask)) {
            if ($t.TaskPath -like '\Microsoft\*') { continue }
            $hit = $false
            foreach ($tr in @($t.Triggers)) {
                if ($tr -and ($tr.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -or $tr.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger')) { $hit = $true }
            }
            if (-not $hit) { continue }
            $exe = ''; $arg = ''
            $a = @($t.Actions) | Select-Object -First 1
            if ($a -and $a.CimClass.CimClassName -eq 'MSFT_TaskExecAction') { $exe = [string]$a.Execute; $arg = [string]$a.Arguments }
            [void]$list.Add(@{ name = [string]$t.TaskName; path = [string]$t.TaskPath; state = [string]$t.State; exe = $exe; args = $arg })
        }
        Write-Result @{ tasks = @($list) }
        """;

    public async Task<StartupScan> ScanAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        var entries = await Task.Run(() => ReadLocal(errors), ct);
        var ps = await PowerShellRunner.RunAsync(TasksScript, TimeSpan.FromSeconds(60), ct, traceName: "Get-ScheduledTask");
        if (ps.Ok)
        {
            foreach (var t in ps.Data!.Value.Arr("tasks"))
            {
                var exe = Environment.ExpandEnvironmentVariables(t.Str("exe")?.Trim('"') ?? "");
                var command = string.IsNullOrEmpty(exe) ? L.T("(komut satırı olmayan eylem)", "(action without a command line)") : $"\"{exe}\" {t.Str("args")}".Trim();
                var state = t.Str("state");
                entries.Add(new StartupEntry((t.Str("path") ?? "\\") + (t.Str("name") ?? "?"), command, exe.Length == 0 ? null : exe,
                    exe.Length == 0 ? null : Publisher(exe), StartupSource.ScheduledTask,
                    !string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase), null, false,
                    L.T("Zamanlanmış görev – burada yalnızca gösterilir; Görev Zamanlayıcı'dan yönetilir.", "Scheduled task – only shown here; managed in Task Scheduler.")));
            }
        }
        else
        {
            errors.Add(L.T("Zamanlanmış görevler okunamadı: ", "Could not read scheduled tasks: ") + ps.DescribeFailure("Get-ScheduledTask"));
        }
        var ordered = entries.OrderBy(e => e.Source == StartupSource.ScheduledTask).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        logger.Info(L.T($"Başlangıç kayıtları: {ordered.Count} ({ordered.Count(e => e.Enabled)} etkin)", $"Startup entries: {ordered.Count} ({ordered.Count(e => e.Enabled)} enabled)") + (errors.Count > 0 ? "; " + string.Join("; ", errors) : "."));
        return new StartupScan(ordered, errors);
    }

    private static List<StartupEntry> ReadLocal(List<string> errors)
    {
        var list = new List<StartupEntry>();
        void Guard(string what, Action read)
        {
            try { read(); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                errors.Add(L.T($"{what} okunamadı: {ex.Message}", $"Could not read {what}: {ex.Message}"));
            }
        }
        Guard("HKCU Run", () => ReadRun(list, Registry.CurrentUser, RunPath, StartupSource.RegistryUser));
        Guard("HKLM Run", () => ReadRun(list, Registry.LocalMachine, RunPath, StartupSource.RegistryMachine));
        Guard("HKLM Run (32 bit)", () => ReadRun(list, Registry.LocalMachine, Run32Path, StartupSource.RegistryMachine32));
        Guard(L.T("Başlangıç klasörü", "Startup folder"), () => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.FolderUser));
        Guard(L.T("Ortak Başlangıç klasörü", "Common Startup folder"), () => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.FolderCommon));
        Guard(L.T("Store başlangıç görevleri", "Store startup tasks"), () => ReadStoreTasks(list, StorePackages.DisplayNamesByFamily()));
        return list;
    }

    private static void ReadRun(List<StartupEntry> list, RegistryKey hive, string path, StartupSource source)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var command = key.GetValue(name) as string ?? "";
            var target = TargetFromCommand(command);
            list.Add(new StartupEntry(name, command, target, target is null ? null : Publisher(target), source,
                IsApproved(source, name) ?? true, WindowsComponentReason(target), source != StartupSource.RegistryUser));
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.ReadTarget(file) : file;
            list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), file, target, target is null ? null : Publisher(target), source,
                IsApproved(source, fileName) ?? true, WindowsComponentReason(target), source == StartupSource.FolderCommon));
        }
    }

    /// <summary>Paketli uygulamaların StartupTask kayıtları (State: 0/1/3 kapalı, 2/4 açık). Yalnızca gösterilir; Ayarlar'dan yönetilir.</summary>
    private static void ReadStoreTasks(List<StartupEntry> list, IReadOnlyDictionary<string, string> names)
    {
        using var root = Registry.CurrentUser.OpenSubKey(StoreTasksPath);
        if (root is null) return;
        foreach (var package in root.GetSubKeyNames())
        {
            using var pk = root.OpenSubKey(package);
            if (pk is null) continue;
            foreach (var task in pk.GetSubKeyNames())
            {
                using var tk = pk.OpenSubKey(task);
                if (tk?.GetValue("State") is not int state) continue;
                var app = names.TryGetValue(package, out var display) ? display : package.Contains('_') ? package[..package.LastIndexOf('_')] : package;
                list.Add(new StartupEntry($"{app} ({task})", package, null, null, StartupSource.StoreApp, state is 2 or 4, null, false,
                    state is 3 or 4 ? L.T("Kuruluş ilkesiyle ayarlanmış.", "Set by organization policy.") : L.T("Store uygulaması – Ayarlar → Uygulamalar → Başlangıç'tan yönetilir.", "Store app – managed in Settings → Apps → Startup.")));
            }
        }
    }

    // ------------------------------------------------------------------ StartupApproved (Görev Yöneticisi durumu)

    private static (RegistryKey Hive, string SubKey) ApprovedLocation(StartupSource source) => source switch
    {
        StartupSource.RegistryUser => (Registry.CurrentUser, ApprovedPath + @"\Run"),
        StartupSource.RegistryMachine => (Registry.LocalMachine, ApprovedPath + @"\Run"),
        StartupSource.RegistryMachine32 => (Registry.LocalMachine, ApprovedPath + @"\Run32"),
        StartupSource.FolderUser => (Registry.CurrentUser, ApprovedPath + @"\StartupFolder"),
        StartupSource.FolderCommon => (Registry.LocalMachine, ApprovedPath + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    /// <summary>StartupApproved değeri: ilk bayt çift (02 / 06) = etkin, tek (03 / 01 / 07) = devre dışı; kayıt yoksa null.</summary>
    internal static bool? IsApproved(StartupSource source, string valueName)
    {
        var (hive, sub) = ApprovedLocation(source);
        using var key = hive.OpenSubKey(sub);
        return key?.GetValue(valueName) is byte[] { Length: > 0 } data ? (data[0] & 1) == 0 : null;
    }

    /// <summary>
    /// Kullanıcının onayladığı durum değişikliği. Yalnızca StartupApproved değeri yazılır (Run değeri / kısayol dokunulmaz) ve geri okunarak
    /// doğrulanır. HKLM kayıtları yönetici ister; yetki yoksa gerçek hata döner.
    /// </summary>
    public (bool Success, string Message) SetEnabled(StartupEntry entry, bool enable)
    {
        if (!entry.Toggleable) return (false, entry.ProtectedReason ?? L.T("Bu kaynak buradan değiştirilemez.", "This source cannot be changed here."));
        var valueName = entry.Source is StartupSource.FolderUser or StartupSource.FolderCommon ? Path.GetFileName(entry.Command) : entry.Name;
        var (hive, sub) = ApprovedLocation(entry.Source);
        var data = new byte[12];
        data[0] = enable ? (byte)0x02 : (byte)0x03;
        if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        try
        {
            using var key = hive.CreateSubKey(sub, writable: true);
            key.SetValue(valueName, data, RegistryValueKind.Binary);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            var msg = entry.NeedsAdmin ? L.T("Tüm kullanıcılar için olan kayıt yönetici yetkisi gerektirir: ", "Entries for all users require administrator rights: ") + ex.Message : ex.Message;
            logger.Warning(L.T($"Başlangıç durumu değiştirilemedi ({entry.Name}): {msg}", $"Could not change the startup state ({entry.Name}): {msg}"));
            return (false, L.T("Değiştirilemedi: ", "Could not change: ") + msg);
        }
        var now = IsApproved(entry.Source, valueName);
        if (now != enable)
        {
            logger.Warning(L.T($"Başlangıç durumu doğrulanamadı ({entry.Name}): yazıldı ama okunan durum {now?.ToString() ?? "yok"}.", $"Could not verify the startup state ({entry.Name}): written, but the state read back is {now?.ToString() ?? "none"}."));
            return (false, L.T("Değer yazıldı ancak geri okunan durum beklenenle aynı değil.", "The value was written but the state read back is not the expected one."));
        }
        logger.Info(L.T($"Başlangıç kaydı {(enable ? "etkinleştirildi" : "devre dışı bırakıldı")}: {entry.Name} ({entry.SourceText}); kayıt silinmedi.", $"Startup entry {(enable ? "enabled" : "disabled")}: {entry.Name} ({entry.SourceText}); the entry was not deleted."));
        return (true, enable ? L.T("Başlangıçta yeniden çalışacak.", "It will run again at startup.") : L.T("Bir sonraki oturum açılışında çalışmayacak (kayıt silinmedi; yeniden etkinleştirilebilir).", "It will not run at the next sign-in (the entry was not deleted; it can be enabled again)."));
    }

    // ------------------------------------------------------------------ yardımcılar

    /// <summary>Komut satırından çalıştırılabilir dosya yolu (tırnaklı veya ilk .exe'ye kadar); ortam değişkenleri açılır.</summary>
    internal static string? TargetFromCommand(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return c[..(exe + 4)];
        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    /// <summary>Windows klasöründeki hedefler (ör. Windows Güvenliği simgesi) korunur.</summary>
    internal static string? WindowsComponentReason(string? target)
    {
        if (target is null) return null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var full = target;
        if (!Path.IsPathRooted(full)) full = Path.Combine(Environment.SystemDirectory, full);
        return full.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ? L.T("Windows bileşeni – değiştirilmez", "Windows component – not changed") : null;
    }

    private static string? Publisher(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

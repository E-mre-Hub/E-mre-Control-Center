using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Bir gizlilik ayarının kayıt defterinden okunan gerçek değeri. SettingsUri: değişiklik için açılacak Windows Ayarlar sayfası.</summary>
public sealed record PrivacySetting(string Title, string Value, CheckState State, string? Detail, string SettingsUri)
{
    public string StateText => CheckStates.Text(State);
}

/// <summary>Kamera / mikrofon / konuma son erişen uygulama (Windows'un kendi kullanım kaydı).</summary>
public sealed record CapabilityUse(string Capability, string App, DateTime? LastStart, DateTime? LastStop, bool InUse)
{
    public string LastText => InUse ? L.T("Şu anda kullanıyor", "In use right now") : Formats.Date(LastStop ?? LastStart);
}

public sealed record PrivacyReport(IReadOnlyList<PrivacySetting> Settings, IReadOnlyList<CapabilityUse> RecentUses, string? Error);

/// <summary>
/// Gizlilik: Windows'un uygulama izinleri (CapabilityAccessManager ConsentStore – Ayarlar → Gizlilik ile aynı değerler), grup ilkesi
/// zorlamaları (AppPrivacy), tanılama verisi düzeyi, reklam kimliği, özel deneyimler ve çevrimiçi konuşma tanıma. Yalnızca OKUR;
/// değişiklik kullanıcının açtığı Windows Ayarlar sayfasında yapılır. Kaydı olmayan ayar "Windows varsayılanı" yazar, değer uydurulmaz.
/// </summary>
public sealed class PrivacyService(Logger logger)
{
    private const string ConsentStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly (string Cap, string Title, string Policy, string Uri)[] Capabilities =
    [
        ("location", L.T("Konum", "Location"), "LetAppsAccessLocation", "ms-settings:privacy-location"),
        ("webcam", L.T("Kamera", "Camera"), "LetAppsAccessCamera", "ms-settings:privacy-webcam"),
        ("microphone", L.T("Mikrofon", "Microphone"), "LetAppsAccessMicrophone", "ms-settings:privacy-microphone"),
        ("contacts", L.T("Kişiler", "Contacts"), "LetAppsAccessContacts", "ms-settings:privacy-contacts"),
        ("appointments", L.T("Takvim", "Calendar"), "LetAppsAccessCalendar", "ms-settings:privacy-calendar"),
        ("userAccountInformation", L.T("Hesap bilgileri", "Account info"), "LetAppsAccessAccountInfo", "ms-settings:privacy-accountinfo"),
        ("userNotificationListener", L.T("Bildirimler", "Notifications"), "LetAppsAccessNotifications", "ms-settings:privacy-notifications"),
        ("documentsLibrary", L.T("Belgeler", "Documents"), "", "ms-settings:privacy-documents"),
        ("picturesLibrary", L.T("Resimler", "Pictures"), "", "ms-settings:privacy-pictures"),
        ("broadFileSystemAccess", L.T("Dosya sistemi", "File system"), "", "ms-settings:privacy-broadfilesystemaccess"),
        ("radios", L.T("Radyolar (Bluetooth / Wi-Fi denetimi)", "Radios (Bluetooth / Wi-Fi control)"), "LetAppsAccessRadios", "ms-settings:privacy-radios")
    ];

    public Task<PrivacyReport> ReadAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var settings = new List<PrivacySetting>();
        var uses = new List<CapabilityUse>();
        try
        {
            foreach (var (cap, title, policy, uri) in Capabilities)
                settings.Add(ReadCapability(cap, title, policy, uri));
            settings.Add(ReadTelemetry());
            settings.Add(ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled",
                L.T("Reklam kimliği", "Advertising ID"), on: L.T("Açık (uygulamalar kişiselleştirilmiş reklam için kullanabilir)", "On (apps can use it for personalized ads)"), off: L.T("Kapalı", "Off"), "ms-settings:privacy-general", onIsWarning: true));
            settings.Add(ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled",
                L.T("Özel deneyimler (tanılama verisiyle)", "Tailored experiences (with diagnostic data)"), on: L.T("Açık", "On"), off: L.T("Kapalı", "Off"), "ms-settings:privacy-feedback", onIsWarning: false));
            settings.Add(ReadDword(Registry.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted",
                L.T("Çevrimiçi konuşma tanıma", "Online speech recognition"), on: L.T("Açık", "On"), off: L.T("Kapalı", "Off"), "ms-settings:privacy-speech", onIsWarning: false));
            var names = StorePackages.DisplayNamesByFamily();
            foreach (var cap in new[] { ("webcam", L.T("Kamera", "Camera")), ("microphone", L.T("Mikrofon", "Microphone")), ("location", L.T("Konum", "Location")) })
                uses.AddRange(ReadUses(cap.Item1, cap.Item2, names));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return new PrivacyReport(settings, uses, L.T("Gizlilik ayarları okunamadı: ", "Could not read privacy settings: ") + ex.Message);
        }
        var recent = uses.OrderByDescending(u => u.InUse).ThenByDescending(u => u.LastStop ?? u.LastStart).Take(15).ToList();
        logger.Info(L.T("Gizlilik ayarları okundu: ", "Privacy settings read: ") + string.Join(" | ", settings.Select(s => $"{s.Title}: {s.Value}")) + L.T($"; son erişim kaydı {recent.Count}.", $"; recent access records {recent.Count}."));
        return new PrivacyReport(settings, recent, null);
    }, ct);

    private static PrivacySetting ReadCapability(string cap, string title, string policy, string uri)
    {
        var device = ReadString(Registry.LocalMachine, $@"{ConsentStore}\{cap}", "Value");
        var user = ReadString(Registry.CurrentUser, $@"{ConsentStore}\{cap}", "Value");
        var desktop = ReadString(Registry.CurrentUser, $@"{ConsentStore}\{cap}\NonPackaged", "Value");
        int? forced = policy.Length == 0 ? null : ReadInt(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", policy);

        string value;
        var state = CheckState.Info;
        if (forced is 1) value = L.T("İlkeyle her zaman izinli", "Always allowed by policy");
        else if (forced is 2) value = L.T("İlkeyle engelli", "Blocked by policy");
        else if (IsDeny(device)) value = L.T("Bu cihazda kapalı", "Off on this device");
        else if (IsDeny(user)) value = L.T("Uygulamalara kapalı", "Off for apps");
        else if (IsAllow(user) || IsAllow(device)) value = L.T("Uygulamalara açık", "On for apps") + (desktop is null ? "" : IsAllow(desktop) ? L.T(" · masaüstü uygulamaları dahil", " · including desktop apps") : L.T(" · masaüstü uygulamalarına kapalı", " · off for desktop apps"));
        else value = L.T("Kayıt yok (Windows varsayılanı geçerli)", "No entry (Windows default applies)");
        var detail = forced is 1 or 2 ? L.T("Kuruluş / grup ilkesi (AppPrivacy) bu izni zorluyor; Ayarlar'dan değiştirilemez.", "An organization / group policy (AppPrivacy) enforces this permission; it cannot be changed in Settings.") : null;
        return new PrivacySetting(title, value, state, detail, uri);

        static bool IsDeny(string? v) => string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
        static bool IsAllow(string? v) => string.Equals(v, "Allow", StringComparison.OrdinalIgnoreCase);
    }

    private static PrivacySetting ReadTelemetry()
    {
        var policy = ReadInt(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");
        var setting = ReadInt(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry");
        var v = policy ?? setting;
        var text = v switch
        {
            0 => L.T("Güvenlik (yalnızca kurumsal sürümlerde; diğerlerinde Gerekli gibi davranır)", "Security (Enterprise editions only; others behave like Required)"),
            1 => L.T("Yalnızca gerekli tanılama verileri", "Required diagnostic data only"),
            2 => L.T("Gelişmiş (eski düzey)", "Enhanced (legacy level)"),
            3 => L.T("İsteğe bağlı tanılama verileri de gönderiliyor", "Optional diagnostic data is also sent"),
            null => L.T("Kayıt yok (Windows varsayılanı geçerli)", "No entry (Windows default applies)"),
            _ => L.T($"Bilinmeyen değer ({v})", $"Unknown value ({v})")
        };
        return new PrivacySetting(L.T("Tanılama verileri", "Diagnostic data"), text, CheckState.Info,
            policy is not null ? L.T("Grup ilkesiyle ayarlanmış.", "Set by group policy.") : null, "ms-settings:privacy-feedback");
    }

    private static PrivacySetting ReadDword(RegistryKey root, string path, string name, string title, string on, string off, string uri, bool onIsWarning)
    {
        var v = ReadInt(root, path, name);
        return v switch
        {
            1 => new PrivacySetting(title, on, onIsWarning ? CheckState.Warning : CheckState.Info, null, uri),
            0 => new PrivacySetting(title, off, CheckState.Info, null, uri),
            null => new PrivacySetting(title, L.T("Kayıt yok (Windows varsayılanı geçerli)", "No entry (Windows default applies)"), CheckState.Info, null, uri),
            _ => new PrivacySetting(title, L.T($"Bilinmeyen değer ({v})", $"Unknown value ({v})"), CheckState.Unknown, null, uri)
        };
    }

    /// <summary>ConsentStore altındaki uygulama kayıtlarından son kullanım zamanları (LastUsedTimeStart / Stop, FILETIME).</summary>
    private static IEnumerable<CapabilityUse> ReadUses(string cap, string title, IReadOnlyDictionary<string, string> names)
    {
        var list = new List<CapabilityUse>();
        using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{cap}");
        if (root is null) return list;
        void Add(RegistryKey key, string app)
        {
            var start = FileTime(key.GetValue("LastUsedTimeStart"));
            var stop = FileTime(key.GetValue("LastUsedTimeStop"));
            if (start is null && stop is null) return;
            var inUse = key.GetValue("LastUsedTimeStop") is long s && s == 0 && start is not null;
            list.Add(new CapabilityUse(title, app, start, stop, inUse));
        }
        foreach (var name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name);
            if (sub is null) continue;
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var exe in sub.GetSubKeyNames())
                {
                    using var ek = sub.OpenSubKey(exe);
                    if (ek is not null) Add(ek, System.IO.Path.GetFileName(exe.Replace('#', '\\')));
                }
            }
            else
            {
                Add(sub, names.TryGetValue(name, out var display) ? display : PackageName(name));
            }
        }
        return list;

        static DateTime? FileTime(object? v) => v is long l && l > 0 ? DateTime.FromFileTime(l) : null;
        // Paket aile adı "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Microsoft.WindowsCamera"
        static string PackageName(string n) => n.Contains('_') ? n[..n.LastIndexOf('_')] : n;
    }

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name) as string;
    }

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name) is int i ? i : null;
    }
}

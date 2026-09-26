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
    public string LastText => InUse ? "Şu anda kullanıyor" : Formats.Date(LastStop ?? LastStart);
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
        ("location", "Konum", "LetAppsAccessLocation", "ms-settings:privacy-location"),
        ("webcam", "Kamera", "LetAppsAccessCamera", "ms-settings:privacy-webcam"),
        ("microphone", "Mikrofon", "LetAppsAccessMicrophone", "ms-settings:privacy-microphone"),
        ("contacts", "Kişiler", "LetAppsAccessContacts", "ms-settings:privacy-contacts"),
        ("appointments", "Takvim", "LetAppsAccessCalendar", "ms-settings:privacy-calendar"),
        ("userAccountInformation", "Hesap bilgileri", "LetAppsAccessAccountInfo", "ms-settings:privacy-accountinfo"),
        ("userNotificationListener", "Bildirimler", "LetAppsAccessNotifications", "ms-settings:privacy-notifications"),
        ("documentsLibrary", "Belgeler", "", "ms-settings:privacy-documents"),
        ("picturesLibrary", "Resimler", "", "ms-settings:privacy-pictures"),
        ("broadFileSystemAccess", "Dosya sistemi", "", "ms-settings:privacy-broadfilesystemaccess"),
        ("radios", "Radyolar (Bluetooth / Wi-Fi denetimi)", "LetAppsAccessRadios", "ms-settings:privacy-radios")
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
                "Reklam kimliği", on: "Açık (uygulamalar kişiselleştirilmiş reklam için kullanabilir)", off: "Kapalı", "ms-settings:privacy-general", onIsWarning: true));
            settings.Add(ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled",
                "Özel deneyimler (tanılama verisiyle)", on: "Açık", off: "Kapalı", "ms-settings:privacy-feedback", onIsWarning: false));
            settings.Add(ReadDword(Registry.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted",
                "Çevrimiçi konuşma tanıma", on: "Açık", off: "Kapalı", "ms-settings:privacy-speech", onIsWarning: false));
            var names = StorePackages.DisplayNamesByFamily();
            foreach (var cap in new[] { ("webcam", "Kamera"), ("microphone", "Mikrofon"), ("location", "Konum") })
                uses.AddRange(ReadUses(cap.Item1, cap.Item2, names));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return new PrivacyReport(settings, uses, "Gizlilik ayarları okunamadı: " + ex.Message);
        }
        var recent = uses.OrderByDescending(u => u.InUse).ThenByDescending(u => u.LastStop ?? u.LastStart).Take(15).ToList();
        logger.Info("Gizlilik ayarları okundu: " + string.Join(" | ", settings.Select(s => $"{s.Title}: {s.Value}")) + $"; son erişim kaydı {recent.Count}.");
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
        if (forced is 1) value = "İlkeyle her zaman izinli";
        else if (forced is 2) value = "İlkeyle engelli";
        else if (IsDeny(device)) value = "Bu cihazda kapalı";
        else if (IsDeny(user)) value = "Uygulamalara kapalı";
        else if (IsAllow(user) || IsAllow(device)) value = "Uygulamalara açık" + (desktop is null ? "" : IsAllow(desktop) ? " · masaüstü uygulamaları dahil" : " · masaüstü uygulamalarına kapalı");
        else value = "Kayıt yok (Windows varsayılanı geçerli)";
        var detail = forced is 1 or 2 ? "Kuruluş / grup ilkesi (AppPrivacy) bu izni zorluyor; Ayarlar'dan değiştirilemez." : null;
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
            0 => "Güvenlik (yalnızca kurumsal sürümlerde; diğerlerinde Gerekli gibi davranır)",
            1 => "Yalnızca gerekli tanılama verileri",
            2 => "Gelişmiş (eski düzey)",
            3 => "İsteğe bağlı tanılama verileri de gönderiliyor",
            null => "Kayıt yok (Windows varsayılanı geçerli)",
            _ => $"Bilinmeyen değer ({v})"
        };
        return new PrivacySetting("Tanılama verileri", text, CheckState.Info,
            policy is not null ? "Grup ilkesiyle ayarlanmış." : null, "ms-settings:privacy-feedback");
    }

    private static PrivacySetting ReadDword(RegistryKey root, string path, string name, string title, string on, string off, string uri, bool onIsWarning)
    {
        var v = ReadInt(root, path, name);
        return v switch
        {
            1 => new PrivacySetting(title, on, onIsWarning ? CheckState.Warning : CheckState.Info, null, uri),
            0 => new PrivacySetting(title, off, CheckState.Info, null, uri),
            null => new PrivacySetting(title, "Kayıt yok (Windows varsayılanı geçerli)", CheckState.Info, null, uri),
            _ => new PrivacySetting(title, $"Bilinmeyen değer ({v})", CheckState.Unknown, null, uri)
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

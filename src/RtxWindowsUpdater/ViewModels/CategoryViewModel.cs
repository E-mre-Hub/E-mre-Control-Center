using System.Globalization;
using System.Windows.Input;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Kategori ekranının bölmeleri (sol alt menü). Aynı tür bölme birden fazla kategoride bulunabilir (ör. İşlem Günlüğü).</summary>
public static class SectionKeys
{
    public const string Cards = "cards";
    public const string Found = "found";
    public const string Log = "log";
    public const string Recent = "recent";
    public const string Health = "health";
    public const string Quick = "quick";
    public const string Admin = "admin";
    public const string LogFiles = "logfiles";
    public const string DeviceInfo = "device-info";
    public const string DeviceStatus = "device-status";
    public const string DeviceAbout = "device-about";
    public const string SpeedTest = "speedtest";
    public const string SpeedServers = "speed-servers";
    public const string SpeedHistory = "speed-history";
    public const string SpeedMethod = "speed-method";

    // v1.8.0 Sistem Tanılama bölmeleri (değerler servis katmanındaki Nav ile aynı)
    public const string Drivers = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Drivers;
    public const string Apps = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Apps;
    public const string StorageAnalysis = global::RtxWindowsUpdater.Services.Diagnostics.Nav.StorageAnalysis;
    public const string SystemHealth = global::RtxWindowsUpdater.Services.Diagnostics.Nav.SystemHealth;
    public const string StorageHealth = global::RtxWindowsUpdater.Services.Diagnostics.Nav.StorageHealth;
    public const string EventLog = global::RtxWindowsUpdater.Services.Diagnostics.Nav.EventLog;
    public const string Crash = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Crash;
    public const string Network = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Network;
    public const string Dns = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Dns;
    public const string Privacy = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Privacy;
    public const string Battery = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Battery;
    public const string Diagnose = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Diagnose;
    public const string Startup = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Startup;
    public const string Services = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Services;
    public const string Processes = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Processes;
    public const string Security = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Security;
    public const string Report = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Report;
    public const string Support = global::RtxWindowsUpdater.Services.Diagnostics.Nav.Support;
}

/// <summary>
/// Kategori ekranındaki bir bölme: sol menüde ikon + ad, sağda başlık + içerik. Bilerek referans eşitliklidir (record değil):
/// farklı kategorilerdeki aynı adlı bölmeler (ör. İşlem Günlüğü) menü listesi değişirken birbirinin yerine seçilmez.
/// </summary>
public sealed class SectionViewModel(string key, string title, string glyph)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
}

/// <summary>Kontrol Merkezi kategorilerinin anahtarları.</summary>
public static class CategoryKeys
{
    public const string Update = "update";
    public const string Cleanup = "cleanup";
    public const string Health = "health";
    public const string Settings = "settings";
    public const string Summary = "summary";
    public const string Device = "device";
    public const string SpeedTest = "speedtest";
    public const string SystemTools = global::RtxWindowsUpdater.Services.Diagnostics.Nav.SystemTools;
}

/// <summary>
/// Kontrol Merkezi ana sayfasındaki bir kategori (Güncelleme, Temizleme, Cihaz Sağlık, Hız Testi, Genel Ayarlar, Özet, Cihaz Bilgileri).
/// Yalnızca arayüz düzenidir: mevcut kartları (aynı nesneler) gruplar; iş mantığı veya yeni veri içermez.
/// </summary>
public sealed class CategoryViewModel(string key, string title, string glyph, string description,
    IReadOnlyList<ComponentCardViewModel> cards, IReadOnlyList<SectionViewModel> sections) : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private string _statusText = string.Empty;
    private ComponentStatus _status = ComponentStatus.NotChecked;

    public string Key { get; } = key;
    public string Title { get; } = title;

    /// <summary>Ana sayfa kartındaki büyük harfli başlık (Türkçe kuralıyla: "Cihaz Bilgileri" → "CİHAZ BİLGİLERİ").</summary>
    public string TileTitle { get; } = title.ToUpper(Turkish);

    public string Glyph { get; } = glyph;
    public string Description { get; } = description;

    /// <summary>Bu kategoride gösterilen mevcut bileşen kartları (boşsa kategori kendi sayfasını kullanır).</summary>
    public IReadOnlyList<ComponentCardViewModel> Cards { get; } = cards;

    public bool HasCards => Cards.Count > 0;

    /// <summary>Sol alt menüdeki bölmeler (ilki kategori açılınca seçilir).</summary>
    public IReadOnlyList<SectionViewModel> Sections { get; } = sections;

    public ICommand? OpenCommand { get; set; }

    /// <summary>Ana sayfa kartındaki kısa durum satırı (kartların / özetin mevcut gerçek durumundan).</summary>
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public ComponentStatus Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Kart içeren kategoride durum satırını kartların GERÇEK durumlarından özetler (yeni veri üretmez).</summary>
    public void RefreshFromCards()
    {
        if (!HasCards) return;
        int ok = 0, pending = 0, errors = 0, running = 0, unavailable = 0, notRun = 0;
        foreach (var c in Cards)
        {
            switch (c.Status)
            {
                case ComponentStatus.UpToDate or ComponentStatus.Updated: ok++; break;
                case ComponentStatus.UpdateAvailable or ComponentStatus.Attention or ComponentStatus.PartiallyUpdated
                    or ComponentStatus.RebootRequired: pending++; break;
                case ComponentStatus.Failed or ComponentStatus.CheckFailed or ComponentStatus.AdminRequired: errors++; break;
                case ComponentStatus.Checking or ComponentStatus.Updating: running++; break;
                case ComponentStatus.Unavailable: unavailable++; break;
                default: notRun++; break;
            }
        }

        var total = Cards.Count;
        var active = total - unavailable;
        (Status, StatusText) =
            running > 0 ? (ComponentStatus.Checking, $"{running} işlem çalışıyor...")
            : active == 0 ? (ComponentStatus.Unavailable, "Kullanım dışı")
            : errors > 0 ? (ComponentStatus.Failed, errors == 1 ? "1 işlemde hata var" : $"{errors} işlemde hata var")
            : pending > 0 ? (ComponentStatus.UpdateAvailable, $"{pending} işlem dikkat gerektiriyor")
            : ok == active ? (ComponentStatus.UpToDate, "Sorun bulunmadı")
            : ok > 0 ? (ComponentStatus.NotChecked, $"{ok} sorunsuz · {notRun} kontrol edilmedi")
            : (ComponentStatus.NotChecked, $"{active} işlem · kontrol edilmedi");
        if (unavailable > 0 && active > 0) StatusText += $" · {unavailable} kullanım dışı";
    }
}

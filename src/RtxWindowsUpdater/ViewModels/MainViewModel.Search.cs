using System.Collections.ObjectModel;
using System.Windows.Input;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Ana sayfa arama sonucu: bir kategori, bölme veya kart; seçilince ilgili bölme açılır.</summary>
public sealed record SearchResultViewModel(string Title, string Subtitle, string Glyph, string CategoryKey, string SectionKey);

/// <summary>
/// Kontrol Merkezi ana sayfası araması (Monster "Ara" kutusu): kategori, bölme ve kart adları / açıklamaları ile bölmelerin
/// anahtar kelimelerinde arar. Yalnızca gezinmedir; hiçbir işlem başlatmaz.
/// </summary>
public sealed partial class MainViewModel
{
    private const int MaxSearchResults = 8;

    /// <summary>Bölme anahtar kelimeleri (başlıkta geçmeyen ama aranabilecek sözcükler).</summary>
    private static readonly Dictionary<string, string> SectionKeywords = new()
    {
        [SectionKeys.Found] = L.T("bulunan güncellemeler paket sürüm program", "updates found package version program"),
        [SectionKeys.Log] = L.T("işlem günlüğü günlük log kayıt filtre", "operation log log record filter"),
        [SectionKeys.Recent] = L.T("işlem geçmişi son işlemler geçmiş kayıt", "operation history recent operations history record"),
        [SectionKeys.Health] = L.T("sağlık özeti durum son işlem", "health summary status last operation"),
        [SectionKeys.Quick] = L.T("kolay ayar bildirim bildirimler çöp kutusu anahtar tercih arka plan bildirim alanı tepsi simge kapatınca", "quick settings notification notifications recycle bin switch preference background notification area tray icon close"),
        [SectionKeys.Admin] = L.T("yönetici yetki uac izin", "administrator rights uac permission elevate"),
        [SectionKeys.LogFiles] = L.T("günlük dosyası log klasör dışa aktar veri klasörü", "log file log folder export data folder"),
        [SectionKeys.Legal] = L.T("yasal gizlilik politikası kullanım koşulları sözleşme lisans eula çerez telemetri kvkk gdpr sorumluluk garanti iletişim privacy terms", "legal privacy policy terms of use agreement license eula cookie telemetry kvkk gdpr liability warranty contact"),
        [SectionKeys.DeviceInfo] = L.T("işlemci cpu ekran kartı gpu bellek ram disk depolama ssd işletim sistemi windows sürüm donanım", "processor cpu graphics card gpu memory ram disk storage ssd operating system windows version hardware"),
        [SectionKeys.DeviceStatus] = L.T("performans canlı sıcaklık kullanım fan cpu gpu vram bellek ram disk ağ termal cihaz durumu", "performance live temperature usage fan cpu gpu vram memory ram disk network thermal device status"),
        [SectionKeys.DeviceAbout] = L.T("hakkında sürüm uygulama kurulum yüklü kaldır", "about version app installation installed uninstall"),
        [SectionKeys.SpeedTest] = L.T("internet hız ping indirme yükleme paket kaybı mbps titreşim", "internet speed ping download upload packet loss mbps jitter"),
        [SectionKeys.SpeedServers] = L.T("sunucu ookla speedtest cloudflare turkcell seç otomatik", "server ookla speedtest cloudflare isp select automatic"),
        [SectionKeys.SpeedHistory] = L.T("sonuçlar geçmiş hız", "results history speed"),
        [SectionKeys.SpeedMethod] = L.T("yöntem nasıl ölçülür", "method how measured"),
        // v1.8.0 Sistem Tanılama bölmeleri
        [SectionKeys.Drivers] = L.T("sürücü driver aygıt yöneticisi nvidia intel amd yonga seti chipset ağ ses bluetooth usb depolama sürücü güncellemesi", "driver drivers device manager nvidia intel amd chipset network audio bluetooth usb storage driver update"),
        [SectionKeys.Apps] = L.T("uygulamalar program kurulu yüklü yazılım winget microsoft store kaldır güncelleme", "apps programs installed software winget microsoft store uninstall update"),
        [SectionKeys.StorageAnalysis] = L.T("depolama analizi büyük dosyalar en büyük klasörler disk alanı boş alan dosya türü", "storage analysis large files largest folders disk space free space file type"),
        [SectionKeys.SystemHealth] = L.T("sistem sağlığı sfc dism windows update etkinleştirme aktivasyon lisans sürüm build yeniden başlatma kritik hizmet", "system health sfc dism windows update activation license version build restart critical service"),
        [SectionKeys.StorageHealth] = L.T("disk ssd nvme sata smart sıcaklık aşınma güvenilirlik depolama sağlığı", "disk ssd nvme sata smart temperature wear reliability storage health"),
        [SectionKeys.EventLog] = L.T("olay günlüğü event log event viewer olay görüntüleyicisi kritik hata uyarı", "event log event viewer critical error warning"),
        [SectionKeys.Crash] = L.T("çökme analizi mavi ekran bsod bugcheck minidump dump kernel-power beklenmedik kapanma", "crash analysis blue screen bsod bugcheck minidump dump kernel-power unexpected shutdown"),
        [SectionKeys.Network] = L.T("ağ merkezi wi-fi wifi kablosuz ethernet ip ipv4 ipv6 ağ geçidi gateway dhcp mac ping bağlantı internet https paket kaybı gecikme", "network center wi-fi wifi wireless ethernet ip ipv4 ipv6 gateway dhcp mac ping connection internet https packet loss latency"),
        [SectionKeys.Dns] = L.T("dns dnssec çözümleme ad sunucusu", "dns dnssec resolution name server"),
        [SectionKeys.Privacy] = L.T("gizlilik konum kamera mikrofon uygulama izinleri tanılama verisi telemetri reklam kimliği", "privacy location camera microphone app permissions diagnostic data telemetry advertising id"),
        [SectionKeys.Battery] = L.T("batarya pil şarj kapasite döngü sağlık dizüstü laptop", "battery charge capacity cycle health laptop notebook"),
        [SectionKeys.Diagnose] = L.T("tek tıkla tanıla tanılama teşhis sorun giderme sistem kontrolü", "one click diagnosis diagnostics troubleshoot system check"),
        [SectionKeys.Startup] = L.T("başlangıç uygulamaları açılış startup otomatik başlatma görev zamanlayıcı", "startup apps boot startup autostart task scheduler"),
        [SectionKeys.Services] = L.T("servis servisler hizmet hizmetler windows servisleri service", "service services windows services"),
        [SectionKeys.Processes] = L.T("işlemler process görev yöneticisi task manager cpu bellek sonlandır pid", "processes process task manager cpu memory end task kill pid"),
        [SectionKeys.Security] = L.T("güvenlik defender antivirüs virüs güvenlik duvarı firewall güvenli önyükleme secure boot uac", "security defender antivirus virus firewall secure boot uac"),
        [SectionKeys.Report] = L.T("sistem raporu rapor txt html json dışa aktar", "system report report txt html json export"),
        [SectionKeys.Support] = L.T("destek paketi zip tanılama verisi destek günlük", "support package zip diagnostic data support log")
    };

    private List<(SearchResultViewModel Item, string Alias, string Haystack, int Kind)>? _searchIndex;
    private string _searchText = string.Empty;
    private bool _isSearchOpen;

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    /// <summary>Ana sayfa arama metni; her değişiklikte sonuçlar yenilenir.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? string.Empty)) return;
            UpdateSearch();
        }
    }

    /// <summary>Sonuç listesi açık mı (dışarı tıklanınca kapanır; metin değişince yeniden açılır).</summary>
    public bool IsSearchOpen { get => _isSearchOpen; set => Set(ref _isSearchOpen, value && SearchText.Trim().Length > 0); }

    public bool HasSearchResults => SearchResults.Count > 0;
    public bool ShowNoSearchResults => SearchText.Trim().Length > 0 && SearchResults.Count == 0;

    public ICommand OpenSearchResultCommand { get; private set; } = null!;
    public ICommand OpenFirstSearchResultCommand { get; private set; } = null!;
    public ICommand ClearSearchCommand { get; private set; } = null!;

    private void InitSearch()
    {
        OpenSearchResultCommand = new RelayCommand(p => { if (p is SearchResultViewModel r) OpenSearchResult(r); });
        OpenFirstSearchResultCommand = new RelayCommand(() => { if (SearchResults.Count > 0) OpenSearchResult(SearchResults[0]); });
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    /// <summary>
    /// Dizin: kategoriler (tür 0), bölmeler (tür 1), kartlar (tür 2); kartın kısa adı (SFC, DISM, MRT…) başlıkla aynı ağırlıkta aranır.
    /// Kategoriler oluşturulduktan sonra bir kez kurulur.
    /// </summary>
    private List<(SearchResultViewModel Item, string Alias, string Haystack, int Kind)> BuildSearchIndex()
    {
        var index = new List<(SearchResultViewModel, string, string, int)>();
        foreach (var c in Categories)
        {
            var first = c.Sections[0];
            index.Add((new SearchResultViewModel(c.Title, c.Description, c.Glyph, c.Key, first.Key), string.Empty,
                $"{c.Title} {c.Description} {string.Join(' ', c.Sections.Select(s => s.Title))}", 0));
            foreach (var s in c.Sections)
            {
                var cards = s.Key == SectionKeys.Cards ? string.Join(' ', c.Cards.Select(k => $"{k.Title} {k.ShortTitle}")) : string.Empty;
                index.Add((new SearchResultViewModel(s.Title, c.Title, s.Glyph, c.Key, s.Key), string.Empty,
                    $"{s.Title} {c.Title} {SectionKeywords.GetValueOrDefault(s.Key, string.Empty)} {cards}", 1));
            }
            var cardSection = c.Sections.FirstOrDefault(s => s.Key == SectionKeys.Cards);
            if (cardSection is null) continue;
            foreach (var k in c.Cards)
                index.Add((new SearchResultViewModel(k.Title, $"{c.Title} → {cardSection.Title}", k.Glyph, c.Key, cardSection.Key), k.ShortTitle,
                    $"{k.Title} {k.ShortTitle} {k.Description} {k.CommandText}", 2));
        }
        return index;
    }

    private void UpdateSearch()
    {
        _searchIndex ??= BuildSearchIndex();
        var q = SearchText.Trim();
        SearchResults.Clear();
        if (q.Length > 0)
        {
            var ranked = _searchIndex
                .Select(e => (e.Item, e.Kind, Score: TextSearch.StartsWith(e.Item.Title, q) || TextSearch.StartsWith(e.Alias, q) ? 0
                    : TextSearch.Contains(e.Item.Title, q) || e.Alias.Length > 0 && TextSearch.Contains(e.Alias, q) ? 1
                    : TextSearch.Contains(e.Haystack, q) ? 2 : -1))
                .Where(e => e.Score >= 0)
                .OrderBy(e => e.Score).ThenBy(e => e.Kind)
                .Select(e => e.Item)
                // Aynı bölme birden çok yoldan eşleşirse bir kez gösterilir (kategori ile ilk bölmesi aynı yere götürür).
                .DistinctBy(r => (r.CategoryKey, r.SectionKey, r.Title))
                .Take(MaxSearchResults);
            foreach (var r in ranked) SearchResults.Add(r);
        }
        IsSearchOpen = q.Length > 0;
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    private void OpenSearchResult(SearchResultViewModel r)
    {
        var category = Categories.FirstOrDefault(c => c.Key == r.CategoryKey);
        var section = category?.Sections.FirstOrDefault(s => s.Key == r.SectionKey);
        _logger.Info(L.T($"Arama: \"{SearchText.Trim()}\" → {category?.Title} / {section?.Title} ({r.Title})", $"Search: \"{SearchText.Trim()}\" → {category?.Title} / {section?.Title} ({r.Title})"));
        SearchText = string.Empty;
        OpenSection(r.CategoryKey, r.SectionKey);
    }
}

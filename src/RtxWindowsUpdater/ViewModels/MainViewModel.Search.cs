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
        [SectionKeys.Found] = "bulunan güncellemeler paket sürüm program",
        [SectionKeys.Log] = "işlem günlüğü günlük log kayıt filtre",
        [SectionKeys.Recent] = "son işlemler geçmiş",
        [SectionKeys.Health] = "sağlık özeti durum son işlem",
        [SectionKeys.Quick] = "kolay ayar bildirim bildirimler çöp kutusu anahtar tercih",
        [SectionKeys.Admin] = "yönetici yetki uac izin",
        [SectionKeys.LogFiles] = "günlük dosyası log klasör dışa aktar veri klasörü",
        [SectionKeys.DeviceInfo] = "işlemci cpu ekran kartı gpu bellek ram disk depolama ssd işletim sistemi windows sürüm donanım",
        [SectionKeys.DeviceStatus] = "canlı sıcaklık kullanım fan cpu gpu bellek termal",
        [SectionKeys.DeviceAbout] = "hakkında sürüm uygulama",
        [SectionKeys.SpeedTest] = "internet hız ping indirme yükleme paket kaybı mbps titreşim",
        [SectionKeys.SpeedServers] = "sunucu ookla speedtest cloudflare turkcell seç otomatik",
        [SectionKeys.SpeedHistory] = "sonuçlar geçmiş hız",
        [SectionKeys.SpeedMethod] = "yöntem nasıl ölçülür"
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
        _logger.Info($"Arama: \"{SearchText.Trim()}\" → {category?.Title} / {section?.Title} ({r.Title})");
        SearchText = string.Empty;
        OpenSection(r.CategoryKey, r.SectionKey);
    }
}

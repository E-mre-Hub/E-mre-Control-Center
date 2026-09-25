using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Ookla aracının hazırlık durumu (Sunucu bölmesi bu duruma göre içerik gösterir).</summary>
public enum OoklaSetupState { Unknown, Checking, NotInstalled, Installing, NeedsLicense, Ready, Error }

/// <summary>Sunucu listesindeki bir Ookla sunucusu ("Istanbul - Turkcell").</summary>
public sealed class OoklaServerViewModel(OoklaServer server) : ObservableObject
{
    private bool _isSelected;

    public OoklaServer Server { get; } = server;
    public int Id => Server.Id;
    public string City => Server.Location;
    public string Sponsor => Server.Sponsor;
    public string ToolTip => $"{Server.Sponsor} · {Server.Location}, {Server.Country}\nSunucu {Server.Id} · {Server.Host}";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Ookla koşul bağlantısı (Sunucu bölmesindeki düğmeler).</summary>
public sealed record LinkItem(string Title, string Url);

/// <summary>
/// Hız Testi – sunucu seçimi (Speedtest by Ookla) ve altyapı tercihi. Ookla aracı yalnızca kullanıcı onayıyla kurulur,
/// lisans / gizlilik koşulları uygulamada kabul edilmeden çalıştırılmaz; kabul geri alınabilir.
/// </summary>
public sealed partial class SpeedTestViewModel
{
    public const string ProviderCloudflare = "cloudflare";
    public const string ProviderOokla = "ookla";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly OoklaSpeedtestService _ookla;
    private string _provider = ProviderCloudflare;
    private OoklaCli? _ooklaCli;
    private OoklaSetupState _ooklaState = OoklaSetupState.Unknown;
    private string _ooklaMessage = string.Empty;
    private bool _isInstalling;
    private bool _isLoadingServers;
    private string _searchText = string.Empty;
    private string _serversStatusText = string.Empty;
    private int? _serverId;
    private string _serverName = string.Empty;
    private List<OoklaServer> _allServers = [];
    private Task? _ensureTask;

    public ObservableCollection<OoklaServerViewModel> Servers { get; } = [];
    public IReadOnlyList<LinkItem> OoklaLinks { get; } = OoklaSpeedtestService.LicenseLinks.Select(l => new LinkItem(l.Title, l.Url)).ToList();
    public string OoklaLicenseNotice => OoklaSpeedtestService.LicenseNotice;

    public ICommand InstallOoklaCommand { get; private set; } = null!;
    public ICommand AcceptOoklaLicenseCommand { get; private set; } = null!;
    public ICommand RevokeOoklaLicenseCommand { get; private set; } = null!;
    public ICommand RefreshServersCommand { get; private set; } = null!;
    public ICommand RetryOoklaCommand { get; private set; } = null!;
    public ICommand AutoSelectServerCommand { get; private set; } = null!;
    public ICommand SelectServerCommand { get; private set; } = null!;
    public ICommand OpenLinkCommand { get; private set; } = null!;
    public ICommand OpenResultCommand { get; private set; } = null!;

    private void InitOokla()
    {
        _provider = _state.State.SpeedTestProvider == ProviderOokla ? ProviderOokla : ProviderCloudflare;
        _serverId = _state.State.SpeedTestServerId;
        _serverName = _state.State.SpeedTestServerName ?? string.Empty;
        InstallOoklaCommand = new AsyncCommand(InstallOoklaAsync,
            () => !IsWorking && !_systemBusy() && OoklaState is OoklaSetupState.NotInstalled or OoklaSetupState.Error,
            ex => _logger.Error("Ookla aracı kurulamadı: " + ex.Message));
        AcceptOoklaLicenseCommand = new AsyncCommand(AcceptLicenseAsync, () => OoklaState == OoklaSetupState.NeedsLicense && !IsWorking,
            ex => _logger.Error("Ookla koşulları kaydedilemedi: " + ex.Message));
        RevokeOoklaLicenseCommand = new RelayCommand(RevokeLicense, () => _state.State.OoklaLicenseAcceptedAt is not null && !IsWorking);
        RefreshServersCommand = new AsyncCommand(LoadServersAsync, () => OoklaState == OoklaSetupState.Ready && !IsLoadingServers && !IsWorking,
            ex => _logger.Error("Ookla sunucu listesi alınamadı: " + ex.Message));
        RetryOoklaCommand = new AsyncCommand(() => EnsureOoklaAsync(force: true), () => !IsWorking && OoklaState != OoklaSetupState.Checking,
            ex => _logger.Error("Ookla aracı denetlenemedi: " + ex.Message));
        AutoSelectServerCommand = new RelayCommand(() => SelectServer(null), () => !IsRunning);
        SelectServerCommand = new RelayCommand(p => SelectServer((p as OoklaServerViewModel)?.Server), _ => !IsRunning);
        OpenLinkCommand = new RelayCommand(p => OpenUrl(p as string));
        OpenResultCommand = new RelayCommand(() => OpenUrl(ResultUrl), () => ResultUrl is not null);
    }

    // ------------------------------------------------------------------ altyapı

    /// <summary>"ookla" / "cloudflare" (altyapı çipleri). Test sürerken değiştirilemez.</summary>
    public string ProviderMode
    {
        get => _provider;
        set
        {
            if (IsRunning || value is not (ProviderOokla or ProviderCloudflare) || value == _provider) return;
            _provider = value;
            _state.SetSpeedTestProvider(value);
            _logger.Info("Hız testi altyapısı: " + (value == ProviderOokla ? SpeedTestProviders.Ookla : SpeedTestProviders.Cloudflare));
            RaiseProviderChanged();
            if (value == ProviderOokla) _ = EnsureOoklaAsync();
        }
    }

    public bool IsOoklaProvider => _provider == ProviderOokla;
    public bool ShowConnectionChoice => !IsOoklaProvider;

    /// <summary>Seçili altyapıya göre sunucu açıklaması (Hız Testi → Test sunucusu satırı).</summary>
    public string ServerNote => IsOoklaProvider
        ? "Speedtest by Ookla: sunucuyu Sunucu bölmesinden seçersiniz (Otomatik'te Ookla seçer). Ookla her testin sonucunu (IP adresi dahil) kendi " +
          "sunucularında saklar ve bir sonuç sayfası üretir."
        : "Cloudflare: sunucu Cloudflare ağında bağlantınız için otomatik seçilir (anycast); hangi veri merkezine gidileceğini ISS'nizin yönlendirmesi " +
          "belirler. ISS'nin kendi ağındaki sunucularla test için Sunucu bölmesinden Speedtest by Ookla'yı seçebilirsiniz.";
    public bool IsInstalling { get => _isInstalling; private set { if (Set(ref _isInstalling, value)) OnWorkingChanged(); } }

    /// <summary>Test veya araç kurulumu sürüyor (sistem işlemleri bu sırada başlatılamaz).</summary>
    public bool IsWorking => IsRunning || IsInstalling;

    /// <summary>Seçili altyapıyla test başlatılabilir mi (Ookla: araç bulundu ve koşullar kabul edildi).</summary>
    public bool ProviderReady => !IsOoklaProvider || (OoklaState == OoklaSetupState.Ready && _ooklaCli is not null);

    private string? ProviderBlockedReason => !IsOoklaProvider || ProviderReady ? null : OoklaState switch
    {
        OoklaSetupState.Checking or OoklaSetupState.Unknown => "Ookla aracı denetleniyor...",
        OoklaSetupState.NotInstalled => "Speedtest by Ookla seçili ancak Ookla aracı kurulu değil. Sunucu bölmesinden kurabilir veya Cloudflare'i seçebilirsiniz.",
        OoklaSetupState.Installing => "Ookla aracı kuruluyor...",
        OoklaSetupState.NeedsLicense => "Speedtest by Ookla için Ookla'nın lisans ve gizlilik koşullarını Sunucu bölmesinde kabul etmeniz gerekiyor.",
        _ => "Ookla aracı kullanılamıyor: " + OoklaMessage
    };

    // ------------------------------------------------------------------ Ookla durumu

    public OoklaSetupState OoklaState
    {
        get => _ooklaState;
        private set
        {
            if (!Set(ref _ooklaState, value)) return;
            OnPropertyChanged(nameof(ShowOoklaChecking));
            OnPropertyChanged(nameof(ShowOoklaInstall));
            OnPropertyChanged(nameof(ShowOoklaInstalling));
            OnPropertyChanged(nameof(ShowOoklaLicense));
            OnPropertyChanged(nameof(ShowOoklaReady));
            OnPropertyChanged(nameof(ShowOoklaError));
            OnPropertyChanged(nameof(ProviderReady));
            OnPropertyChanged(nameof(BlockedReason));
            OnPropertyChanged(nameof(OoklaToolText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool ShowOoklaChecking => OoklaState is OoklaSetupState.Checking or OoklaSetupState.Unknown;
    public bool ShowOoklaInstall => OoklaState == OoklaSetupState.NotInstalled;
    public bool ShowOoklaInstalling => OoklaState == OoklaSetupState.Installing;
    public bool ShowOoklaLicense => OoklaState == OoklaSetupState.NeedsLicense;
    public bool ShowOoklaReady => OoklaState == OoklaSetupState.Ready;
    public bool ShowOoklaError => OoklaState == OoklaSetupState.Error;

    public string OoklaMessage { get => _ooklaMessage; private set => Set(ref _ooklaMessage, value); }

    /// <summary>"Speedtest by Ookla 1.2.0.84 · kabul: 25.09.2026 14:40"</summary>
    public string OoklaToolText => _ooklaCli is null ? string.Empty
        : $"{SpeedTestProviders.Ookla} {_ooklaCli.Version}" +
          (_state.State.OoklaLicenseAcceptedAt is { } at ? $" · koşullar {at:dd.MM.yyyy HH:mm} tarihinde kabul edildi" : "");

    public bool IsLoadingServers
    {
        get => _isLoadingServers;
        private set { if (Set(ref _isLoadingServers, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string ServersStatusText { get => _serversStatusText; private set => Set(ref _serversStatusText, value); }
    public bool HasServers => Servers.Count > 0;

    /// <summary>Sunucu araması (şehir, sağlayıcı, ülke veya sunucu kimliği); Türkçe büyük/küçük harf ve aksan duyarsız.</summary>
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? string.Empty)) ApplyFilter(); }
    }

    // ------------------------------------------------------------------ seçim

    public bool IsAutoSelected => _serverId is null;

    /// <summary>"Otomatik Seç" düğmesinin metni: Otomatik zaten seçiliyse bunu açıkça gösterir.</summary>
    public string AutoSelectText => IsAutoSelected ? "Otomatik seçili" : "Otomatik Seç";

    /// <summary>Seçili sunucunun başlığı (ana ekran ve Sunucu bölmesi).</summary>
    public string SelectionTitle => !IsOoklaProvider ? "Cloudflare" : _serverId is null ? "Otomatik" : SponsorOf(_serverName);

    public string SelectionTitleUpper => SelectionTitle.ToUpper(Turkish);

    public string SelectionSubtitle => !IsOoklaProvider
        ? "Otomatik · en yakın Cloudflare veri merkezi"
        : _serverId is null
            ? "Speedtest by Ookla en uygun sunucuyu seçer"
            : $"{LocationOf(_serverName)} · sunucu {_serverId}";

    private static string SponsorOf(string name) => name.Split(" · ")[0];
    private static string LocationOf(string name) => name.Contains(" · ") ? name[(name.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : string.Empty;

    private void SelectServer(OoklaServer? server)
    {
        // Aynı seçim tekrar yapılırsa hiçbir şey değişmez (kayıt / günlük yazılmaz).
        if (IsRunning || _serverId == server?.Id) return;
        _serverId = server?.Id;
        _serverName = server is null ? string.Empty : $"{server.Sponsor} · {server.Location}, {server.Country}";
        _state.SetSpeedTestServer(_serverId, _serverName);
        foreach (var s in Servers) s.IsSelected = s.Id == _serverId;
        _logger.Info("Hız testi sunucusu: " + (server is null ? "Otomatik (Ookla seçer)" : $"{_serverName} (id {server.Id})"));
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(IsAutoSelected));
        OnPropertyChanged(nameof(AutoSelectText));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionTitleUpper));
        OnPropertyChanged(nameof(SelectionSubtitle));
    }

    private void RaiseProviderChanged()
    {
        OnPropertyChanged(nameof(ProviderMode));
        OnPropertyChanged(nameof(IsOoklaProvider));
        OnPropertyChanged(nameof(ShowConnectionChoice));
        OnPropertyChanged(nameof(ServerNote));
        OnPropertyChanged(nameof(ProviderReady));
        OnPropertyChanged(nameof(BlockedReason));
        RaiseSelectionChanged();
        if (_last is null && !IsRunning) StatusText = IdleStatusText;
        CommandManager.InvalidateRequerySuggested();
    }

    private string IdleStatusText => IsOoklaProvider
        ? "Hazır. Test yaklaşık 40 saniye sürer; sonuç Ookla'ya kaydedilir."
        : "Hazır. Test yaklaşık 25 saniye sürer.";

    // ------------------------------------------------------------------ hazırlık: bul → (kur) → koşullar → sunucular

    /// <summary>Ookla aracını bulur ve durumu günceller; hazırsa (liste yoksa) sunucu listesini alır.</summary>
    public Task EnsureOoklaAsync(bool force = false)
    {
        if (_ensureTask is { IsCompleted: false }) return _ensureTask;
        if (!force && OoklaState is OoklaSetupState.Ready or OoklaSetupState.NeedsLicense or OoklaSetupState.Installing)
            return OoklaState == OoklaSetupState.Ready && _allServers.Count == 0 && !IsLoadingServers ? LoadServersAsync() : Task.CompletedTask;
        return _ensureTask = EnsureCoreAsync();
    }

    private async Task EnsureCoreAsync()
    {
        OoklaState = OoklaSetupState.Checking;
        OoklaMessage = "Ookla aracı denetleniyor...";
        _ooklaCli = await Task.Run(() => _ookla.LocateAsync(CancellationToken.None));
        if (_ooklaCli is null)
        {
            OoklaMessage = "Ookla Speedtest aracı bu bilgisayarda bulunamadı.";
            OoklaState = OoklaSetupState.NotInstalled;
            return;
        }
        OoklaMessage = $"Bulundu: {_ooklaCli.Path}";
        if (_state.State.OoklaLicenseAcceptedAt is null)
        {
            OoklaState = OoklaSetupState.NeedsLicense;
            return;
        }
        OoklaState = OoklaSetupState.Ready;
        await LoadServersAsync();
    }

    private async Task InstallOoklaAsync()
    {
        if (IsWorking || _systemBusy()) return;
        var go = await _dialog.ShowAsync("Ookla Speedtest aracı kurulsun mu?",
            "Speedtest sunucularından (ör. İstanbul'daki Turkcell, Turknet, Türksat sunucuları) test yapmak için Ookla'nın resmi komut satırı " +
            "aracı gerekir. Araç winget ile Microsoft'un paket deposundan kurulur; kurulumdan sonra ayrıca Ookla'nın koşullarını kabul etmeniz istenir.",
            MainViewModel.Icons.Download, DialogKind.Question, "Kur", "Vazgeç",
            bullets:
            [
                $"Paket: {OoklaSpeedtestService.PackageId} (yayıncı: Ookla, yaklaşık 1 MB), kaynak: winget; dosya install.speedtest.net adresinden iner",
                "Kurulum yeri: %LOCALAPPDATA%\\Microsoft\\WinGet\\Packages (kullanıcı kapsamı, yönetici yetkisi gerekmez)",
                "Ookla'nın Windows aracı dijital olarak imzalı değildir; indirilen dosya winget tarafından paket bildirimindeki SHA256 özetiyle doğrulanır",
                "Kaldırmak için: winget uninstall Ookla.Speedtest.CLI"
            ]);
        if (!go || IsWorking || _systemBusy()) return;

        IsInstalling = true;
        OoklaState = OoklaSetupState.Installing;
        OoklaMessage = "winget ile kuruluyor...";
        (OoklaCli? Cli, string Message) result;
        try
        {
            result = await Task.Run(() => _ookla.InstallAsync(CancellationToken.None));
        }
        finally
        {
            IsInstalling = false;
        }
        OoklaMessage = result.Message;
        if (result.Cli is null)
        {
            OoklaState = OoklaSetupState.Error;
            return;
        }
        _ooklaCli = result.Cli;
        OoklaState = _state.State.OoklaLicenseAcceptedAt is null ? OoklaSetupState.NeedsLicense : OoklaSetupState.Ready;
        if (OoklaState == OoklaSetupState.Ready) await LoadServersAsync();
    }

    private async Task AcceptLicenseAsync()
    {
        if (OoklaState != OoklaSetupState.NeedsLicense) return;
        _state.SetOoklaLicenseAccepted(DateTime.Now);
        _logger.Info("Ookla Speedtest lisans, kullanım ve gizlilik koşulları kullanıcı tarafından kabul edildi (" +
                     string.Join(", ", OoklaSpeedtestService.LicenseLinks.Select(l => l.Url)) + ").");
        OoklaState = OoklaSetupState.Ready;
        await LoadServersAsync();
    }

    private void RevokeLicense()
    {
        if (IsWorking) return;
        _state.SetOoklaLicenseAccepted(null);
        _logger.Info("Ookla Speedtest koşullarının kabulü geri alındı; araç, koşullar yeniden kabul edilene kadar çalıştırılmayacak.");
        _allServers = [];
        ApplyFilter();
        ServersStatusText = string.Empty;
        if (_ooklaCli is not null) OoklaState = OoklaSetupState.NeedsLicense;
        OnPropertyChanged(nameof(OoklaToolText));
    }

    private async Task LoadServersAsync()
    {
        if (_ooklaCli is null || OoklaState != OoklaSetupState.Ready || IsLoadingServers) return;
        IsLoadingServers = true;
        ServersStatusText = "Yakın sunucular alınıyor...";
        try
        {
            var path = _ooklaCli.Path;
            var (servers, error) = await Task.Run(() => _ookla.ListServersAsync(path, CancellationToken.None));
            if (servers is null)
            {
                ServersStatusText = "Sunucu listesi alınamadı: " + error;
                _logger.Warning("Ookla sunucu listesi alınamadı: " + error);
                return;
            }
            _allServers = servers;
            ApplyFilter();
            var missing = _serverId is { } id && servers.All(s => s.Id != id);
            ServersStatusText = $"{servers.Count} yakın sunucu · {DateTime.Now:HH:mm} itibarıyla Ookla'dan alındı" +
                                (missing ? $" · seçili sunucu ({_serverId}) bu listede yok, yine de kullanılır" : "");
        }
        finally
        {
            IsLoadingServers = false;
            OnPropertyChanged(nameof(OoklaToolText));
        }
    }

    private void ApplyFilter()
    {
        var q = Normalize(SearchText.Trim());
        Servers.Clear();
        foreach (var s in _allServers)
        {
            if (q.Length > 0 && !new[] { s.Sponsor, s.Location, s.Country, s.Id.ToString(CultureInfo.InvariantCulture) }
                    .Any(field => CultureInfo.InvariantCulture.CompareInfo.IndexOf(Normalize(field), q,
                        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
                continue;
            Servers.Add(new OoklaServerViewModel(s) { IsSelected = s.Id == _serverId });
        }
        OnPropertyChanged(nameof(HasServers));
    }

    /// <summary>Türkçe ı / İ harflerini aramada i / I ile eşleştirir (aksan ve büyük/küçük harf duyarsız karşılaştırmaya ek).</summary>
    private static string Normalize(string s) => s.Replace('ı', 'i').Replace('İ', 'I');

    /// <summary>Yalnızca Ookla adreslerini (koşullar ve sonuç sayfası) varsayılan tarayıcıda açar.</summary>
    private void OpenUrl(string? url)
    {
        if (url is null || !url.StartsWith("https://www.speedtest.net/", StringComparison.Ordinal)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Warning($"Bağlantı açılamadı ({url}): {ex.Message}");
        }
    }
}

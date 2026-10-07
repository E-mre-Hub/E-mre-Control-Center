using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
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
    public string ToolTip => L.T($"{Server.Sponsor} · {Server.Location}, {Server.Country}\nSunucu {Server.Id} · {Server.Host}", $"{Server.Sponsor} · {Server.Location}, {Server.Country}\nServer {Server.Id} · {Server.Host}");
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
            ex => _logger.Error(L.T("Ookla aracı kurulamadı: ", "Could not install the Ookla tool: ") + ex.Message));
        AcceptOoklaLicenseCommand = new AsyncCommand(AcceptLicenseAsync, () => OoklaState == OoklaSetupState.NeedsLicense && !IsWorking,
            ex => _logger.Error(L.T("Ookla koşulları kaydedilemedi: ", "Could not save the Ookla terms: ") + ex.Message));
        RevokeOoklaLicenseCommand = new RelayCommand(RevokeLicense, () => _state.State.OoklaLicenseAcceptedAt is not null && !IsWorking);
        RefreshServersCommand = new AsyncCommand(LoadServersAsync, () => OoklaState == OoklaSetupState.Ready && !IsLoadingServers && !IsWorking,
            ex => _logger.Error(L.T("Ookla sunucu listesi alınamadı: ", "Could not get the Ookla server list: ") + ex.Message));
        RetryOoklaCommand = new AsyncCommand(() => EnsureOoklaAsync(force: true), () => !IsWorking && OoklaState != OoklaSetupState.Checking,
            ex => _logger.Error(L.T("Ookla aracı denetlenemedi: ", "Could not check the Ookla tool: ") + ex.Message));
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
            _logger.Info(L.T("Hız testi altyapısı: ", "Speed test engine: ") + (value == ProviderOokla ? SpeedTestProviders.Ookla : SpeedTestProviders.Cloudflare));
            RaiseProviderChanged();
            if (value == ProviderOokla) _ = EnsureOoklaAsync();
        }
    }

    public bool IsOoklaProvider => _provider == ProviderOokla;
    public bool ShowConnectionChoice => !IsOoklaProvider;

    /// <summary>Seçili altyapıya göre sunucu açıklaması (Hız Testi → Test sunucusu satırı).</summary>
    public string ServerNote => IsOoklaProvider
        ? L.T("Speedtest by Ookla: sunucuyu Sunucu bölmesinden seçersiniz (Otomatik'te Ookla seçer). Ookla her testin sonucunu (IP adresi dahil) kendi ", "Speedtest by Ookla: you choose the server in the Server section (in Automatic, Ookla chooses). Ookla stores every test result (including the IP address) on its own ") +
          L.T("sunucularında saklar ve bir sonuç sayfası üretir.", "servers and creates a result page.")
        : L.T("Cloudflare: sunucu Cloudflare ağında bağlantınız için otomatik seçilir (anycast); hangi veri merkezine gidileceğini ISS'nizin yönlendirmesi ", "Cloudflare: the server is chosen automatically for your connection in the Cloudflare network (anycast); your ISP's routing decides which data center ") +
          L.T("belirler. ISS'nin kendi ağındaki sunucularla test için Sunucu bölmesinden Speedtest by Ookla'yı seçebilirsiniz.", "is used. To test with servers inside your ISP's own network, choose Speedtest by Ookla in the Server section.");
    public bool IsInstalling { get => _isInstalling; private set { if (Set(ref _isInstalling, value)) OnWorkingChanged(); } }

    /// <summary>Test veya araç kurulumu sürüyor (sistem işlemleri bu sırada başlatılamaz).</summary>
    public bool IsWorking => IsRunning || IsInstalling;

    /// <summary>Seçili altyapıyla test başlatılabilir mi (Ookla: araç bulundu ve koşullar kabul edildi).</summary>
    public bool ProviderReady => !IsOoklaProvider || (OoklaState == OoklaSetupState.Ready && _ooklaCli is not null);

    private string? ProviderBlockedReason => !IsOoklaProvider || ProviderReady ? null : OoklaState switch
    {
        OoklaSetupState.Checking or OoklaSetupState.Unknown => L.T("Ookla aracı denetleniyor...", "Checking the Ookla tool..."),
        OoklaSetupState.NotInstalled => L.T("Speedtest by Ookla seçili ancak Ookla aracı kurulu değil. Sunucu bölmesinden kurabilir veya Cloudflare'i seçebilirsiniz.", "Speedtest by Ookla is selected but the Ookla tool is not installed. You can install it in the Server section or choose Cloudflare."),
        OoklaSetupState.Installing => L.T("Ookla aracı kuruluyor...", "Installing the Ookla tool..."),
        OoklaSetupState.NeedsLicense => L.T("Speedtest by Ookla için Ookla'nın lisans ve gizlilik koşullarını Sunucu bölmesinde kabul etmeniz gerekiyor.", "For Speedtest by Ookla you need to accept Ookla's license and privacy terms in the Server section."),
        _ => L.T("Ookla aracı kullanılamıyor: ", "The Ookla tool is unavailable: ") + OoklaMessage
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
          (_state.State.OoklaLicenseAcceptedAt is { } at ? L.T($" · koşullar {at:dd.MM.yyyy HH:mm} tarihinde kabul edildi", $" · terms accepted on {at:yyyy-MM-dd HH:mm}") : "");

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
    public string AutoSelectText => IsAutoSelected ? L.T("Otomatik seçili", "Automatic selected") : L.T("Otomatik Seç", "Select Automatic");

    /// <summary>Seçili sunucunun başlığı (ana ekran ve Sunucu bölmesi).</summary>
    public string SelectionTitle => !IsOoklaProvider ? "Cloudflare" : _serverId is null ? L.T("Otomatik", "Automatic") : SponsorOf(_serverName);

    public string SelectionTitleUpper => L.Upper(SelectionTitle);

    public string SelectionSubtitle => !IsOoklaProvider
        ? L.T("Otomatik · en yakın Cloudflare veri merkezi", "Automatic · nearest Cloudflare data center")
        : _serverId is null
            ? L.T("Speedtest by Ookla en uygun sunucuyu seçer", "Speedtest by Ookla chooses the best server")
            : L.T($"{LocationOf(_serverName)} · sunucu {_serverId}", $"{LocationOf(_serverName)} · server {_serverId}");

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
        _logger.Info(L.T("Hız testi sunucusu: ", "Speed test server: ") + (server is null ? L.T("Otomatik (Ookla seçer)", "Automatic (Ookla chooses)") : $"{_serverName} (id {server.Id})"));
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
        ? L.T("Hazır. Test yaklaşık 40 saniye sürer; sonuç Ookla'ya kaydedilir.", "Ready. The test takes about 40 seconds; the result is recorded by Ookla.")
        : L.T("Hazır. Test yaklaşık 25 saniye sürer.", "Ready. The test takes about 25 seconds.");

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
        OoklaMessage = L.T("Ookla aracı denetleniyor...", "Checking the Ookla tool...");
        _ooklaCli = await Task.Run(() => _ookla.LocateAsync(CancellationToken.None));
        if (_ooklaCli is null)
        {
            OoklaMessage = L.T("Ookla Speedtest aracı bu bilgisayarda bulunamadı.", "The Ookla Speedtest tool was not found on this computer.");
            OoklaState = OoklaSetupState.NotInstalled;
            return;
        }
        OoklaMessage = L.T($"Bulundu: {_ooklaCli.Path}", $"Found: {_ooklaCli.Path}");
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
        var go = await _dialog.ShowAsync(L.T("Ookla Speedtest aracı kurulsun mu?", "Install the Ookla Speedtest tool?"),
            L.T("Speedtest sunucularından (ör. İstanbul'daki Turkcell, Turknet, Türksat sunucuları) test yapmak için Ookla'nın resmi komut satırı ", "Testing with Speedtest servers (e.g. servers of your internet provider) requires Ookla's official command-line ") +
            L.T("aracı gerekir. Araç winget ile Microsoft'un paket deposundan kurulur; kurulumdan sonra ayrıca Ookla'nın koşullarını kabul etmeniz istenir.", "tool. The tool is installed with winget from Microsoft's package repository; after installation you are also asked to accept Ookla's terms."),
            MainViewModel.Icons.Download, DialogKind.Question, L.T("Kur", "Install"), L.T("Vazgeç", "Cancel"),
            bullets:
            [
                L.T($"Paket: {OoklaSpeedtestService.PackageId} (yayıncı: Ookla, yaklaşık 1 MB), kaynak: winget; dosya install.speedtest.net adresinden iner", $"Package: {OoklaSpeedtestService.PackageId} (publisher: Ookla, about 1 MB), source: winget; the file is downloaded from install.speedtest.net"),
                L.T("Kurulum yeri: %LOCALAPPDATA%\\Microsoft\\WinGet\\Packages (kullanıcı kapsamı, yönetici yetkisi gerekmez)", "Install location: %LOCALAPPDATA%\\Microsoft\\WinGet\\Packages (user scope, no administrator rights needed)"),
                L.T("Ookla'nın Windows aracı dijital olarak imzalı değildir; indirilen dosya winget tarafından paket bildirimindeki SHA256 özetiyle doğrulanır", "Ookla's Windows tool is not digitally signed; the downloaded file is verified by winget against the SHA256 hash in the package manifest"),
                L.T("Kaldırmak için: winget uninstall Ookla.Speedtest.CLI", "To uninstall: winget uninstall Ookla.Speedtest.CLI")
            ]);
        if (!go || IsWorking || _systemBusy()) return;

        IsInstalling = true;
        OoklaState = OoklaSetupState.Installing;
        OoklaMessage = L.T("winget ile kuruluyor...", "Installing with winget...");
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
        _logger.Info(L.T("Ookla Speedtest lisans, kullanım ve gizlilik koşulları kullanıcı tarafından kabul edildi (", "The Ookla Speedtest license, terms of use and privacy terms were accepted by the user (") +
                     string.Join(", ", OoklaSpeedtestService.LicenseLinks.Select(l => l.Url)) + ").");
        OoklaState = OoklaSetupState.Ready;
        await LoadServersAsync();
    }

    private void RevokeLicense()
    {
        if (IsWorking) return;
        _state.SetOoklaLicenseAccepted(null);
        _logger.Info(L.T("Ookla Speedtest koşullarının kabulü geri alındı; araç, koşullar yeniden kabul edilene kadar çalıştırılmayacak.", "Acceptance of the Ookla Speedtest terms was withdrawn; the tool will not run until the terms are accepted again."));
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
        ServersStatusText = L.T("Yakın sunucular alınıyor...", "Getting nearby servers...");
        try
        {
            var path = _ooklaCli.Path;
            var (servers, error) = await Task.Run(() => _ookla.ListServersAsync(path, CancellationToken.None));
            if (servers is null)
            {
                ServersStatusText = L.T("Sunucu listesi alınamadı: ", "Could not get the server list: ") + error;
                _logger.Warning(L.T("Ookla sunucu listesi alınamadı: ", "Could not get the Ookla server list: ") + error);
                return;
            }
            _allServers = servers;
            ApplyFilter();
            var missing = _serverId is { } id && servers.All(s => s.Id != id);
            ServersStatusText = L.T($"{servers.Count} yakın sunucu · {DateTime.Now:HH:mm} itibarıyla Ookla'dan alındı", $"{servers.Count} nearby servers · fetched from Ookla at {DateTime.Now:HH:mm}") +
                                (missing ? L.T($" · seçili sunucu ({_serverId}) bu listede yok, yine de kullanılır", $" · the selected server ({_serverId}) is not in this list but will still be used") : "");
        }
        finally
        {
            IsLoadingServers = false;
            OnPropertyChanged(nameof(OoklaToolText));
        }
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        Servers.Clear();
        foreach (var s in _allServers)
        {
            if (!new[] { s.Sponsor, s.Location, s.Country, s.Id.ToString(CultureInfo.InvariantCulture) }.Any(field => TextSearch.Contains(field, q)))
                continue;
            Servers.Add(new OoklaServerViewModel(s) { IsSelected = s.Id == _serverId });
        }
        OnPropertyChanged(nameof(HasServers));
    }

    /// <summary>Yalnızca Ookla adreslerini (koşullar ve sonuç sayfası) varsayılan tarayıcıda açar.</summary>
    private void OpenUrl(string? url)
    {
        if (url is null || !url.StartsWith("https://www.speedtest.net/", StringComparison.Ordinal)) return;
        // Gezgin üzerinden: uygulama yönetici olarak çalışsa da tarayıcı normal yetkiyle açılır.
        if (ShellOpen.OpenUrl(url) is { } error)
            _logger.Warning(L.T($"Bağlantı açılamadı ({url}): {error}", $"Could not open the link ({url}): {error}"));
    }
}

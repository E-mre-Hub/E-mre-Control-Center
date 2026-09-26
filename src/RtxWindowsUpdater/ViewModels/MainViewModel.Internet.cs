using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Threading;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// İnternet bağlantısı gereksinimi (KULLANICI KARARI 2026-09-27): gereksinimler Windows 11 + (kartlı / kartsız) + yönetici yetkisi +
/// İNTERNET. İnternet yoksa gereksinim sayfasından girilemez; kullanım sırasında internet giderse "İnternet bağlantısı yok" ekranı
/// uygulamayı kilitler (süren işlem / hız testi yarıda kesilmez, bitince gelir) ve bağlantı gelince kendiliğinden kalkar.
/// Bağlantı değişiklikleri Windows'un ağ olaylarıyla izlenir; internet yokken 15 sn'de bir yeniden denenir.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Gerçek denetim; test düzenekleri yansımayla değiştirir.</summary>
    internal static Func<CancellationToken, Task<InternetCheckResult>> InternetProbe = InternetConnectivity.CheckAsync;

    private static readonly TimeSpan OfflineRetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NetworkEventDebounce = TimeSpan.FromSeconds(2);

    private bool _hasInternet;
    private bool _internetChecked;
    private bool _internetChecking;
    private string _internetDetail = "Kontrol ediliyor...";
    private DateTime? _internetCheckedAt;
    private DispatcherTimer? _offlineRetryTimer;
    private DispatcherTimer? _networkDebounce;
    private bool _internetMonitoring;
    private bool _pendingAcceptedEntry;

    public RequirementRowViewModel InternetRow { get; } = new("İnternet bağlantısı", "");

    public bool HasInternet
    {
        get => _hasInternet;
        private set
        {
            if (!Set(ref _hasInternet, value)) return;
            RaiseInternetChanged();
        }
    }

    public bool InternetChecked
    {
        get => _internetChecked;
        private set
        {
            if (!Set(ref _internetChecked, value)) return;
            RaiseInternetChanged();
        }
    }

    public bool IsInternetChecking
    {
        get => _internetChecking;
        private set
        {
            if (!Set(ref _internetChecking, value)) return;
            OnPropertyChanged(nameof(RetryInternetText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Son internet denetiminin gerçek sonucu (neden dahil).</summary>
    public string InternetDetail
    {
        get => _internetDetail;
        private set
        {
            if (!Set(ref _internetDetail, value)) return;
            OnPropertyChanged(nameof(OfflineDetailText));
        }
    }

    public string RetryInternetText => IsInternetChecking ? "Denetleniyor…" : "Tekrar dene";

    public string OfflineDetailText => _internetCheckedAt is { } at ? $"{InternetDetail} · son deneme {at:HH:mm:ss}" : InternetDetail;

    /// <summary>Gereksinim sayfası: internet yok uyarısı (girilemez).</summary>
    public bool ShowNoInternet => RequirementsChecked && IsSupported && InternetChecked && !HasInternet;

    /// <summary>Ana ekranda internet yok: uygulama kilitlenir (süren sistem işlemi / hız testi bitince).</summary>
    public bool ShowOfflineOverlay => IsDashboard && InternetChecked && !HasInternet && !IsBusy && SpeedTest?.IsWorking != true;

    public ICommand RetryInternetCommand { get; private set; } = null!;
    public ICommand ExitAppCommand { get; private set; } = null!;

    private void InitializeInternetCommands()
    {
        RetryInternetCommand = new AsyncCommand(() => RefreshInternetAsync(userRequested: true), () => !IsInternetChecking, OnCommandError);
        ExitAppCommand = new RelayCommand(AppLifetime.Exit, () => !IsBusy);
    }

    private void RaiseInternetChanged()
    {
        OnPropertyChanged(nameof(ShowNoInternet));
        OnPropertyChanged(nameof(ShowOfflineOverlay));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>İnternet bağlantısını gerçekten denetler ve gereksinim satırını / kilit ekranını günceller.</summary>
    public async Task RefreshInternetAsync(bool userRequested = false)
    {
        if (IsInternetChecking) return;
        IsInternetChecking = true;
        if (!InternetChecked) InternetRow.Detail = "Kontrol ediliyor...";
        InternetCheckResult result;
        try
        {
            result = await InternetProbe(CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = new InternetCheckResult(InternetState.Unreachable, "İnternet bağlantısı denetlenemedi: " + ex.Message);
        }
        finally
        {
            IsInternetChecking = false;
        }

        var changed = !InternetChecked || result.IsConnected != HasInternet;
        _internetCheckedAt = DateTime.Now;
        InternetDetail = result.Detail;
        OnPropertyChanged(nameof(OfflineDetailText));
        InternetRow.State = result.IsConnected ? RequirementState.Ok : RequirementState.Failed;
        InternetRow.Detail = result.Detail;
        HasInternet = result.IsConnected;
        InternetChecked = true;

        if (changed || userRequested)
        {
            if (result.IsConnected) _logger.Success($"İnternet bağlantısı doğrulandı ({result.Detail}).");
            else _logger.Error($"İnternet bağlantısı yok: {result.Detail} Uygulama bağlantı gelene kadar kullanılamaz.");
        }
        UpdateOfflineRetryTimer();

        // "--accepted" ile açılış (güncelleme / UAC sonrası): bağlantı gelince giriş kendiliğinden tamamlanır.
        if (HasInternet && _pendingAcceptedEntry)
        {
            _pendingAcceptedEntry = false;
            await EnterAcceptedAsync();
        }
    }

    private void UpdateOfflineRetryTimer()
    {
        if (HasInternet || !_internetMonitoring)
        {
            _offlineRetryTimer?.Stop();
            return;
        }
        if (_offlineRetryTimer is null)
        {
            _offlineRetryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = OfflineRetryInterval };
            _offlineRetryTimer.Tick += (_, _) => _ = RefreshInternetAsync();
        }
        _offlineRetryTimer.Start();
    }

    /// <summary>Windows'un ağ olaylarını izlemeye başlar (bağlantı koptu / geldi → kısa gecikmeyle yeniden denetim).</summary>
    private void StartInternetMonitoring()
    {
        if (_internetMonitoring) return;
        _internetMonitoring = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        try
        {
            // Windows'un bağlantı göstergesi (NCSI) "internet erişimi var / yok" değişince (Wi-Fi bağlı ama internet yok dahil).
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch (Exception ex)
        {
            _logger.Warning("Windows bağlantı durumu olayına abone olunamadı: " + ex.Message);
        }
        UpdateOfflineRetryTimer();
    }

    private void StopInternetMonitoring()
    {
        if (!_internetMonitoring) return;
        _internetMonitoring = false;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged; }
        catch (Exception) { /* abone olunamamıştı */ }
        _offlineRetryTimer?.Stop();
        _networkDebounce?.Stop();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => ScheduleInternetRecheck();
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => ScheduleInternetRecheck();
    private void OnNetworkStatusChanged(object? sender) => ScheduleInternetRecheck();

    /// <summary>Ağ olayları art arda gelir: 2 sn içinde gelenler tek denetimde birleştirilir.</summary>
    private void ScheduleInternetRecheck()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_internetMonitoring) return;
            if (_networkDebounce is null)
            {
                _networkDebounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = NetworkEventDebounce };
                _networkDebounce.Tick += (_, _) =>
                {
                    _networkDebounce.Stop();
                    _ = RefreshInternetAsync();
                };
            }
            _networkDebounce.Stop();
            _networkDebounce.Start();
        });
    }
}

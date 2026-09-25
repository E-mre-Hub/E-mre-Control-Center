using System.Collections.ObjectModel;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Kullanım uygunluğu (web, oyun, video, görüntülü görüşme): ölçülen gerçek değerlerden, araç ipucunda yazan sabit eşiklerle
/// hesaplanan 1-5 puan. Ölçülemeyen değer varsa puan verilmez (0 = boş noktalar).
/// </summary>
public sealed class UsageRatingViewModel(string key, string title, string glyph, string thresholds) : ObservableObject
{
    private int _score;
    private string _detail = title + ": henüz ölçülmedi.\n" + thresholds;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Thresholds { get; } = thresholds;

    /// <summary>0 = ölçülmedi, 1-5 = puan.</summary>
    public int Score
    {
        get => _score;
        set { if (Set(ref _score, Math.Clamp(value, 0, 5))) OnPropertyChanged(nameof(Dots)); }
    }

    public IReadOnlyList<bool> Dots => Enumerable.Range(1, 5).Select(i => i <= Score).ToArray();
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

/// <summary>
/// Hız Testi kategorisi. Ölçümü <see cref="SpeedTestService"/> yapar; burada yalnızca gerçek sonuçlar ekrana hazırlanır.
/// Sistem işlemi sürerken test başlatılamaz (sonuçları bozar); test sürerken de sistem işlemleri başlatılamaz (MainViewModel).
/// </summary>
public sealed partial class SpeedTestViewModel : ObservableObject, IDisposable
{
    public const string Dash = "—";
    private const int MaxHistory = 50;

    private readonly Logger _logger;
    private readonly AppStateStore _state;
    private readonly DialogViewModel _dialog;
    private readonly Func<bool> _systemBusy;
    private readonly SpeedTestOptions _options = new();

    private CancellationTokenSource? _cts;
    private int _runId;
    private SpeedTestPhase _phase = SpeedTestPhase.Idle;
    private bool _isRunning;
    private bool _useMultiple;
    private double _gaugeValue;
    private string _gaugeValueText = "0";
    private string _gaugeUnit = "Mbps";
    private string _gaugeCaption = string.Empty;
    private double _phaseProgress;
    private string _phaseText = string.Empty;
    private string _downloadText = Dash, _uploadText = Dash, _pingText = Dash, _downloadPingText = Dash, _uploadPingText = Dash;
    private string _jitterText = Dash, _packetLossText = Dash, _packetLossToolTip = "Paket kaybı henüz ölçülmedi.";
    private string _ispText = "Test başlatıldığında okunur", _clientText = string.Empty;
    private string _serverText = "Test başlatıldığında okunur", _serverDetailText = string.Empty;
    private string _statusText = "Hazır. Test yaklaşık 25 saniye sürer.";
    private ComponentStatus _statusKind = ComponentStatus.NotChecked;
    private SpeedTestResult? _last;
    private string? _resultUrl;

    public SpeedTestViewModel(Logger logger, AppStateStore state, DialogViewModel dialog, Func<bool> systemBusy)
    {
        _logger = logger;
        _state = state;
        _dialog = dialog;
        _systemBusy = systemBusy;
        _ookla = new OoklaSpeedtestService(logger);
        InitOokla();
        _statusText = IdleStatusText;
        _useMultiple = !state.State.SpeedTestSingleConnection;
        History = new ObservableCollection<SpeedTestRecord>(state.State.SpeedTests);
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        Ratings =
        [
            new UsageRatingViewModel("web", "Web", "",
                "Ölçüt: indirme hızı — ≥25 Mbps: 5 · ≥10: 4 · ≥5: 3 · ≥2: 2 · daha düşük: 1 (ping 100 ms üstündeyse 1 puan düşer)."),
            new UsageRatingViewModel("game", "Oyun", "",
                "Ölçüt: ping — ≤20 ms: 5 · ≤40: 4 · ≤60: 3 · ≤100: 2 · daha yüksek: 1 (titreşim 10 ms üstündeyse 1 puan düşer, paket kaybı %1 üstündeyse en fazla 2)."),
            new UsageRatingViewModel("video", "Video", "",
                "Ölçüt: indirme hızı — ≥25 Mbps: 5 (4K) · ≥15: 4 · ≥5: 3 (Full HD) · ≥3: 2 · daha düşük: 1."),
            new UsageRatingViewModel("call", "Görüntülü görüşme", "",
                "Ölçüt: indirme ve yüklemenin düşüğü — ≥10 Mbps: 5 · ≥3,8: 4 · ≥1,8: 3 · ≥0,6: 2 · daha düşük: 1 (ping 150 ms üstündeyse 1 puan düşer).")
        ];
        StartCommand = new AsyncCommand(StartAsync, () => !IsWorking && !_systemBusy() && ProviderReady,
            ex => _logger.Error("Hız testi başlatılamadı: " + ex.Message));
        CancelCommand = new RelayCommand(Cancel, () => IsRunning && _cts is { IsCancellationRequested: false });
    }

    /// <summary>Durum değişti (ana sayfa kutucuğu ve sistem işlemi butonları yenilenir).</summary>
    public event EventHandler? Changed;

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }

    public ObservableCollection<SpeedTestRecord> History { get; }
    public bool HasHistory => History.Count > 0;
    public ObservableCollection<UsageRatingViewModel> Ratings { get; }

    public SpeedTestPhase Phase { get => _phase; private set => Set(ref _phase, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) OnWorkingChanged(); }
    }

    private void OnWorkingChanged()
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(BlockedReason));
        CommandManager.InvalidateRequerySuggested();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Çoklu bağlantı (6 paralel TCP bağlantısı) veya tek bağlantı; tercih saklanır.</summary>
    public bool UseMultipleConnections
    {
        get => _useMultiple;
        set
        {
            if (IsRunning || !Set(ref _useMultiple, value)) return;
            OnPropertyChanged(nameof(ConnectionMode));
            _state.SetSpeedTestSingleConnection(!value);
        }
    }

    /// <summary>"multi" / "single" (bağlantı seçim çipleri için).</summary>
    public string ConnectionMode
    {
        get => _useMultiple ? "multi" : "single";
        set { if (value is "multi" or "single") UseMultipleConnections = value == "multi"; }
    }

    public string MultiLabel => $"Çoklu ({SpeedTestService.MultiStreams})";

    /// <summary>Başlatma engelliyse nedeni (sistem işlemi sürüyor / seçili altyapı hazır değil).</summary>
    public string BlockedReason =>
        IsRunning ? string.Empty
        : _systemBusy() ? "Bir kontrol / güncelleme işlemi sürüyor. Hız testi, sonuçları etkilememesi için işlem bitince başlatılabilir."
        : ProviderBlockedReason ?? string.Empty;

    /// <summary>Son Ookla testinin sonuç sayfası (https://www.speedtest.net/result/...).</summary>
    public string? ResultUrl
    {
        get => _resultUrl;
        private set { if (Set(ref _resultUrl, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public double GaugeValue { get => _gaugeValue; private set => Set(ref _gaugeValue, value); }
    public bool GaugeIsUpload => Phase == SpeedTestPhase.Upload;
    public string GaugeValueText { get => _gaugeValueText; private set => Set(ref _gaugeValueText, value); }
    public string GaugeUnit { get => _gaugeUnit; private set => Set(ref _gaugeUnit, value); }
    public string GaugeCaption { get => _gaugeCaption; private set => Set(ref _gaugeCaption, value); }

    /// <summary>Çalışan aşamada gerçekten geçen sürenin oranı (%).</summary>
    public double PhaseProgress { get => _phaseProgress; private set => Set(ref _phaseProgress, value); }
    public string PhaseText { get => _phaseText; private set => Set(ref _phaseText, value); }

    public string DownloadText { get => _downloadText; private set => Set(ref _downloadText, value); }
    public string UploadText { get => _uploadText; private set => Set(ref _uploadText, value); }
    public string PingText { get => _pingText; private set => Set(ref _pingText, value); }
    public string DownloadPingText { get => _downloadPingText; private set => Set(ref _downloadPingText, value); }
    public string UploadPingText { get => _uploadPingText; private set => Set(ref _uploadPingText, value); }
    public string JitterText { get => _jitterText; private set => Set(ref _jitterText, value); }
    public string PacketLossText { get => _packetLossText; private set => Set(ref _packetLossText, value); }
    public string PacketLossToolTip { get => _packetLossToolTip; private set => Set(ref _packetLossToolTip, value); }

    public string IspText { get => _ispText; private set => Set(ref _ispText, value); }
    public string ClientText { get => _clientText; private set => Set(ref _clientText, value); }
    public string ServerText { get => _serverText; private set => Set(ref _serverText, value); }
    public string ServerDetailText { get => _serverDetailText; private set => Set(ref _serverDetailText, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public ComponentStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>Son çalıştırılan testin gerçek sonucu (bu oturumda).</summary>
    public SpeedTestResult? LastResult => _last;

    /// <summary>Ana sayfa kutucuğunun durum satırı (son gerçek test veya sürmekte olan test).</summary>
    public (ComponentStatus Status, string Text) Tile
    {
        get
        {
            if (IsRunning) return (ComponentStatus.Checking, "Test sürüyor...");
            if (IsInstalling) return (ComponentStatus.Checking, "Ookla aracı kuruluyor...");
            var last = History.FirstOrDefault();
            if (last is null) return (ComponentStatus.NotChecked, "Henüz test yapılmadı");
            if (last.DownloadMbps is null && last.UploadMbps is null) return (ComponentStatus.Failed, "Son test başarısız");
            return (last.Status, $"{last.DownloadText} / {last.UploadText} Mbps · {last.PingText} ms");
        }
    }

    /// <summary>Sistem işleminin başlaması / bitmesi (MainViewModel bildirir).</summary>
    public void OnSystemBusyChanged()
    {
        OnPropertyChanged(nameof(BlockedReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task StartAsync()
    {
        if (IsWorking || _systemBusy() || !ProviderReady) return;
        var metered = MeteredDescription();
        if (metered is not null)
        {
            var go = await _dialog.ShowAsync("Tarifeli bağlantı",
                $"Windows bu bağlantıyı tarifeli olarak bildiriyor ({metered}).\n\nHız testi, bağlantı hızına göre onlarca ile yüzlerce MB veri " +
                "kullanır. Teste başlansın mı?",
                MainViewModel.Icons.Warning, DialogKind.Warning, "Teste başla", "Vazgeç");
            if (!go) return;
            _logger.Info($"Hız testi tarifeli bağlantıda kullanıcı onayıyla başlatıldı ({metered}).");
        }
        if (IsWorking || _systemBusy() || !ProviderReady) return;

        var run = ++_runId;
        ResetDisplay();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Phase = SpeedTestPhase.Connecting;
        OnPhaseChanged();
        SpeedTestResult result;
        try
        {
            var progress = new Progress<SpeedTestProgress>(p => { if (run == _runId && IsRunning) OnProgress(p); });
            var token = _cts.Token;
            if (IsOoklaProvider && _ooklaCli is { } cli)
            {
                var serverId = _serverId;
                result = await Task.Run(() => _ookla.RunAsync(cli.Path, serverId, progress, token));
            }
            else
            {
                var service = new SpeedTestService(_logger, _options);
                var multiple = UseMultipleConnections;
                result = await Task.Run(() => service.RunAsync(multiple, progress, token));
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
        _last = result;
        Phase = result.Phase;
        ApplyResult(result);
        if (result.Phase is SpeedTestPhase.Completed or SpeedTestPhase.Failed)
        {
            var record = ToRecord(result);
            _state.AddSpeedTest(record);
            History.Insert(0, record);
            while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
        }
        IsRunning = false; // Changed → kutucuk ve sistem butonları
        OnPhaseChanged();
    }

    private void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _logger.Info("Hız testi kullanıcı tarafından iptal edildi.");
        _cts.Cancel();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ResetDisplay()
    {
        GaugeValue = 0;
        GaugeValueText = "0";
        GaugeUnit = "Mbps";
        PhaseProgress = 0;
        DownloadText = UploadText = PingText = DownloadPingText = UploadPingText = JitterText = PacketLossText = Dash;
        PacketLossToolTip = "Paket kaybı ölçülüyor...";
        ResultUrl = null;
        foreach (var r in Ratings)
        {
            r.Score = 0;
            r.Detail = r.Title + ": ölçülüyor...\n" + r.Thresholds;
        }
        StatusText = "Test sürüyor...";
        StatusKind = ComponentStatus.Checking;
    }

    private void OnPhaseChanged()
    {
        OnPropertyChanged(nameof(GaugeIsUpload));
        (GaugeCaption, PhaseText) = Phase switch
        {
            SpeedTestPhase.Connecting => ("Bağlanıyor", "Sunucuya bağlanılıyor..."),
            SpeedTestPhase.Latency => ("Ping", IsOoklaProvider ? "Gecikme ölçülüyor..." : "Gecikme ve paket kaybı ölçülüyor..."),
            SpeedTestPhase.Download => ("İndirme", "İndirme hızı ölçülüyor..."),
            SpeedTestPhase.Upload => ("Yükleme", "Yükleme hızı ölçülüyor..."),
            _ => (string.Empty, string.Empty)
        };
    }

    private void OnProgress(SpeedTestProgress p)
    {
        if (p.Phase != Phase)
        {
            Phase = p.Phase;
            OnPhaseChanged();
            GaugeValue = 0;
            GaugeValueText = "0";
        }
        if (p.Server is { } server && _serverText != server.ServerName) ApplyServer(server);
        // Biten aşamaların gerçek sonuçları test bitmeden gösterilir (ör. yükleme sürerken indirme sonucu)
        if (p.IdleLatency is { } idle)
        {
            PingText = $"{idle.MedianMs:0}";
            JitterText = $"{idle.JitterMs:0.0}";
        }
        if (p.PacketLoss is not null || p.PacketLossNote is not null) ApplyPacketLoss(p.PacketLoss, p.PacketLossNote);
        if (p.Download is { } download) DownloadText = SpeedTestRecord.FormatMbps(download.Mbps);
        if (p.DownloadLatency is { } downloadLatency) DownloadPingText = $"{downloadLatency.MedianMs:0}";
        PhaseProgress = p.PhaseFraction * 100;
        switch (p.Phase)
        {
            case SpeedTestPhase.Latency:
                GaugeUnit = "ms";
                GaugeValue = 0;
                if (p.LatencyMs is { } ms)
                {
                    GaugeValueText = $"{ms:0}";
                    PingText = $"{ms:0}";
                }
                break;
            case SpeedTestPhase.Download or SpeedTestPhase.Upload:
                GaugeUnit = "Mbps";
                if (p.CurrentMbps is { } mbps)
                {
                    GaugeValue = mbps;
                    GaugeValueText = SpeedTestRecord.FormatMbps(mbps);
                }
                break;
        }
    }

    private void ApplyServer(SpeedTestServer s)
    {
        IspText = s.IspName;
        ClientText = string.Join(" · ", new[] { s.ClientIp.Length > 0 ? "IP " + s.ClientIp : "", s.ClientLocation }.Where(x => x.Length > 0));
        ServerText = s.ServerName;
        ServerDetailText = s.Provider == SpeedTestProviders.Ookla
            ? string.Join(" · ", new[] { s.ProviderText, s.HttpProtocol, s.ServerAddress.Length > 0 ? "Adres " + s.ServerAddress : "" }.Where(x => x.Length > 0))
            : string.Join(" · ", new[] { s.ProviderText, s.ServerAddress.Length > 0 ? "Adres " + s.ServerAddress : "", "HTTPS (TLS)", s.HttpProtocol }
                .Where(x => x.Length > 0));
    }

    private void ApplyResult(SpeedTestResult r)
    {
        if (r.Server is { } server) ApplyServer(server);
        DownloadText = SpeedTestRecord.FormatMbps(r.Download?.Mbps);
        UploadText = SpeedTestRecord.FormatMbps(r.Upload?.Mbps);
        PingText = r.IdleLatency is { } idle ? $"{idle.MedianMs:0}" : Dash;
        JitterText = r.IdleLatency is { } j ? $"{j.JitterMs:0.0}" : Dash;
        DownloadPingText = r.DownloadLatency is { } dl ? $"{dl.MedianMs:0}" : Dash;
        UploadPingText = r.UploadLatency is { } ul ? $"{ul.MedianMs:0}" : Dash;
        if (r.Phase == SpeedTestPhase.Cancelled && r.PacketLoss is null && r.PacketLossNote is null)
        {
            PacketLossText = Dash;
            PacketLossToolTip = "Paket kaybı ölçülmedi (test iptal edildi).";
        }
        else ApplyPacketLoss(r.PacketLoss, r.PacketLossNote ?? (r.PacketLoss is null ? "test tamamlanmadı" : null));
        RateUsage(r);

        GaugeValue = 0;
        GaugeValueText = "0";
        PhaseProgress = r.Phase == SpeedTestPhase.Completed ? 100 : PhaseProgress;
        ResultUrl = r.ResultUrl;
        var seconds = $"{r.Duration.TotalSeconds:0} sn";
        var data = $"{r.DataUsedBytes / 1_000_000.0:0.0} MB veri kullanıldı" + (r.ResultUrl is null ? "" : " · sonuç Ookla'ya kaydedildi");
        (StatusText, StatusKind) = r.Phase switch
        {
            SpeedTestPhase.Completed when r.IsComplete && r.Notes.Count == 0 =>
                ($"Test tamamlandı · {seconds} · {data}", ComponentStatus.UpToDate),
            SpeedTestPhase.Completed =>
                ($"Kısmen ölçüldü · {seconds} · {data} · {string.Join(" · ", r.Notes)}", ComponentStatus.Attention),
            SpeedTestPhase.Cancelled => ($"Test iptal edildi · {seconds}", ComponentStatus.Skipped),
            _ => ("Test başarısız: " + r.Error, ComponentStatus.Failed)
        };
    }

    private void ApplyPacketLoss(PacketLossStats? pl, string? note)
    {
        if (pl is { LossPercent: { } loss })
        {
            PacketLossText = $"%{loss:0.#}";
            PacketLossToolTip = pl.ReportedPercent is not null
                ? "Speedtest by Ookla aracının bildirdiği paket kaybı (gönderilen / alınan paket sayısını bildirmez)."
                : $"ICMP yankı isteği: {pl.Sent} gönderildi, {pl.Received} yanıt alındı" +
                  (pl.MedianRttMs is { } rtt ? $" (ICMP ping medyanı {rtt:0} ms)." : ".");
        }
        else
        {
            PacketLossText = "Ölçülemedi";
            PacketLossToolTip = "Paket kaybı ölçülemedi: " + (note ?? "yanıt yok");
        }
    }

    private void RateUsage(SpeedTestResult r)
    {
        double? down = r.Download?.Mbps, up = r.Upload?.Mbps, ping = r.IdleLatency?.MedianMs, jitter = r.IdleLatency?.JitterMs;
        var loss = r.PacketLoss?.LossPercent;
        foreach (var rating in Ratings)
        {
            int? score = rating.Key switch
            {
                "web" => down is { } d ? Step(d, 25, 10, 5, 2) - (ping > 100 ? 1 : 0) : null,
                "game" => ping is { } p
                    ? Math.Min(StepDown(p, 20, 40, 60, 100) - (jitter > 10 ? 1 : 0), loss > 1 ? 2 : 5)
                    : null,
                "video" => down is { } d2 ? Step(d2, 25, 15, 5, 3) : null,
                "call" => down is { } d3 && up is { } u ? Step(Math.Min(d3, u), 10, 3.8, 1.8, 0.6) - (ping > 150 ? 1 : 0) : null,
                _ => null
            };
            rating.Score = score is { } s ? Math.Max(1, s) : 0;
            rating.Detail = score is null
                ? $"{rating.Title}: gerekli değer ölçülemediği için puan verilmedi.\n{rating.Thresholds}"
                : $"{rating.Title}: {rating.Score}/5 (ölçülen değerlerden).\n{rating.Thresholds}";
        }

        static int Step(double v, double t5, double t4, double t3, double t2) => v >= t5 ? 5 : v >= t4 ? 4 : v >= t3 ? 3 : v >= t2 ? 2 : 1;
        static int StepDown(double v, double t5, double t4, double t3, double t2) => v <= t5 ? 5 : v <= t4 ? 4 : v <= t3 ? 3 : v <= t2 ? 2 : 1;
    }

    private static SpeedTestRecord ToRecord(SpeedTestResult r) => new()
    {
        CompletedAt = r.StartedAt + r.Duration,
        DownloadMbps = r.Download?.Mbps,
        UploadMbps = r.Upload?.Mbps,
        PingMs = r.IdleLatency?.MedianMs,
        JitterMs = r.IdleLatency?.JitterMs,
        DownloadPingMs = r.DownloadLatency?.MedianMs,
        UploadPingMs = r.UploadLatency?.MedianMs,
        PacketLossPercent = r.PacketLoss?.LossPercent,
        Isp = r.Server?.IspName ?? string.Empty,
        Server = r.Server?.ServerName ?? string.Empty,
        Provider = r.Provider,
        ResultUrl = r.ResultUrl,
        MultipleConnections = r.MultipleConnections,
        Streams = r.Provider == SpeedTestProviders.Ookla ? 0
            : r.Download?.Streams ?? r.Upload?.Streams ?? (r.MultipleConnections ? SpeedTestService.MultiStreams : 1),
        DataUsedBytes = r.DataUsedBytes,
        DurationMs = (long)r.Duration.TotalMilliseconds,
        Error = r.Phase == SpeedTestPhase.Failed ? r.Error : r.Notes.Count > 0 ? string.Join(" · ", r.Notes) : null
    };

    /// <summary>Windows bağlantıyı tarifeli bildiriyorsa açıklaması; değilse (veya okunamazsa) null.</summary>
    private string? MeteredDescription()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            var cost = profile?.GetConnectionCost();
            if (cost is null) return null;
            var reasons = new List<string>();
            if (cost.NetworkCostType == Windows.Networking.Connectivity.NetworkCostType.Fixed) reasons.Add("sabit veri limitli");
            if (cost.NetworkCostType == Windows.Networking.Connectivity.NetworkCostType.Variable) reasons.Add("kullanıma göre ücretli");
            if (cost.Roaming) reasons.Add("dolaşımda");
            if (cost.OverDataLimit) reasons.Add("veri limiti aşıldı");
            else if (cost.ApproachingDataLimit) reasons.Add("veri limitine yaklaşıldı");
            return reasons.Count == 0 ? null : string.Join(", ", reasons);
        }
        catch (Exception ex)
        {
            _logger.Warning("Bağlantının tarifeli olup olmadığı okunamadı: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}

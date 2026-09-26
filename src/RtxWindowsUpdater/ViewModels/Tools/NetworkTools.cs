using System.Collections.ObjectModel;
using System.Windows.Input;
using RtxWindowsUpdater.Services.Diagnostics;

namespace RtxWindowsUpdater.ViewModels.Tools;

/// <summary>Ağ Merkezi ve DNS Tanılama'nın paylaştığı son ağ okuması (aynı bağdaştırıcı listesi iki ekranda yeniden okunmaz).</summary>
public sealed class NetworkState
{
    public NetworkSnapshot? Snapshot { get; set; }
    public IReadOnlyList<NetTestResult>? Tests { get; set; }
    public DnsReport? Dns { get; set; }
}

// ====================================================================== Ağ Merkezi

public sealed class NetworkViewModel : ToolViewModel
{
    private readonly NetworkDiagnosticsService _service;
    private readonly NetworkState _state;
    private bool _showInactive;
    private string _testStatus = "Testler henüz çalıştırılmadı. Testler gerçek ağ trafiği kullanır (yaklaşık 40 ping, 3 DNS sorgusu, 2 HTTPS isteği).";

    public NetworkViewModel(IToolHost host, NetworkState state) : base(host)
    {
        _service = new NetworkDiagnosticsService(host.Logger);
        _state = state;
        RunTestsCommand = new AsyncCommand(() => RunAsync(RunTestsAsync), () => !IsBusy);
    }

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<CheckRowViewModel> Tests { get; } = [];
    public ICommand RunTestsCommand { get; }
    public string ConnectivityText => _state.Snapshot?.ConnectivityText ?? "—";
    public string ProfileText => _state.Snapshot?.ProfileName is { } p ? "Bağlantı profili: " + p : string.Empty;
    public string WifiText
    {
        get
        {
            var w = _state.Snapshot?.Wifi;
            if (w is null) return string.Empty;
            if (w.Error is not null) return w.Error;
            var parts = new List<string>();
            if (w.Ssid is not null) parts.Add("Ağ: " + w.Ssid);
            if (w.SignalPercent is not null) parts.Add("Sinyal: " + w.SignalText);
            if (w.PhyType is not null) parts.Add(w.PhyType);
            if (w.RxMbps is not null) parts.Add($"Alma {w.RxMbps} Mbps / Gönderme {w.TxMbps} Mbps");
            return string.Join(" · ", parts);
        }
    }
    public bool HasWifi => !string.IsNullOrEmpty(WifiText);
    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }

    /// <summary>Bağlı olmayan sanal / kapalı bağdaştırıcıları da göster.</summary>
    public bool ShowInactive { get => _showInactive; set { if (Set(ref _showInactive, value)) ApplyAdapters(); } }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        StatusText = "Ağ bağdaştırıcıları okunuyor…";
        var snap = await Task.Run(() => _service.ReadAsync(ct), ct);
        _state.Snapshot = snap;
        if (snap.Error is not null) ErrorText = "Ağ bilgisi alınamadı. " + snap.Error;
        ApplyAdapters();
        OnPropertyChanged(nameof(ConnectivityText));
        OnPropertyChanged(nameof(ProfileText));
        OnPropertyChanged(nameof(WifiText));
        OnPropertyChanged(nameof(HasWifi));
        StatusText = snap.Primary is null ? "Etkin ağ bağlantısı yok" : $"Birincil bağlantı: {snap.Primary.Name} ({snap.Primary.TypeText}, {snap.Primary.SpeedText})";
        if (_state.Tests is { } previous) ShowTests(previous);
    }

    private void ApplyAdapters()
    {
        Adapters.Clear();
        foreach (var a in _state.Snapshot?.Adapters ?? [])
            if (_showInactive || a.IsUp) Adapters.Add(a);
    }

    private async Task RunTestsAsync(CancellationToken ct)
    {
        var sw = Start();
        if (_state.Snapshot is null) await LoadAsync(ct);
        var progress = new Progress<string>(t => TestStatus = t);
        var tests = await Task.Run(() => _service.RunTestsAsync(_state.Snapshot!, progress, ct), ct);
        _state.Tests = tests;
        ShowTests(tests);
        var worst = CheckStates.Worst(tests.Select(t => t.State));
        TestStatus = $"{tests.Count} test tamamlandı · {sw.Elapsed.TotalSeconds:0.0} sn · {DateTime.Now:HH:mm:ss}";
        var summary = worst == CheckState.Healthy ? "Bağlı · " + (tests.FirstOrDefault(t => t.LatencyMs is not null && t.Title.StartsWith("İnternet", StringComparison.Ordinal))?.Summary ?? "")
            : string.Join(" · ", tests.Where(t => t.State is CheckState.Warning or CheckState.Error).Select(t => $"{t.Title}: {t.Summary}"));
        Host.ReportDiagnostic("network", new CheckResult("Ağ", worst, summary, null, Nav.SpeedTest, Nav.Network));
        Host.RecordToolOperation("Ağ bağlantı testi", worst, summary, sw.Elapsed,
            worst == CheckState.Error ? string.Join("; ", tests.Where(t => t.State == CheckState.Error).Select(t => $"{t.Title}: {t.Detail ?? t.Summary}")) : null);
    }

    private void ShowTests(IReadOnlyList<NetTestResult> tests)
    {
        Tests.Clear();
        foreach (var t in tests) Tests.Add(new CheckRowViewModel(new CheckResult(t.Title, t.State, t.Summary, t.Detail)));
    }
}

// ====================================================================== DNS Tanılama

public sealed class DnsViewModel : ToolViewModel
{
    private readonly DnsDiagnosticsService _service;
    private readonly NetworkDiagnosticsService _network;
    private readonly NetworkState _state;
    private string _overall = "DNS testi henüz çalıştırılmadı.";

    public DnsViewModel(IToolHost host, NetworkState state) : base(host)
    {
        _service = new DnsDiagnosticsService(host.Logger);
        _network = new NetworkDiagnosticsService(host.Logger);
        _state = state;
        StatusText = "Test, yapılandırılmış her DNS sunucusuna doğrudan sorgu gönderir (sunucu başına 6 sorgu).";
    }

    public ObservableCollection<DnsServerResult> Servers { get; } = [];
    public string TestDomains => string.Join(", ", DnsDiagnosticsService.TestDomains);
    public string OverallText { get => _overall; private set => Set(ref _overall, value); }

    /// <summary>DNS testi ağ trafiği oluşturur: yalnızca kullanıcı başlatınca çalışır. Önceki sonuç varsa gösterilir.</summary>
    protected override bool AutoLoad => false;

    public override void Activate()
    {
        if (Servers.Count == 0 && _state.Dns is { } d) Show(d);
    }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var sw = Start();
        StatusText = "Ağ bağdaştırıcıları okunuyor…";
        var snap = await Task.Run(() => _network.ReadAsync(ct), ct);
        _state.Snapshot = snap;
        var progress = new Progress<string>(t => StatusText = t);
        var report = await Task.Run(() => _service.RunAsync(snap.Adapters, progress, ct), ct);
        _state.Dns = report;
        Show(report);
        StatusText = $"Test tamamlandı · {sw.Elapsed.TotalSeconds:0.0} sn · {DateTime.Now:HH:mm:ss}";
        var tested = report.Servers.Where(s => !s.Placeholder).ToList();
        var summary = report.Error ?? string.Join(" · ", tested.Select(s => $"{s.ServerText}: {s.ResultText}, {s.TimeText}"));
        Host.ReportDiagnostic("dns", new CheckResult("DNS", report.Error is not null ? CheckState.Skipped : report.Overall, summary, null, Nav.SpeedTest, Nav.Dns));
        Host.RecordToolOperation("DNS testi", report.Error is not null ? CheckState.Skipped : report.Overall, summary, sw.Elapsed,
            report.Overall == CheckState.Error ? string.Join("; ", tested.SelectMany(s => s.Failures)) : report.Error);
    }

    private void Show(DnsReport report)
    {
        Servers.Clear();
        foreach (var s in report.Servers) Servers.Add(s);
        if (report.Error is not null) ErrorText = "DNS bilgisi alınamadı. " + report.Error;
        var tested = report.Servers.Where(s => !s.Placeholder).ToList();
        OverallText = report.Error ?? (tested.Count == 0 ? "Test edilebilir DNS sunucusu yok"
            : $"{tested.Count(s => s.State == CheckState.Healthy)} / {tested.Count} sunucu sağlıklı · IPv4: {tested.Count(s => s.Family == "IPv4")}, IPv6: {tested.Count(s => s.Family == "IPv6")}");
    }
}

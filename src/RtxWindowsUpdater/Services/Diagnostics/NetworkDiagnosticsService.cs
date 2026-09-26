using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Bir ağ bağdaştırıcısının Windows'un bildirdiği yapılandırması (System.Net.NetworkInformation).</summary>
public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    long? SpeedBitsPerSecond,
    string? Mac,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> DnsServers,
    bool? DhcpEnabled,
    IReadOnlyList<string> DhcpServers,
    bool IsPrimary)
{
    public bool IsUp => Status == OperationalStatus.Up;
    public bool IsWireless => Type == NetworkInterfaceType.Wireless80211;
    public string TypeText => Type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP / çevirmeli",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobil geniş bant",
        _ => Type.ToString()
    };
    public string StatusText => Status switch
    {
        OperationalStatus.Up => "Bağlı",
        OperationalStatus.Down => "Bağlı değil",
        OperationalStatus.Dormant => "Beklemede",
        OperationalStatus.NotPresent => "Aygıt yok",
        OperationalStatus.LowerLayerDown => "Alt katman bağlı değil",
        _ => Status.ToString()
    };
    public string SpeedText => SpeedBitsPerSecond is > 0 and var s ? s >= 1_000_000_000 ? $"{s / 1e9:0.#} Gbps" : $"{s / 1e6:0} Mbps" : "—";
    public string IPv4Text => IPv4.Count == 0 ? "—" : string.Join(", ", IPv4);
    public string IPv6Text => IPv6.Count == 0 ? "—" : string.Join(", ", IPv6);
    public string GatewayText => Gateways.Count == 0 ? "—" : string.Join(", ", Gateways);
    public string DnsText => DnsServers.Count == 0 ? "—" : string.Join(", ", DnsServers);
    public string DhcpText => DhcpEnabled switch
    {
        true => DhcpServers.Count > 0 ? $"Açık ({string.Join(", ", DhcpServers)})" : "Açık",
        false => "Kapalı (elle yapılandırılmış)",
        _ => "—"
    };
    public string MacText => Mac ?? "—";
}

/// <summary>Wi-Fi bağlantı ayrıntısı (Windows WLAN API). Okunamazsa Error dolu (ör. konum izni).</summary>
public sealed record WifiInfo(string? Ssid, int? SignalPercent, string? PhyType, int? RxMbps, int? TxMbps, string? Error)
{
    public string SignalText => SignalPercent is { } s ? $"%{s}" : "—";
}

/// <summary>Tek ağ testinin gerçek ölçümü.</summary>
public sealed record NetTestResult(string Title, CheckState State, string Summary, string? Detail, double? LatencyMs, double? LossPercent)
{
    public string StateText => CheckStates.Text(State);
}

/// <summary>ProfileName: Windows bağlantı profilinin adı (Wi-Fi'de genellikle ağ adı) – yalnızca ekranda gösterilir, rapora yazılmaz.</summary>
public sealed record NetworkSnapshot(IReadOnlyList<AdapterInfo> Adapters, WifiInfo? Wifi, string? Error, string? ConnectivityText, string? ProfileName = null)
{
    public AdapterInfo? Primary => Adapters.FirstOrDefault(a => a.IsPrimary);
}

/// <summary>
/// Ağ merkezi: bağdaştırıcılar (Ethernet / Wi-Fi) ve gerçek bağlantı testleri – ağ geçidi ICMP, internet ICMP (1.1.1.1, paket kaybı +
/// gecikme), Windows'un internet erişimi değerlendirmesi (NCSI), sistem çözümleyicisiyle DNS ve HTTPS bağlantısı. Hız testinden bağımsızdır.
/// </summary>
public sealed class NetworkDiagnosticsService(Logger logger)
{
    private static readonly IPAddress InternetProbe = IPAddress.Parse("1.1.1.1");
    private static readonly string[] ResolveHosts = ["www.microsoft.com", "www.cloudflare.com", "www.google.com"];
    private static readonly Uri[] HttpsTargets = [new("https://www.microsoft.com/"), new("https://speed.cloudflare.com/")];

    public Task<NetworkSnapshot> ReadAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            var adapters = ReadAdapters();
            WifiInfo? wifi = adapters.Any(a => a.IsWireless && a.IsUp) ? WlanApi.ReadCurrent() : null;
            var (connectivity, profile) = ReadConnectivity();
            return new NetworkSnapshot(adapters, wifi, null, connectivity, profile);
        }
        catch (NetworkInformationException ex)
        {
            return new NetworkSnapshot([], null, "Ağ bağdaştırıcıları okunamadı: " + ex.Message, null);
        }
    }, ct);

    internal static List<AdapterInfo> ReadAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties? props = null;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { /* ayrıntı okunamadı; bağdaştırıcı yine listelenir */ }

            var v4 = new List<string>();
            var v6 = new List<string>();
            foreach (var u in props?.UnicastAddresses ?? Enumerable.Empty<UnicastIPAddressInformation>())
            {
                if (u.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add($"{u.Address}/{u.PrefixLength}");
                else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    v6.Add(u.Address.IsIPv6LinkLocal ? $"{Strip(u.Address)} (bağlantı yerel)" : u.Address.ToString());
            }
            var gateways = props?.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToList() ?? [];
            var dns = props?.DnsAddresses.ToList() ?? [];
            bool? dhcp = null;
            try { dhcp = props?.GetIPv4Properties()?.IsDhcpEnabled; }
            catch (NetworkInformationException) { }
            var dhcpServers = props?.DhcpServerAddresses.Select(a => a.ToString()).ToList() ?? [];
            string? mac = null;
            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length == 6) mac = string.Join("-", bytes.Select(b => b.ToString("X2")));
            long? speed = null;
            try { speed = nic.Speed > 0 ? nic.Speed : null; }
            catch (PlatformNotSupportedException) { }
            list.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus, speed, mac,
                v4, v6, gateways, dns, dhcp, dhcpServers, false));
        }
        // Birincil: bağlı + ağ geçidi olan, IPv4 ağ geçidi olan önce (Windows'un varsayılan yolu çoğunlukla budur).
        var primary = list.Where(a => a.IsUp && a.Gateways.Count > 0)
            .OrderByDescending(a => a.Gateways.Any(g => g.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(a => a.SpeedBitsPerSecond ?? 0).FirstOrDefault();
        return list.Select(a => a == primary ? a with { IsPrimary = true } : a)
            .OrderByDescending(a => a.IsPrimary).ThenByDescending(a => a.IsUp).ThenBy(a => a.Name).ToList();

        static string Strip(IPAddress a) => a.ToString().Split('%')[0];
    }

    /// <summary>Windows'un internet erişimi değerlendirmesi (NCSI; WinRT NetworkInformation).</summary>
    private static (string Text, string? Profile) ReadConnectivity()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return ("Windows: etkin internet bağlantısı yok", null);
            var text = profile.GetNetworkConnectivityLevel() switch
            {
                Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess => "Windows: internet erişimi var",
                Windows.Networking.Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess => "Windows: kısıtlı internet erişimi – oturum açma sayfası olabilir",
                Windows.Networking.Connectivity.NetworkConnectivityLevel.LocalAccess => "Windows: yalnızca yerel ağ",
                _ => "Windows: bağlantı yok"
            };
            return (text, profile.ProfileName);
        }
        catch (Exception ex)
        {
            return ("Windows bağlantı durumu okunamadı: " + ex.Message, null);
        }
    }

    /// <summary>
    /// Bağlantı testleri (her biri gerçek ölçüm). <paramref name="progress"/>: şu an çalışan testin adı (yüzde yok).
    /// Ağ yoksa ilgili testler "Atlandı" + neden döner; sonuçlar uydurulmaz.
    /// </summary>
    public async Task<IReadOnlyList<NetTestResult>> RunTestsAsync(NetworkSnapshot snapshot, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<NetTestResult>();
        var primary = snapshot.Primary;
        var connected = primary is not null;

        // 1) Ağ geçidi
        progress?.Report("Ağ geçidi test ediliyor…");
        var gw = primary?.Gateways.FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork) ?? primary?.Gateways.FirstOrDefault();
        if (gw is null)
            results.Add(new NetTestResult("Ağ geçidi (yönlendirici)", CheckState.Skipped,
                connected ? "Varsayılan ağ geçidi yok" : "Etkin ağ bağlantısı yok", null, null, null));
        else
            results.Add(Summarize("Ağ geçidi (yönlendirici)", $"{gw}", await IcmpProbe.RunAsync(gw, 10, TimeSpan.FromMilliseconds(100), 1000, ct),
                warnMs: 20, local: true));

        // 2) İnternet (ICMP 1.1.1.1) – gecikme + paket kaybı
        progress?.Report("İnternet bağlantısı test ediliyor…");
        var inet = await IcmpProbe.RunAsync(InternetProbe, 20, TimeSpan.FromMilliseconds(100), 1500, ct);
        results.Add(Summarize("İnternet gecikmesi ve paket kaybı", "1.1.1.1 (Cloudflare)", inet, warnMs: 100, local: false));

        // 3) Windows internet değerlendirmesi
        var conn = snapshot.ConnectivityText;
        results.Add(new NetTestResult("Windows internet erişimi", conn switch
        {
            null => CheckState.Unknown,
            _ when conn.Contains("internet erişimi var", StringComparison.Ordinal) => CheckState.Healthy,
            _ when conn.Contains("okunamadı", StringComparison.Ordinal) => CheckState.Unknown,
            _ when conn.Contains("kısıtlı", StringComparison.Ordinal) || conn.Contains("yerel", StringComparison.Ordinal) => CheckState.Warning,
            _ => CheckState.Error
        }, conn ?? "Okunamadı", "Windows Ağ Bağlantısı Durum Göstergesi (NCSI) sonucu", null, null));

        // 4) DNS çözümleme (sistem çözümleyicisi)
        progress?.Report("DNS çözümleme test ediliyor…");
        results.Add(await ResolveTestAsync(ct));

        // 5) HTTPS
        progress?.Report("HTTPS bağlantısı test ediliyor…");
        results.Add(await HttpsTestAsync(ct));

        logger.Info("Ağ testleri: " + string.Join(" | ", results.Select(r => $"{r.Title}: {r.StateText} – {r.Summary}")));
        return results;
    }

    private static NetTestResult Summarize(string title, string target, IcmpResult r, double warnMs, bool local)
    {
        if (r.Received == 0)
            return new NetTestResult(title, local ? CheckState.Warning : CheckState.Error,
                r.Error is not null ? "ICMP gönderilemedi" : "Yanıt yok",
                r.Error ?? $"{target}: {r.Sent} isteğin hiçbirine yanıt gelmedi (ICMP engelleniyor olabilir ya da bağlantı yok); kayıp oranı hesaplanmadı.",
                null, null);
        var median = SpeedTestMath.Median(r.RttsMs);
        var loss = r.LossPercent ?? 0;
        var state = loss >= 10 ? CheckState.Error : loss > 0 || median > warnMs ? CheckState.Warning : CheckState.Healthy;
        return new NetTestResult(title, state, $"{median:0} ms · %{loss:0.#} kayıp",
            $"{target}: {r.Received}/{r.Sent} yanıt, en düşük {r.RttsMs.Min():0} ms, en yüksek {r.RttsMs.Max():0} ms", median, loss);
    }

    private static async Task<NetTestResult> ResolveTestAsync(CancellationToken ct)
    {
        var times = new List<double>();
        var failures = new List<string>();
        foreach (var host in ResolveHosts)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, timeout.Token);
                if (addrs.Length == 0) failures.Add($"{host}: adres dönmedi");
                else times.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failures.Add($"{host}: 5 sn içinde çözümlenemedi");
            }
            catch (SocketException ex)
            {
                failures.Add($"{host}: {ex.Message}");
            }
        }
        if (times.Count == 0)
            return new NetTestResult("DNS çözümleme", CheckState.Error, "Alan adları çözümlenemedi", string.Join("\n", failures), null, null);
        var median = SpeedTestMath.Median(times);
        return new NetTestResult("DNS çözümleme", failures.Count > 0 ? CheckState.Warning : CheckState.Healthy,
            $"{times.Count}/{ResolveHosts.Length} başarılı · {median:0} ms",
            (failures.Count > 0 ? string.Join("\n", failures) + "\n" : "") +
            "Windows çözümleyicisi kullanıldı (önbellekteki adlar daha hızlı döner). Sunucu bazında ölçüm: DNS Tanılama.", median, null);
    }

    private static async Task<NetTestResult> HttpsTestAsync(CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { UseProxy = true, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"E-mreControlCenter/{AppInfo.Version}");
        var ok = new List<string>();
        var failed = new List<string>();
        var times = new List<double>();
        foreach (var uri in HttpsTargets)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, uri);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                times.Add(sw.Elapsed.TotalMilliseconds);
                ok.Add($"{uri.Host}: HTTP {(int)resp.StatusCode} · {sw.Elapsed.TotalMilliseconds:0} ms");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failed.Add($"{uri.Host}: zaman aşımı");
            }
            catch (HttpRequestException ex)
            {
                failed.Add($"{uri.Host}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        if (ok.Count == 0)
            return new NetTestResult("HTTPS bağlantısı", CheckState.Error, "Güvenli bağlantı kurulamadı", string.Join("\n", failed), null, null);
        return new NetTestResult("HTTPS bağlantısı", failed.Count > 0 ? CheckState.Warning : CheckState.Healthy,
            $"{ok.Count}/{HttpsTargets.Length} sunucu yanıt verdi · {SpeedTestMath.Median(times):0} ms",
            string.Join("\n", ok.Concat(failed)), SpeedTestMath.Median(times), null);
    }
}

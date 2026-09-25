using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services;

public enum SpeedTestPhase
{
    Idle,
    Connecting,
    Latency,
    Download,
    Upload,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Ölçüm ayarları. Varsayılanlar uygulamanın kullandığı değerlerdir; testler kısa süre / farklı adres verebilir.</summary>
public sealed record SpeedTestOptions
{
    public Uri BaseUri { get; init; } = new("https://speed.cloudflare.com/");
    public int Streams { get; init; } = SpeedTestService.MultiStreams;
    public TimeSpan TransferDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(2);
    public int IdleLatencySamples { get; init; } = 20;
    public int PacketLossProbes { get; init; } = 50;
    public long DownloadChunkBytes { get; init; } = 25_000_000; // sunucu 100 MB ve üstünü reddediyor (HTTP 403)
    public long UploadChunkBytes { get; init; } = 25_000_000;
}

/// <summary>Sunucu ve bağlantı bilgisi (Cloudflare /meta yanıtından).</summary>
/// <summary>Ölçüm altyapıları (sunucu ağı).</summary>
public static class SpeedTestProviders
{
    public const string Cloudflare = "Cloudflare";
    public const string Ookla = "Speedtest by Ookla";
}

/// <summary>Sunucu ve bağlantı bilgisi (Cloudflare /meta yanıtından veya Ookla aracının sonucundan).</summary>
public sealed record SpeedTestServer
{
    public string Provider { get; init; } = SpeedTestProviders.Cloudflare;

    /// <summary>Sunucuyu barındıran kuruluş (Ookla: "Turkcell"; Cloudflare: "Cloudflare").</summary>
    public string Sponsor { get; init; } = "Cloudflare";

    /// <summary>Ookla sunucu kimliği (Cloudflare'de yok).</summary>
    public int? ServerId { get; init; }
    public string Isp { get; init; } = string.Empty;
    public long? Asn { get; init; }
    public string ClientIp { get; init; } = string.Empty;
    public string ClientCity { get; init; } = string.Empty;
    public string ClientCountry { get; init; } = string.Empty;
    public string ServerCode { get; init; } = string.Empty;
    public string ServerCity { get; init; } = string.Empty;
    public string ServerCountry { get; init; } = string.Empty;
    public string HttpProtocol { get; init; } = string.Empty;
    public string ServerAddress { get; init; } = string.Empty;

    /// <summary>"Cloudflare · Amsterdam, NL (AMS)" / "Turkcell · Beyoglu, Turkey"</summary>
    public string ServerName =>
        $"{Sponsor} · " + (ServerCity.Length > 0 ? $"{ServerCity}{(ServerCountry.Length > 0 ? ", " + ServerCountry : "")}" : "bilinmeyen konum") +
        (ServerCode.Length > 0 ? $" ({ServerCode})" : "");

    /// <summary>"Cloudflare (anycast)" / "Speedtest by Ookla · sunucu 73840"</summary>
    public string ProviderText => Provider == SpeedTestProviders.Ookla
        ? $"{SpeedTestProviders.Ookla}{(ServerId is { } id ? $" · sunucu {id}" : "")}"
        : "Cloudflare (anycast)";

    public string IspName => Isp.Length == 0 ? "Bilgi alınamadı" : Asn is { } a ? $"{Isp} (AS{a})" : Isp;

    public string ClientLocation =>
        ClientCity.Length > 0 ? $"{ClientCity}{(ClientCountry.Length > 0 ? ", " + ClientCountry : "")}" : ClientCountry;
}

/// <summary>Gecikme ölçümü (ms). Yalnızca başarılı ölçümlerden hesaplanır.</summary>
public sealed record LatencyStats(double MedianMs, double MinMs, double JitterMs, int Samples, int Failed);

/// <summary>Aktarım ölçümü. <see cref="Mbps"/> yalnızca ısınma sonrası penceredeki gerçek baytlardan hesaplanır.</summary>
public sealed record TransferStats(double Mbps, long MeasuredBytes, double MeasuredSeconds, long TotalBytes, int Streams, int FailedStreams);

/// <summary>
/// Paket kaybı. Cloudflare: ICMP yankı isteği (hiç yanıt yoksa oran hesaplanmaz – ICMP engellenmiş olabilir).
/// Ookla: aracın bildirdiği oran (<see cref="ReportedPercent"/>; gönderilen / alınan sayısı bildirilmez).
/// </summary>
public sealed record PacketLossStats(int Sent, int Received, double? MedianRttMs)
{
    public double? ReportedPercent { get; init; }
    public double? LossPercent => ReportedPercent ?? (Received == 0 ? null : (Sent - Received) * 100.0 / Sent);
}

public sealed class SpeedTestResult
{
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public TimeSpan Duration { get; set; }
    public string Provider { get; init; } = SpeedTestProviders.Cloudflare;
    public bool MultipleConnections { get; init; }

    /// <summary>Ookla'nın sonuç sayfası (https://www.speedtest.net/result/...); Cloudflare'de yok.</summary>
    public string? ResultUrl { get; set; }
    public SpeedTestPhase Phase { get; set; } = SpeedTestPhase.Idle;
    public SpeedTestServer? Server { get; set; }
    public LatencyStats? IdleLatency { get; set; }
    public LatencyStats? DownloadLatency { get; set; }
    public LatencyStats? UploadLatency { get; set; }
    public TransferStats? Download { get; set; }
    public TransferStats? Upload { get; set; }
    public PacketLossStats? PacketLoss { get; set; }
    public string? PacketLossNote { get; set; }
    public string? Error { get; set; }
    public List<string> Notes { get; } = [];

    public long DataUsedBytes => (Download?.TotalBytes ?? 0) + (Upload?.TotalBytes ?? 0);

    /// <summary>İki aktarım da ölçüldüyse tamamlandı; biri ölçülemediyse kısmen başarısız.</summary>
    public bool IsComplete => Phase == SpeedTestPhase.Completed && Download is not null && Upload is not null && IdleLatency is not null;
}

/// <summary>
/// Canlı ilerleme: aşama, aşamadaki gerçek geçen süre oranı ve son 1 saniyenin gerçek hızı. Bir aşama bitince sonraki aşamanın
/// ilk bildirimi biten aşamaların sonuçlarını da taşır (ekran, test bitmeden ping / indirme sonucunu gösterir).
/// </summary>
public sealed record SpeedTestProgress(SpeedTestPhase Phase, double PhaseFraction, double? CurrentMbps, double? LatencyMs, SpeedTestServer? Server)
{
    public LatencyStats? IdleLatency { get; init; }
    public PacketLossStats? PacketLoss { get; init; }
    public string? PacketLossNote { get; init; }
    public TransferStats? Download { get; init; }
    public LatencyStats? DownloadLatency { get; init; }
}

/// <summary>Ölçüm hesapları (saf fonksiyonlar; servis testleriyle doğrulanır).</summary>
public static class SpeedTestMath
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("Ölçüm yok.", nameof(values));
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Titreşim: ardışık ölçümler arasındaki mutlak farkların ortalaması (tek ölçümde 0).</summary>
    public static double Jitter(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        for (var i = 1; i < values.Count; i++) sum += Math.Abs(values[i] - values[i - 1]);
        return sum / (values.Count - 1);
    }

    public static LatencyStats? Latency(IReadOnlyList<double> samples, int failed) =>
        samples.Count == 0 ? null : new LatencyStats(Median(samples), samples.Min(), Jitter(samples), samples.Count, failed);

    /// <summary>
    /// Isınma sonrası hız (Mbps): ısınma anına en yakın örnekten son örneğe kadar aktarılan bayt / geçen süre.
    /// Örnekler (saniye, toplam bayt) artan sıradadır. Ölçüm penceresi yoksa null.
    /// </summary>
    public static (double Mbps, long Bytes, double Seconds)? SteadyRate(IReadOnlyList<(double Seconds, long Bytes)> samples, double warmUpSeconds)
    {
        if (samples.Count < 2) return null;
        var end = samples[^1];
        var start = samples.FirstOrDefault(s => s.Seconds >= warmUpSeconds);
        if (start.Seconds <= 0 || end.Seconds - start.Seconds < 0.5) start = samples[0];
        var seconds = end.Seconds - start.Seconds;
        var bytes = end.Bytes - start.Bytes;
        if (seconds <= 0 || bytes <= 0) return null;
        return (bytes * 8 / seconds / 1_000_000, bytes, seconds);
    }

    /// <summary>Son <paramref name="window"/> saniyedeki hız (canlı gösterge için).</summary>
    public static double? WindowRate(IReadOnlyList<(double Seconds, long Bytes)> samples, double window)
    {
        if (samples.Count < 2) return null;
        var end = samples[^1];
        var start = samples[0];
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            start = samples[i];
            if (end.Seconds - samples[i].Seconds >= window) break;
        }
        var seconds = end.Seconds - start.Seconds;
        return seconds <= 0 ? null : (end.Bytes - start.Bytes) * 8 / seconds / 1_000_000;
    }
}

/// <summary>
/// İnternet hız testi: Cloudflare'in herkese açık hız testi altyapısı (speed.cloudflare.com; anycast, en yakın veri merkezi
/// otomatik seçilir). Tüm değerler gerçek ölçümdür:
/// gecikme = TCP bağlantı süresi (SYN → SYN-ACK, 443), paket kaybı = ICMP yankı isteği, hız = ısınma sonrası aktarılan gerçek bayt / süre.
/// Ölçülemeyen değer uydurulmaz; nedeni sonuçta ve günlükte yazılır.
/// </summary>
public sealed class SpeedTestService(Logger logger, SpeedTestOptions? options = null)
{
    public const int MultiStreams = 6;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoadedPingInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    private readonly SpeedTestOptions _options = options ?? new SpeedTestOptions();

    public SpeedTestOptions Options => _options;

    public async Task<SpeedTestResult> RunAsync(bool multipleConnections, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        var streams = multipleConnections ? Math.Max(1, _options.Streams) : 1;
        var result = new SpeedTestResult { MultipleConnections = multipleConnections };
        var total = Stopwatch.StartNew();
        logger.Info($"Hız testi başladı: {_options.BaseUri.Host}, {(multipleConnections ? $"çoklu bağlantı ({streams})" : "tek bağlantı")}, " +
                    $"aşama süresi {_options.TransferDuration.TotalSeconds:0} sn (ilk {_options.WarmUp.TotalSeconds:0} sn hesaba katılmaz).");

        try
        {
            // ---- 1) Sunucu ve bağlantı bilgisi
            result.Phase = SpeedTestPhase.Connecting;
            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Connecting, 0, null, null, null));
            var address = await ResolveAsync(ct);
            using var http = CreateClient(address);
            result.Server = await ReadServerAsync(http, address, ct);
            logger.Info($"Hız testi sunucusu: {result.Server.ServerName} · adres {address} · ISS {result.Server.IspName} · " +
                        $"konum {result.Server.ClientLocation} · {result.Server.HttpProtocol}");
            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Connecting, 1, null, null, result.Server));

            // ---- 2) Boşta gecikme (TCP) + paket kaybı (ICMP), eş zamanlı
            result.Phase = SpeedTestPhase.Latency;
            var lossTask = MeasurePacketLossAsync(address, ct);
            var idle = new List<double>();
            var idleFailed = 0;
            string? latencyError = null;
            for (var i = 0; i < _options.IdleLatencySamples; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (ms, error) = await TcpPingAsync(address, ct);
                if (ms is { } v) idle.Add(v);
                else { idleFailed++; latencyError ??= error; }
                progress?.Report(new SpeedTestProgress(SpeedTestPhase.Latency, (i + 1.0) / _options.IdleLatencySamples, null,
                    idle.Count > 0 ? SpeedTestMath.Median(idle) : null, result.Server));
                await Task.Delay(100, ct);
            }
            result.IdleLatency = SpeedTestMath.Latency(idle, idleFailed);
            if (result.IdleLatency is null) result.Notes.Add("Gecikme ölçülemedi: " + (latencyError ?? "yanıt yok"));
            (result.PacketLoss, result.PacketLossNote) = await lossTask;
            logger.Info(result.IdleLatency is { } il
                ? $"Hız testi gecikme (TCP, boşta): medyan {il.MedianMs:0.0} ms, en düşük {il.MinMs:0.0} ms, titreşim {il.JitterMs:0.0} ms ({il.Samples} ölçüm, {il.Failed} başarısız)"
                : "Hız testi gecikme ölçülemedi: " + (latencyError ?? "yanıt yok"));
            logger.Info(result.PacketLoss is { } pl && pl.LossPercent is { } lp
                ? $"Hız testi paket kaybı (ICMP): %{lp:0.#} ({pl.Sent} gönderildi, {pl.Received} yanıt)"
                : "Hız testi paket kaybı ölçülemedi: " + result.PacketLossNote);

            // ---- 3) İndirme, 4) Yükleme (her biri sırasında yük altında gecikme)
            result.Phase = SpeedTestPhase.Download;
            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Download, 0, null, null, result.Server)
            {
                IdleLatency = result.IdleLatency, PacketLoss = result.PacketLoss, PacketLossNote = result.PacketLossNote
            });
            (result.Download, result.DownloadLatency, var downloadError) =
                await MeasureTransferAsync(http, address, SpeedTestPhase.Download, streams, result.Server, progress, ct);
            if (downloadError is not null) result.Notes.Add("İndirme: " + downloadError);

            result.Phase = SpeedTestPhase.Upload;
            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Upload, 0, null, null, result.Server)
            {
                IdleLatency = result.IdleLatency, PacketLoss = result.PacketLoss, PacketLossNote = result.PacketLossNote,
                Download = result.Download, DownloadLatency = result.DownloadLatency
            });
            (result.Upload, result.UploadLatency, var uploadError) =
                await MeasureTransferAsync(http, address, SpeedTestPhase.Upload, streams, result.Server, progress, ct);
            if (uploadError is not null) result.Notes.Add("Yükleme: " + uploadError);

            result.Phase = result.Download is null && result.Upload is null ? SpeedTestPhase.Failed : SpeedTestPhase.Completed;
            if (result.Phase == SpeedTestPhase.Failed)
                result.Error = downloadError ?? uploadError ?? "Veri aktarılamadı.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Phase = SpeedTestPhase.Cancelled;
            result.Error = "Test iptal edildi.";
        }
        catch (Exception ex)
        {
            result.Phase = SpeedTestPhase.Failed;
            result.Error = Describe(ex);
        }
        finally
        {
            result.Duration = total.Elapsed;
        }

        var summary = result.Phase switch
        {
            SpeedTestPhase.Completed =>
                $"Hız testi tamamlandı ({result.Duration.TotalSeconds:0} sn): indirme {Mbps(result.Download)}, yükleme {Mbps(result.Upload)}, " +
                $"ping {(result.IdleLatency is { } l ? $"{l.MedianMs:0} ms" : "ölçülemedi")}, kullanılan veri {result.DataUsedBytes / 1_000_000.0:0.0} MB" +
                (result.Notes.Count > 0 ? " · " + string.Join(" · ", result.Notes) : ""),
            SpeedTestPhase.Cancelled => $"Hız testi iptal edildi ({result.Duration.TotalSeconds:0} sn).",
            _ => "Hız testi başarısız: " + result.Error
        };
        if (result.Phase == SpeedTestPhase.Completed && result.IsComplete) logger.Success(summary);
        else if (result.Phase == SpeedTestPhase.Completed) logger.Warning(summary);
        else if (result.Phase == SpeedTestPhase.Cancelled) logger.Warning(summary);
        else logger.Error(summary);
        return result;
    }

    private static string Mbps(TransferStats? t) => t is null ? "ölçülemedi" : $"{t.Mbps:0.00} Mbps";

    /// <summary>
    /// HTTP/1.1 istemcisi: her paralel istek kendi TCP bağlantısını kullanır ("çoklu bağlantı" gerçekten çoklu bağlantıdır).
    /// Bağlantılar gecikme ve ICMP ölçümüyle AYNI adrese kurulur (TLS için ana bilgisayar adı korunur); vekil sunucu kullanılmaz.
    /// </summary>
    private HttpClient CreateClient(IPAddress address)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            MaxConnectionsPerServer = 32,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = _options.BaseUri,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"E-mreControlCenter/{AppInfo.Version}");
        // /meta yanıtı yalnızca Referer başlığı varsa dolu döner; uygulama kendi adresini gönderir (başka siteyi taklit etmez).
        http.DefaultRequestHeaders.Referrer = new Uri("https://github.com/E-mre-Hub/E-mre-Control-Center");
        return http;
    }

    private async Task<IPAddress> ResolveAsync(CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(_options.BaseUri.Host, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Sunucu adresi çözümlenemedi ({_options.BaseUri.Host}): {ex.Message}", ex);
        }
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Sunucu adresi çözümlenemedi ({_options.BaseUri.Host}): adres dönmedi");
        // IPv4 tercih edilir (TCP gecikmesi ve ICMP aynı adrese ölçülür); yoksa IPv6.
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }

    private async Task<SpeedTestServer> ReadServerAsync(HttpClient http, IPAddress address, CancellationToken ct)
    {
        using var response = await http.GetAsync("meta", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Sunucu bilgisi alınamadı: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var colo = root.TryGetProperty("colo", out var c) ? c : default;
        var server = new SpeedTestServer
        {
            Provider = SpeedTestProviders.Cloudflare,
            Sponsor = "Cloudflare",
            Isp = Str(root, "asOrganization"),
            Asn = root.TryGetProperty("asn", out var asn) && asn.ValueKind == JsonValueKind.Number && asn.TryGetInt64(out var asnValue) ? asnValue : null,
            ClientIp = Str(root, "clientIp"),
            ClientCity = Str(root, "city"),
            ClientCountry = Str(root, "country"),
            HttpProtocol = Str(root, "httpProtocol"),
            ServerCode = colo.ValueKind == JsonValueKind.Object ? Str(colo, "iata") : colo.ValueKind == JsonValueKind.String ? colo.GetString() ?? "" : "",
            ServerCity = Str(colo, "city"),
            ServerCountry = Str(colo, "cca2"),
            ServerAddress = address.ToString()
        };
        if (server.ClientIp.Length == 0 && server.ServerCode.Length == 0)
            logger.Warning("Hız testi: sunucu bilgi yanıtı boş geldi (ISS / konum gösterilemeyecek).");
        return server;
    }

    /// <summary>TCP bağlantı süresi (SYN → SYN-ACK). Sunucu işlem süresi içermez; bağlantı hemen kapatılır.</summary>
    private async Task<(double? Ms, string? Error)> TcpPingAsync(IPAddress address, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, _options.BaseUri.Port), timeout.Token);
            return (sw.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"bağlantı {ConnectTimeout.TotalSeconds:0} sn içinde kurulamadı");
        }
        catch (SocketException ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(PacketLossStats?, string?)> MeasurePacketLossAsync(IPAddress address, CancellationToken ct)
    {
        var count = _options.PacketLossProbes;
        if (count <= 0) return (null, "ölçülmedi");
        var tasks = new List<Task<PingReply?>>();
        string? error = null;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            tasks.Add(SendPingAsync());
            await Task.Delay(60, ct);
        }
        var replies = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        var ok = replies.Where(r => r?.Status == IPStatus.Success).Select(r => (double)r!.RoundtripTime).ToList();
        if (error is not null && ok.Count == 0) return (null, "ICMP gönderilemedi: " + error);
        if (ok.Count == 0)
            return (new PacketLossStats(count, 0, null),
                "sunucu ICMP yankı isteklerine yanıt vermedi (ağ veya sunucu ICMP'yi engelliyor olabilir); kayıp oranı hesaplanmadı");
        return (new PacketLossStats(count, ok.Count, SpeedTestMath.Median(ok)), null);

        async Task<PingReply?> SendPingAsync()
        {
            try
            {
                using var ping = new Ping();
                return await ping.SendPingAsync(address, 1000);
            }
            catch (PingException ex)
            {
                error ??= ex.InnerException?.Message ?? ex.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// Bir aktarım aşaması: <paramref name="streams"/> paralel bağlantı, sabit süre. Her 100 ms'de toplam gerçek bayt örneklenir;
    /// sonuç ısınma sonrası penceredir. Aynı anda 0,5 sn'de bir TCP gecikmesi ölçülür (yük altında gecikme).
    /// </summary>
    private async Task<(TransferStats?, LatencyStats?, string?)> MeasureTransferAsync(
        HttpClient http, IPAddress address, SpeedTestPhase phase, int streams, SpeedTestServer? server,
        IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        var name = phase == SpeedTestPhase.Download ? "indirme" : "yükleme";
        long bytes = 0;
        var errors = new List<string>();
        var failedStreams = 0;
        var samples = new List<(double, long)> { (0, 0) };
        var loaded = new List<double>();
        var loadedFailed = 0;

        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        var block = phase == SpeedTestPhase.Upload ? RandomBlock() : null;

        async Task StreamAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!phaseCts.IsCancellationRequested)
                {
                    if (phase == SpeedTestPhase.Download)
                    {
                        using var response = await http.GetAsync($"__down?bytes={_options.DownloadChunkBytes}",
                            HttpCompletionOption.ResponseHeadersRead, phaseCts.Token);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        await using var stream = await response.Content.ReadAsStreamAsync(phaseCts.Token);
                        int n;
                        while ((n = await stream.ReadAsync(buffer, phaseCts.Token)) > 0)
                            Interlocked.Add(ref bytes, n);
                    }
                    else
                    {
                        using var content = new CountingUploadContent(_options.UploadChunkBytes, block!, n => Interlocked.Add(ref bytes, n));
                        using var response = await http.PostAsync("__up", content, phaseCts.Token);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                }
            }
            catch (Exception) when (phaseCts.IsCancellationRequested)
            {
                // aşama süresi doldu (veya kullanıcı iptal etti): normal bitiş
            }
            catch (Exception ex)
            {
                lock (errors) { errors.Add(Describe(ex)); failedStreams++; }
            }
        }

        async Task LoadedPingAsync()
        {
            try
            {
                while (!phaseCts.IsCancellationRequested)
                {
                    await Task.Delay(LoadedPingInterval, phaseCts.Token);
                    var (ms, _) = await TcpPingAsync(address, phaseCts.Token);
                    lock (loaded)
                    {
                        if (ms is { } v) loaded.Add(v);
                        else loadedFailed++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        var workers = Enumerable.Range(0, streams).Select(_ => Task.Run(StreamAsync)).ToList();
        var pinger = Task.Run(LoadedPingAsync);
        var duration = _options.TransferDuration.TotalSeconds;
        try
        {
            while (sw.Elapsed.TotalSeconds < duration)
            {
                await Task.Delay(SampleInterval, ct);
                var t = sw.Elapsed.TotalSeconds;
                samples.Add((t, Interlocked.Read(ref bytes)));
                progress?.Report(new SpeedTestProgress(phase, Math.Min(1, t / duration), SpeedTestMath.WindowRate(samples, 1.0), null, server));
                if (workers.All(w => w.IsCompleted)) break; // tüm akışlar hata verdi
            }
        }
        finally
        {
            phaseCts.Cancel();
            await Task.WhenAll(workers.Append(pinger));
        }
        ct.ThrowIfCancellationRequested();

        var total = Interlocked.Read(ref bytes);
        var rate = SpeedTestMath.SteadyRate(samples, _options.WarmUp.TotalSeconds);
        LatencyStats? latency;
        lock (loaded) latency = SpeedTestMath.Latency(loaded, loadedFailed);
        string? error = errors.Count > 0 ? $"{errors.Count} bağlantı hata verdi: {errors[0]}" : null;
        if (rate is null)
        {
            logger.Warning($"Hız testi {name} ölçülemedi: " + (error ?? "veri aktarılmadı"));
            return (null, latency, error ?? "veri aktarılmadı");
        }

        var stats = new TransferStats(rate.Value.Mbps, rate.Value.Bytes, rate.Value.Seconds, total, streams, failedStreams);
        logger.Info($"Hız testi {name}: {stats.Mbps:0.00} Mbps (ölçüm penceresi {stats.MeasuredSeconds:0.0} sn, {stats.MeasuredBytes / 1_000_000.0:0.0} MB; " +
                    $"toplam {total / 1_000_000.0:0.0} MB, {streams} bağlantı)" +
                    (latency is { } l ? $", yük altında gecikme {l.MedianMs:0.0} ms" : "") + (error is null ? "" : " · " + error));
        return (stats, latency, error);
    }

    private static byte[] RandomBlock()
    {
        // Rastgele veri: sıkıştırılabilir içerik ara cihazlarda sıkıştırılıp sonucu şişirebilirdi.
        var block = new byte[64 * 1024];
        RandomNumberGenerator.Fill(block);
        return block;
    }

    internal static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is HttpRequestException or AggregateException) inner = inner.InnerException;
        var text = ex is HttpRequestException && inner != ex ? $"{ex.Message} ({inner.Message})" : ex.Message;
        return ex switch
        {
            HttpRequestException when inner is SocketException se => $"Sunucuya bağlanılamadı: {se.Message}",
            _ => text
        };
    }

    /// <summary>Yükleme gövdesi: bloklar hâlinde yazılır, yazılan her blok sayılır (ilerleme = gerçekten gönderilen bayt).</summary>
    private sealed class CountingUploadContent : HttpContent
    {
        private readonly long _length;
        private readonly byte[] _block;
        private readonly Action<int> _written;

        public CountingUploadContent(long length, byte[] block, Action<int> written)
        {
            _length = length;
            _block = block;
            _written = written;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var remaining = _length;
            while (remaining > 0)
            {
                var n = (int)Math.Min(_block.Length, remaining);
                await stream.WriteAsync(_block.AsMemory(0, n), cancellationToken);
                _written(n);
                remaining -= n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}

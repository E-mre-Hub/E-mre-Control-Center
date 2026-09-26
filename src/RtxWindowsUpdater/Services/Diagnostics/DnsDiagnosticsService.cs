using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Tek DNS sorgusunun gerçek yanıtı (UDP 53). Rcode: 0 NOERROR, 2 SERVFAIL, 3 NXDOMAIN; Error: yanıt alınamadı.</summary>
public sealed record DnsReply(int? Rcode, int Answers, bool Authenticated, double? Ms, string? Error)
{
    public bool Ok => Error is null && Rcode == 0 && Answers > 0;
}

/// <summary>Bir DNS sunucusunun ölçümü.</summary>
public sealed record DnsServerResult(
    IPAddress Server,
    string Adapter,
    bool Placeholder,
    int Queried,
    int Succeeded,
    double? MedianMs,
    double? MaxMs,
    bool? ValidatesDnssec,
    string DnssecText,
    IReadOnlyList<string> Failures,
    CheckState State)
{
    public string Family => Server.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
    public string ServerText => Server.ToString().Split('%')[0];
    public string ResultText => Placeholder ? "Test edilmedi (yapılandırılmamış yer tutucu adres)"
        : Queried == 0 ? "—" : $"{Succeeded}/{Queried} başarılı";
    public string TimeText => MedianMs is { } m ? $"{m:0} ms" + (MaxMs is { } x && x > m + 1 ? $" (en yüksek {x:0} ms)" : "") : "—";
    public string FailuresText => Failures.Count == 0 ? "" : string.Join(" · ", Failures);
    public string StateText => CheckStates.Text(State);
}

public sealed record DnsReport(IReadOnlyList<DnsServerResult> Servers, string? Error)
{
    public CheckState Overall => Error is not null ? CheckState.Unknown : CheckStates.Worst(Servers.Where(s => !s.Placeholder).Select(s => s.State));
}

/// <summary>
/// DNS tanılama: etkin bağlantıların GERÇEK DNS sunucuları (Windows yapılandırması) tek tek UDP 53 üzerinden doğrudan sorgulanır –
/// yanıt süresi, çözümleme başarısı ve DNSSEC doğrulaması (EDNS DO bitiyle imzalı alan adı → AD bayrağı; bilerek bozuk imzalı
/// dnssec-failed.org → doğrulayan sunucu SERVFAIL döndürür). DNS ayarı DEĞİŞTİRİLMEZ.
/// </summary>
public sealed class DnsDiagnosticsService(Logger logger)
{
    internal static readonly string[] TestDomains = ["www.microsoft.com", "www.cloudflare.com", "www.google.com", "www.wikipedia.org"];
    private const string SignedDomain = "cloudflare.com";
    private const string BrokenSignatureDomain = "dnssec-failed.org";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Windows'un yapılandırılmamış IPv6 DNS için gösterdiği yer tutucular (fec0:0:0:ffff::1-3).</summary>
    internal static bool IsPlaceholder(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal &&
        a.ToString().StartsWith("fec0:0:0:ffff::", StringComparison.OrdinalIgnoreCase);

    public async Task<DnsReport> RunAsync(IReadOnlyList<AdapterInfo> adapters, IProgress<string>? progress, CancellationToken ct)
    {
        var servers = adapters.Where(a => a.IsUp)
            .SelectMany(a => a.DnsServers.Select(d => (Server: d, Adapter: a.Name)))
            .GroupBy(x => x.Server).Select(g => g.First()).ToList();
        if (servers.Count == 0)
            return new DnsReport([], adapters.Any(a => a.IsUp) ? "Etkin bağlantıda yapılandırılmış DNS sunucusu yok." : "Etkin ağ bağlantısı yok; DNS test edilemedi.");

        var results = new List<DnsServerResult>();
        foreach (var (server, adapter) in servers)
        {
            ct.ThrowIfCancellationRequested();
            if (IsPlaceholder(server))
            {
                results.Add(new DnsServerResult(server, adapter, true, 0, 0, null, null, null, "—", [], CheckState.Info));
                continue;
            }
            progress?.Report($"DNS sunucusu {server} sorgulanıyor…");
            results.Add(await TestServerAsync(server, adapter, ct));
        }
        logger.Info("DNS tanılama: " + string.Join(" | ", results.Select(r => $"{r.ServerText} ({r.Adapter}): {r.ResultText}, {r.TimeText}, DNSSEC {r.DnssecText}")));
        return new DnsReport(results, null);
    }

    private static async Task<DnsServerResult> TestServerAsync(IPAddress server, string adapter, CancellationToken ct)
    {
        var times = new List<double>();
        var failures = new List<string>();
        var succeeded = 0;
        foreach (var domain in TestDomains)
        {
            var reply = await QueryAsync(server, domain, 1, false, ct);
            if (reply.Ms is { } ms && reply.Error is null) times.Add(ms);
            if (reply.Ok) succeeded++;
            else failures.Add($"{domain}: {Describe(reply)}");
        }

        // DNSSEC: imzalı alan adı AD bayrağıyla dönmeli; bozuk imzalı alan adı SERVFAIL almalı.
        bool? validates = null;
        string dnssec;
        var signed = await QueryAsync(server, SignedDomain, 1, true, ct);
        var broken = await QueryAsync(server, BrokenSignatureDomain, 1, true, ct);
        if (signed.Error is not null || broken.Error is not null)
            dnssec = "Denetlenemedi (" + (signed.Error ?? broken.Error) + ")";
        else if (signed.Authenticated && broken.Rcode == 2)
        {
            validates = true;
            dnssec = "Doğruluyor (imzalı yanıt doğrulandı, bozuk imza reddedildi)";
        }
        else if (!signed.Authenticated && broken.Rcode == 0)
        {
            validates = false;
            dnssec = "Doğrulamıyor (bozuk imzalı alan adı da çözümlendi)";
        }
        else
            dnssec = $"Belirsiz (AD={(signed.Authenticated ? 1 : 0)}, bozuk imza yanıt kodu {broken.Rcode})";

        var state = succeeded == 0 ? CheckState.Error
            : succeeded < TestDomains.Length ? CheckState.Warning
            : times.Count > 0 && SpeedTestMath.Median(times) > 150 ? CheckState.Warning
            : CheckState.Healthy;
        return new DnsServerResult(server, adapter, false, TestDomains.Length, succeeded,
            times.Count > 0 ? SpeedTestMath.Median(times) : null, times.Count > 0 ? times.Max() : null, validates, dnssec, failures, state);
    }

    internal static string Describe(DnsReply r) => r.Error ?? r.Rcode switch
    {
        0 when r.Answers == 0 => "yanıt boş (kayıt yok)",
        1 => "biçim hatası (FORMERR)",
        2 => "sunucu hatası (SERVFAIL)",
        3 => "alan adı bulunamadı (NXDOMAIN)",
        5 => "sunucu sorguyu reddetti (REFUSED)",
        _ => $"yanıt kodu {r.Rcode}"
    };

    /// <summary>
    /// Tek UDP DNS sorgusu (RFC 1035; <paramref name="dnssecOk"/> ise EDNS0 OPT kaydı + DO biti, RFC 6891 / 4035). Kimliği tutmayan / yanıt olmayan
    /// paket kabul edilmez; zaman aşımında bir kez yeniden denenir.
    /// </summary>
    internal static async Task<DnsReply> QueryAsync(IPAddress server, string name, ushort type, bool dnssecOk, CancellationToken ct)
    {
        string? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
            var query = BuildQuery(id, name, type, dnssecOk);
            using var udp = new UdpClient(server.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                udp.Connect(new IPEndPoint(server, 53));
                await udp.SendAsync(query, timeout.Token);
                while (true)
                {
                    var res = await udp.ReceiveAsync(timeout.Token);
                    var parsed = Parse(res.Buffer, id);
                    if (parsed is null) continue; // başka / bozuk paket: beklemeye devam
                    return parsed with { Ms = sw.Elapsed.TotalMilliseconds };
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"{QueryTimeout.TotalSeconds:0} sn içinde yanıt yok";
            }
            catch (SocketException ex)
            {
                return new DnsReply(null, 0, false, null, ex.SocketErrorCode switch
                {
                    SocketError.ConnectionReset => "sunucu 53 numaralı bağlantı noktasında yanıt vermiyor (ICMP port unreachable)",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => "sunucuya ulaşılamıyor",
                    _ => ex.Message
                });
            }
        }
        return new DnsReply(null, 0, false, null, lastError);
    }

    internal static byte[] BuildQuery(ushort id, string name, ushort type, bool dnssecOk)
    {
        var buf = new List<byte>(64);
        void U16(int v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        U16(id);
        U16(0x0100);               // RD (özyinelemeli sorgu)
        U16(1); U16(0); U16(0); U16(dnssecOk ? 1 : 0);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Geçersiz alan adı: " + name, nameof(name));
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);
        U16(type);
        U16(1);                    // IN
        if (dnssecOk)
        {
            buf.Add(0);            // kök ad
            U16(41);               // OPT
            U16(1232);             // UDP yük boyutu
            buf.Add(0); buf.Add(0); // genişletilmiş RCODE + sürüm
            U16(0x8000);           // DO biti
            U16(0);                // RDATA yok
        }
        return buf.ToArray();
    }

    /// <summary>Yanıt başlığını okur; kimlik / QR biti tutmazsa null (paket yok sayılır).</summary>
    internal static DnsReply? Parse(byte[] data, ushort id)
    {
        if (data.Length < 12) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(data) != id) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if ((flags & 0x8000) == 0) return null; // yanıt değil
        var rcode = flags & 0x000F;
        var ad = (flags & 0x0020) != 0;
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        return new DnsReply(rcode, answers, ad, null, null);
    }
}

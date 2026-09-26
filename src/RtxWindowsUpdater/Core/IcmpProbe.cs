using System.Net;
using System.Net.NetworkInformation;

namespace RtxWindowsUpdater.Core;

/// <summary>ICMP yankı dizisinin gerçek sonucu. Error: ICMP hiç gönderilemediyse sistemin mesajı.</summary>
public sealed record IcmpResult(int Sent, IReadOnlyList<double> RttsMs, string? Error)
{
    public int Received => RttsMs.Count;

    /// <summary>Kayıp oranı; hiç yanıt yoksa hesaplanmaz (ICMP engellenmiş olabilir – %100 kayıp denmez).</summary>
    public double? LossPercent => Received == 0 || Sent == 0 ? null : (Sent - Received) * 100.0 / Sent;
}

/// <summary>
/// ICMP yankı istekleri (ping). Hız testi paket kaybı ve Ağ Merkezi ağ geçidi / internet testleri aynı kodu kullanır.
/// İstekler <paramref name="interval"/> arayla gönderilir, yanıtlar paralel beklenir.
/// </summary>
public static class IcmpProbe
{
    public static async Task<IcmpResult> RunAsync(IPAddress address, int count, TimeSpan interval, int timeoutMs, CancellationToken ct)
    {
        if (count <= 0) return new IcmpResult(0, [], null);
        var tasks = new List<Task<PingReply?>>();
        string? error = null;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            tasks.Add(SendAsync());
            if (i < count - 1) await Task.Delay(interval, ct);
        }
        var replies = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        var ok = replies.Where(r => r?.Status == IPStatus.Success).Select(r => (double)r!.RoundtripTime).ToList();
        return new IcmpResult(count, ok, ok.Count == 0 ? error : null);

        async Task<PingReply?> SendAsync()
        {
            try
            {
                using var ping = new Ping();
                return await ping.SendPingAsync(address, timeoutMs);
            }
            catch (PingException ex)
            {
                error ??= ex.InnerException?.Message ?? ex.Message;
                return null;
            }
        }
    }
}

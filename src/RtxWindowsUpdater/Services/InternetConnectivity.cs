using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace RtxWindowsUpdater.Services;

public enum InternetState
{
    Connected,
    /// <summary>Hiçbir ağ bağdaştırıcısı bağlı değil (Wi-Fi / Ethernet).</summary>
    NoNetwork,
    /// <summary>Bağlantı var ama test adresi beklenen yanıtı vermedi (oturum açma sayfası / proxy).</summary>
    Limited,
    /// <summary>Test adreslerine hiç ulaşılamadı (gerçek hata mesajıyla).</summary>
    Unreachable
}

public sealed record InternetCheckResult(InternetState State, string Detail)
{
    public bool IsConnected => State == InternetState.Connected;
}

/// <summary>
/// İnternet bağlantısı gereksinimi (KULLANICI KARARI 2026-09-27: "interneti yoksa uygulamaya giremesin, kullanamasın").
/// Windows'un bağlantı göstergesinin (NCSI) kullandığı Microsoft test adresleri gerçekten istenir; yalnızca beklenen yanıt gelirse
/// "bağlı" sayılır. Ağ bağdaştırıcısı yoksa "ağ yok", yanıt geliyor ama farklıysa "sınırlı" (oturum açma sayfası / proxy), hiç
/// ulaşılamıyorsa gerçek hata mesajıyla "erişilemiyor". Kablosuz ağ adı (SSID) okunmaz / yazılmaz.
/// </summary>
public static class InternetConnectivity
{
    private static readonly (Uri Url, string Expected)[] Probes =
    [
        (new Uri("http://www.msftconnecttest.com/connecttest.txt"), "Microsoft Connect Test"),
        (new Uri("http://www.msftncsi.com/ncsi.txt"), "Microsoft NCSI")
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new() { Timeout = ProbeTimeout };

    public static async Task<InternetCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return new(InternetState.NoNetwork, L.T("Ağ bağlantısı yok – Wi-Fi veya Ethernet bağlı değil.", "No network connection – Wi-Fi or Ethernet is not connected."));

        var limited = false;
        string? error = null;
        foreach (var (url, expected) in Probes)
        {
            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && text.Contains(expected, StringComparison.Ordinal))
                    return new(InternetState.Connected, L.T($"Bağlı · {ConnectionName()}", $"Connected · {ConnectionName()}"));
                limited = true;
                error = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                error = ex is TaskCanceledException ? L.T($"{ProbeTimeout.TotalSeconds:0} sn içinde yanıt gelmedi", $"no response within {ProbeTimeout.TotalSeconds:0} sec") : ex.Message;
            }
        }

        return limited
            ? new(InternetState.Limited, L.T("İnternet erişimi sınırlı – bağlantı var ama Microsoft'un bağlantı testi beklenen yanıtı vermedi ", "Internet access is limited – there is a connection, but Microsoft's connectivity test did not return the expected answer ") +
                                         L.T("(oturum açma sayfası veya proxy olabilir).", "(it may be a sign-in page or a proxy)."))
            : new(InternetState.Unreachable, L.T("İnternete erişilemiyor: ", "The internet cannot be reached: ") + error);
    }

    /// <summary>Etkin bağlantının türü (Wi-Fi / Ethernet); ağ adı okunmaz.</summary>
    private static string ConnectionName()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .FirstOrDefault(n => n.GetIPProperties().GatewayAddresses
                    .Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)));
            return nic?.NetworkInterfaceType switch
            {
                null => L.T("etkin bağlantı", "active connection"),
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => L.T("mobil geniş bant", "mobile broadband"),
                _ => L.T("etkin bağlantı", "active connection")
            };
        }
        catch (NetworkInformationException)
        {
            return L.T("etkin bağlantı", "active connection");
        }
    }
}

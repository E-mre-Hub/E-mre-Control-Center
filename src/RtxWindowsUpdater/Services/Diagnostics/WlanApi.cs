using System.Runtime.InteropServices;
using System.Text;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>
/// Windows WLAN API (wlanapi.dll) ile bağlı Wi-Fi ağının adı, sinyal kalitesi, standardı ve bağlantı hızları. Windows 11 24H2 ve sonrası
/// ağ adını yalnızca masaüstü uygulamalarının konuma erişimine izin verildiyse döndürür; reddedilirse gerçek neden bildirilir.
/// </summary>
internal static class WlanApi
{
    private const int CurrentConnection = 7; // wlan_intf_opcode_current_connection
    private const int InterfaceInfoSize = 532; // GUID + WCHAR[256] + state

    public static WifiInfo ReadCurrent()
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            var rc = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (rc != 0) return Fail(rc);
            rc = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (rc != 0) return Fail(rc);
            var count = Marshal.ReadInt32(list, 0);
            uint lastError = 0;
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * InterfaceInfoSize;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var state = Marshal.ReadInt32(item, 16 + 512);
                if (state != 1) continue; // wlan_interface_state_connected
                rc = WlanQueryInterface(handle, ref guid, CurrentConnection, IntPtr.Zero, out _, out var data, out _);
                if (rc != 0)
                {
                    lastError = rc;
                    continue;
                }
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: 8 + profil adı (512) → ilişkilendirme öznitelikleri 520'den başlar.
                    const int assoc = 520;
                    var ssidLen = Math.Clamp(Marshal.ReadInt32(data, assoc), 0, 32);
                    var ssidBytes = new byte[ssidLen];
                    Marshal.Copy(data + assoc + 4, ssidBytes, 0, ssidLen);
                    var phy = Marshal.ReadInt32(data, assoc + 48);
                    var signal = Marshal.ReadInt32(data, assoc + 56);
                    var rx = Marshal.ReadInt32(data, assoc + 60);
                    var tx = Marshal.ReadInt32(data, assoc + 64);
                    return new WifiInfo(ssidLen > 0 ? Encoding.UTF8.GetString(ssidBytes) : null,
                        signal is >= 0 and <= 100 ? signal : null, PhyText(phy),
                        rx > 0 ? rx / 1000 : null, tx > 0 ? tx / 1000 : null, null);
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }
            return lastError != 0 ? Fail(lastError) : new WifiInfo(null, null, null, null, null, L.T("Bağlı Wi-Fi arabirimi bulunamadı.", "No connected Wi-Fi interface was found."));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new WifiInfo(null, null, null, null, null, L.T("Bu sistemde WLAN hizmeti yok (wlanapi.dll yüklenemedi).", "There is no WLAN service on this system (wlanapi.dll could not be loaded)."));
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static WifiInfo Fail(uint code) => new(null, null, null, null, null, code switch
    {
        5 => L.T("Wi-Fi ayrıntıları okunamadı: erişim reddedildi. Windows 11, ağ adını yalnızca Ayarlar → Gizlilik ve güvenlik → Konum → ", "Could not read Wi-Fi details: access denied. Windows 11 provides the network name only when Settings → Privacy & security → Location → ") +
             L.T("\"Masaüstü uygulamalarının konumunuza erişmesine izin ver\" açıkken verir.", "\"Let desktop apps access your location\" is on."),
        1062 => L.T("WLAN Otomatik Yapılandırma hizmeti (WlanSvc) çalışmıyor.", "The WLAN AutoConfig service (WlanSvc) is not running."),
        5023 => L.T("Wi-Fi arabirimi bağlı değil.", "The Wi-Fi interface is not connected."),
        _ => L.T($"Wi-Fi ayrıntıları okunamadı (Windows hata kodu {code}).", $"Could not read Wi-Fi details (Windows error code {code}).")
    });

    private static string? PhyText(int phy) => phy switch
    {
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4 (802.11n)", 8 => "Wi-Fi 5 (802.11ac)",
        9 => "802.11ad", 10 => "Wi-Fi 6 (802.11ax)", 11 => "Wi-Fi 7 (802.11be)", _ => null
    };

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}

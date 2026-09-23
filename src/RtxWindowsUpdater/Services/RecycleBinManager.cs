using System.Runtime.InteropServices;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows Çöp Kutusu – Shell API (SHQueryRecycleBin / SHEmptyRecycleBin) ile tüm sürücülerdeki
/// geçerli kullanıcının çöp kutusu okunur ve yalnızca kullanıcı onayladığında boşaltılır.
/// </summary>
public sealed class RecycleBinManager(Logger logger) : IUpdateModule
{
    public string Key => ComponentKeys.RecycleBin;
    public string DisplayName => "Çöp Kutusu";

    // x64'te doğal hizalama (cbSize = 24); uygulama yalnızca win-x64 için yayımlanır.
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    public static (long Items, long Bytes, int HResult) Query()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        var hr = SHQueryRecycleBin(null, ref info);
        return (info.i64NumItems, info.i64Size, hr);
    }

    public Task<ModuleResult> CheckAsync(CancellationToken ct) => Task.Run(() =>
    {
        logger.Info("Çöp kutusu kontrol ediliyor...");
        try
        {
            var (items, bytes, hr) = Query();
            if (hr != 0)
            {
                var reason = $"Çöp kutusu okunamadı (HRESULT 0x{unchecked((uint)hr):X8}).";
                logger.Error(reason);
                return ModuleResult.CheckFailed(Key, reason);
            }

            if (items == 0)
            {
                logger.Success("Çöp kutusu zaten boş.");
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.UpToDate,
                    Summary = "Çöp kutusu zaten boş",
                    Details = "Temizlenecek öğe yok."
                };
            }

            logger.Info($"{items} öğe bulundu ({FormatSize(bytes)}).");
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpdateAvailable,
                Summary = $"{items} adet öğe bulundu",
                Details = $"Toplam boyut: {FormatSize(bytes)}\nBoşaltma işlemi kalıcıdır ve yalnızca onayınızla yapılır.",
                ActionableCount = (int)Math.Min(items, int.MaxValue)
            };
        }
        catch (Exception ex)
        {
            var reason = "Çöp kutusu okunamadı: " + ex.Message;
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
    }, ct);

    public Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct) => Task.Run(() =>
    {
        logger.Info("Çöp kutusu temizleniyor...");
        try
        {
            var (before, bytes, _) = Query();
            if (before == 0)
            {
                logger.Success("Çöp kutusu zaten boş.");
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.UpToDate,
                    Summary = "Çöp kutusu zaten boş"
                };
            }

            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
            var (after, _, _) = Query();

            if (after == 0)
            {
                logger.Success($"Çöp kutusu başarıyla temizlendi ({before} öğe, {FormatSize(bytes)}).");
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.Updated,
                    Summary = "Çöp kutusu başarıyla temizlendi",
                    Details = $"Silinen öğe: {before}\nBoşaltılan alan: {FormatSize(bytes)}"
                };
            }

            var reason = $"Çöp kutusu tamamen boşaltılamadı: {after} öğe kaldı (HRESULT 0x{unchecked((uint)hr):X8}). Bazı dosyalar kullanımda olabilir.";
            logger.Error(reason);
            return new ModuleResult
            {
                Key = Key,
                Status = after < before ? ComponentStatus.PartiallyUpdated : ComponentStatus.Failed,
                Summary = after < before ? "Kısmen temizlendi" : "Temizlenemedi",
                Reason = reason
            };
        }
        catch (Exception ex)
        {
            var reason = "Çöp kutusu temizlenemedi: " + ex.Message;
            logger.Error(reason);
            return ModuleResult.Failed(Key, reason);
        }
    }, CancellationToken.None);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:N0} KB",
        _ => $"{bytes} bayt"
    };
}

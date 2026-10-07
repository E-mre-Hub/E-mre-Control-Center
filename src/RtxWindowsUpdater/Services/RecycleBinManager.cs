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
    public string DisplayName => L.T("Çöp Kutusu", "Recycle Bin");

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
        logger.Info(L.T("Çöp kutusu kontrol ediliyor...", "Checking the Recycle Bin..."));
        try
        {
            var (items, bytes, hr) = Query();
            ExecutionTrace.Note(L.T($"SHQueryRecycleBin → HRESULT 0x{unchecked((uint)hr):X8}, {items} öğe, {FormatSize(bytes)}", $"SHQueryRecycleBin → HRESULT 0x{unchecked((uint)hr):X8}, {items} item(s), {FormatSize(bytes)}"));
            if (hr != 0)
            {
                var reason = L.T($"Çöp kutusu okunamadı (HRESULT 0x{unchecked((uint)hr):X8}).", $"Could not read the Recycle Bin (HRESULT 0x{unchecked((uint)hr):X8}).");
                logger.Error(reason);
                return ModuleResult.CheckFailed(Key, reason);
            }

            if (items == 0)
            {
                logger.Success(L.T("Çöp kutusu zaten boş.", "The Recycle Bin is already empty."));
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.UpToDate,
                    Summary = L.T("Çöp kutusu zaten boş", "The Recycle Bin is already empty"),
                    Details = L.T("Temizlenecek öğe yok.", "Nothing to clean.")
                };
            }

            logger.Info(L.T($"{items} öğe bulundu ({FormatSize(bytes)}).", $"{items} item(s) found ({FormatSize(bytes)})."));
            return new ModuleResult
            {
                Key = Key,
                Status = ComponentStatus.UpdateAvailable,
                Summary = L.T($"{items} adet öğe bulundu", $"{items} item(s) found"),
                Details = L.T($"Toplam boyut: {FormatSize(bytes)}\nBoşaltma işlemi kalıcıdır ve yalnızca onayınızla yapılır.", $"Total size: {FormatSize(bytes)}\nEmptying is permanent and done only with your approval."),
                ActionableCount = (int)Math.Min(items, int.MaxValue)
            };
        }
        catch (Exception ex)
        {
            var reason = L.T("Çöp kutusu okunamadı: ", "Could not read the Recycle Bin: ") + ex.Message;
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
    }, ct);

    public Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct) => Task.Run(() =>
    {
        logger.Info(L.T("Çöp kutusu temizleniyor...", "Emptying the Recycle Bin..."));
        try
        {
            var (before, bytes, _) = Query();
            if (before == 0)
            {
                logger.Success(L.T("Çöp kutusu zaten boş.", "The Recycle Bin is already empty."));
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.UpToDate,
                    Summary = L.T("Çöp kutusu zaten boş", "The Recycle Bin is already empty")
                };
            }

            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
            var (after, _, _) = Query();
            ExecutionTrace.Note(L.T($"SHEmptyRecycleBin → HRESULT 0x{unchecked((uint)hr):X8}; önce {before} öğe, sonra {after} öğe", $"SHEmptyRecycleBin → HRESULT 0x{unchecked((uint)hr):X8}; {before} item(s) before, {after} item(s) after"));

            if (after == 0)
            {
                logger.Success(L.T($"Çöp kutusu başarıyla temizlendi ({before} öğe, {FormatSize(bytes)}).", $"The Recycle Bin was emptied successfully ({before} item(s), {FormatSize(bytes)})."));
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.Updated,
                    Summary = L.T("Çöp kutusu başarıyla temizlendi", "The Recycle Bin was emptied successfully"),
                    Details = L.T($"Silinen öğe: {before}\nBoşaltılan alan: {FormatSize(bytes)}", $"Deleted items: {before}\nSpace freed: {FormatSize(bytes)}")
                };
            }

            var reason = L.T($"Çöp kutusu tamamen boşaltılamadı: {after} öğe kaldı (HRESULT 0x{unchecked((uint)hr):X8}). Bazı dosyalar kullanımda olabilir.", $"The Recycle Bin could not be emptied completely: {after} item(s) remain (HRESULT 0x{unchecked((uint)hr):X8}). Some files may be in use.");
            logger.Error(reason);
            return new ModuleResult
            {
                Key = Key,
                Status = after < before ? ComponentStatus.PartiallyUpdated : ComponentStatus.Failed,
                Summary = after < before ? L.T("Kısmen temizlendi", "Partially emptied") : L.T("Temizlenemedi", "Could not be emptied"),
                Reason = reason
            };
        }
        catch (Exception ex)
        {
            var reason = L.T("Çöp kutusu temizlenemedi: ", "Could not empty the Recycle Bin: ") + ex.Message;
            logger.Error(reason);
            return ModuleResult.Failed(Key, reason);
        }
    }, CancellationToken.None);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:N0} KB",
        _ => L.T($"{bytes} bayt", $"{bytes} bytes")
    };
}

using System.Management;
using System.Runtime.InteropServices;

namespace RtxWindowsUpdater.Core;

/// <summary>WMI sorgusu sonucu: satırlar veya gerçek neden (erişim reddi, desteklenmeyen sınıf, zaman aşımı…).</summary>
public sealed record WmiResult(IReadOnlyList<ManagementBaseObject> Rows, string? Error, bool AccessDenied = false)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Tanılama servislerinin ortak WMI sorgu yardımcısı. Sorgu ayrı iş parçacığında, zaman aşımıyla çalışır; hatalar kullanıcıya
/// gösterilebilecek Türkçe nedenlere çevrilir (uydurma değer üretilmez: hata varsa satır listesi boştur ve Error doludur).
/// </summary>
public static class Wmi
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static Task<WmiResult> QueryAsync(string scope, string wql, CancellationToken ct = default, TimeSpan? timeout = null) =>
        Task.Run(() => Query(scope, wql, timeout ?? DefaultTimeout), ct);

    public static WmiResult Query(string scope, string wql, TimeSpan timeout)
    {
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            var rows = new List<ManagementBaseObject>();
            foreach (var row in searcher.Get()) rows.Add(row);
            return new WmiResult(rows, null);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            return new WmiResult([], Describe(ex), IsAccessDenied(ex));
        }
    }

    public static bool IsAccessDenied(Exception ex) => ex switch
    {
        ManagementException m => m.ErrorCode == ManagementStatus.AccessDenied,
        UnauthorizedAccessException => true,
        COMException c => (uint)c.HResult == 0x80070005,
        _ => false
    };

    /// <summary>WMI / COM hatasını anlaşılır nedene çevirir (gerçek kod parantez içinde korunur).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ManagementException m => m.ErrorCode switch
        {
            ManagementStatus.AccessDenied => "Erişim reddedildi (yönetici yetkisi gerekiyor olabilir).",
            ManagementStatus.InvalidNamespace => "Bu Windows'ta ilgili WMI ad alanı yok (özellik desteklenmiyor).",
            ManagementStatus.InvalidClass => "Bu Windows'ta ilgili WMI sınıfı yok (özellik desteklenmiyor).",
            ManagementStatus.NotSupported => "Bu sistemde desteklenmiyor.",
            ManagementStatus.Timedout => "WMI yanıtı zaman aşımına uğradı.",
            ManagementStatus.ProviderLoadFailure => "WMI sağlayıcısı yüklenemedi.",
            _ => $"WMI hatası: {m.Message.Trim()} ({m.ErrorCode})."
        },
        UnauthorizedAccessException => "Erişim reddedildi (yönetici yetkisi gerekiyor).",
        COMException c when (uint)c.HResult == 0x80070005 => "Erişim reddedildi (yönetici yetkisi gerekiyor).",
        COMException c when (uint)c.HResult == 0x80041010 => "Bu Windows'ta ilgili WMI sınıfı yok (özellik desteklenmiyor).",
        COMException c => $"COM hatası: {c.Message.Trim()} (0x{(uint)c.HResult:X8}).",
        TimeoutException => "WMI yanıtı zaman aşımına uğradı.",
        _ => ex.Message
    };

    // ------------------------------------------------------------------ güvenli alan okuma

    public static string? Str(this ManagementBaseObject o, string name)
    {
        var v = Get(o, name);
        return v is null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } s ? s : null;
    }

    public static long? Long(this ManagementBaseObject o, string name)
    {
        var v = Get(o, name);
        try { return v is null ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    public static ulong? ULong(this ManagementBaseObject o, string name)
    {
        var v = Get(o, name);
        try { return v is null ? null : Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    public static bool? Bool(this ManagementBaseObject o, string name) => Get(o, name) is bool b ? b : null;

    public static DateTime? Date(this ManagementBaseObject o, string name)
    {
        var s = Str(o, name);
        if (s is null) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException or FormatException) { return null; }
    }

    private static object? Get(ManagementBaseObject o, string name)
    {
        try { return o[name]; }
        catch (ManagementException) { return null; } // alan bu sürümde yok
    }
}

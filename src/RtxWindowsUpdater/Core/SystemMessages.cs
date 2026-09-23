using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Windows araçlarının (sfc.exe vb.) kullanıcıya yazdığı mesajları, aracın kendi kaynak
/// dosyasından Windows'un görüntüleme dilinde yükler. Böylece aracın çıktısı Türkçe, İngilizce
/// veya başka bir dilde olsa da, Windows'un gerçek mesaj metniyle birebir karşılaştırma yapılabilir.
/// Yükleme başarısız olursa çağıran taraf İngilizce yedek metinleri kullanır.
/// </summary>
public static class SystemMessages
{
    private const uint LoadLibraryAsDatafile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const uint FormatMessageFromHModule = 0x00000800;
    private const uint FormatMessageIgnoreInserts = 0x00000200;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hLibModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int FormatMessage(uint dwFlags, IntPtr lpSource, uint dwMessageId, uint dwLanguageId,
        StringBuilder lpBuffer, int nSize, IntPtr arguments);

    /// <summary>Mesaj tablosundaki (RT_MESSAGETABLE) metinleri kullanıcı dilinde yükler.</summary>
    public static Dictionary<uint, string> LoadMessageTable(string modulePath, IEnumerable<uint> ids)
    {
        var result = new Dictionary<uint, string>();
        var module = LoadLibraryEx(modulePath, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero) return result;
        try
        {
            var buffer = new StringBuilder(4096);
            foreach (var id in ids)
            {
                buffer.Clear();
                var len = FormatMessage(FormatMessageFromHModule | FormatMessageIgnoreInserts,
                    module, id, 0, buffer, buffer.Capacity, IntPtr.Zero);
                if (len > 0) result[id] = buffer.ToString(0, len);
            }
        }
        finally
        {
            FreeLibrary(module);
        }
        return result;
    }

    /// <summary>Boşlukları sadeleştirir ve küçük harfe çevirir (karşılaştırma için).</summary>
    public static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>
    /// Bir Windows mesajının ilk cümlesini karşılaştırma anahtarı olarak döndürür.
    /// (Mesajların devamı genellikle log dosyası yolu gibi değişken açıklamalardır.)
    /// </summary>
    public static string FirstSentenceKey(string message)
    {
        var n = Normalize(message.Replace("%0", string.Empty).Replace("%%", "%"));
        var idx = n.IndexOf(". ", StringComparison.Ordinal);
        return idx > 0 ? n[..(idx + 1)] : n;
    }

    /// <summary>
    /// "Verification %1!u!%% complete." gibi yerelleştirilmiş bir şablondan ilerleme yüzdesini
    /// yakalayan bir düzenli ifade üretir.
    /// </summary>
    public static Regex? BuildPercentRegex(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var t = template.Replace("%0", string.Empty).Trim();
        var i = t.IndexOf("%1", StringComparison.Ordinal);
        if (i < 0) return null;

        var end = i + 2;
        if (end < t.Length && t[end] == '!')
        {
            var close = t.IndexOf('!', end + 1);
            end = close > 0 ? close + 1 : end;
        }

        var prefix = Normalize(t[..i].Replace("%%", "%"));
        var suffix = Normalize(t[end..].Replace("%%", "%"));
        var pattern = Regex.Escape(prefix) + @"\s*(\d{1,3})\s*" + Regex.Escape(suffix);
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

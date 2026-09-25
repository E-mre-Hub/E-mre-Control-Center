using System.Globalization;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Arama eşleşmesi: büyük/küçük harf ve aksan duyarsız (ç=c, ğ=g, ş=s, ü=u, ö=o), Türkçe ı / İ harfleri i / I ile eşleşir.
/// Ana sayfa araması ve Ookla sunucu araması kullanır.
/// </summary>
public static class TextSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static string Normalize(string s) => s.Replace('ı', 'i').Replace('İ', 'I');

    public static bool Contains(string text, string query) =>
        query.Length == 0 || CultureInfo.InvariantCulture.CompareInfo.IndexOf(Normalize(text), Normalize(query), Options) >= 0;

    public static bool StartsWith(string text, string query) =>
        query.Length > 0 && CultureInfo.InvariantCulture.CompareInfo.IsPrefix(Normalize(text), Normalize(query), Options);
}

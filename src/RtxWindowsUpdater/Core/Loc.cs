using System.Globalization;
using System.Text.RegularExpressions;

namespace RtxWindowsUpdater.Core;

/// <summary>Uygulama dili. Varsayılan Türkçe; İngilizce v2.0.0 ile eklendi.</summary>
public enum AppLanguage
{
    Turkish,
    English
}

/// <summary>
/// Dil (v2.0.0, KULLANICI İSTEĞİ 2026-10-07: "Türkçe / English, her şeye uygulansın – global"). Her metin kullanım yerinde iki dilde
/// yazılır: <c>L.T("Türkçe", "English")</c> (XAML'da <c>{l:T 'Türkçe', 'English'}</c>). Dil, ilk pencere oluşmadan önce bir kez
/// seçilir (<see cref="Set"/>); değiştirmek uygulamayı yeniden başlatır. Varsayılan Türkçe: dil seçilmemiş testler ve eski davranış aynı kalır.
/// </summary>
public static class L
{
    public static AppLanguage Language { get; private set; } = AppLanguage.Turkish;

    public static bool En => Language == AppLanguage.English;

    /// <summary>"tr" / "en" (state.json ve komut satırı).</summary>
    public static string Code => En ? "en" : "tr";

    /// <summary>Seçili dildeki metin.</summary>
    public static string T(string tr, string en) => En ? en : tr;

    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Büyük harfe çevirme kültürü: Türkçede tr-TR (i → İ), İngilizcede sabit kültür ("Device" → "DEVICE", "DEVİCE" değil).</summary>
    public static CultureInfo CaseCulture => En ? CultureInfo.InvariantCulture : TurkishCulture;

    /// <summary>Seçili dile göre büyük harf.</summary>
    public static string Upper(string text) => text.ToUpper(CaseCulture);

    /// <summary>
    /// Dili ayarlar. İngilizcede sayı / tarih biçimi de İngilizce olur (en-US); Türkçede sistemin biçimi korunur (önceki davranış).
    /// </summary>
    public static void Set(AppLanguage language)
    {
        Language = language;
        if (language == AppLanguage.English)
        {
            var culture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    /// <summary>"tr" / "en" (büyük-küçük harf duyarsız) → dil; tanınmayan değer null.</summary>
    public static AppLanguage? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "tr" or "turkish" or "türkçe" => AppLanguage.Turkish,
        "en" or "english" => AppLanguage.English,
        _ => null
    };

    /// <summary>Windows'un görüntüleme dili Türkçeyse Türkçe, değilse İngilizce (ilk açılıştaki öneri).</summary>
    public static AppLanguage SystemDefault =>
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "tr" ? AppLanguage.Turkish : AppLanguage.English;

    private static readonly Regex Marker = new("«([^«»|]*)\\|([^«»]*)»", RegexOptions.CultureInvariant);

    /// <summary>
    /// Betik metinlerindeki «Türkçe|English» işaretlerini seçili dile çevirir (PowerShell betiklerinin günlüğe yazdığı satırlar).
    /// </summary>
    public static string Pick(string text) =>
        text.Contains('«') ? Marker.Replace(text, m => En ? m.Groups[2].Value : m.Groups[1].Value) : text;
}

using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RtxWindowsUpdater.Views;

/// <summary>Görünüm teması. Varsayılan koyu (uygulamanın özgün tasarımı); açık tema v2.0.0 ile eklendi.</summary>
public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Tema (v2.0.0, KULLANICI İSTEĞİ 2026-10-07: "koyu tema / açık tema – kurulum dahil her yerde"). Renkler iki sözlükte aynı
/// anahtarlarla tanımlıdır (Themes/Colors.Dark.xaml, Colors.Light.xaml); stiller ve ekranlar renkleri DynamicResource ile kullanır.
/// Seçilen sözlük uygulama kaynaklarının EN SONUNA (en yüksek öncelik) eklenir: açık tüm pencereler anında yeni renklere geçer.
/// Theme.xaml koyu sözlüğü kendi içinde de birleştirir (test düzenekleri ve tasarımcı yalnızca Theme.xaml'ı yükler).
/// </summary>
public static class ThemeManager
{
    private const string Folder = "pack://application:,,,/E-mre Control Center;component/Themes/";

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static bool IsLight => Current == AppTheme.Light;

    /// <summary>Tema değişti (OnRender ile çizen denetimler kendini yeniden çizer).</summary>
    public static event Action? Changed;

    /// <summary>"dark" / "light" → tema; tanınmayan değer null.</summary>
    public static AppTheme? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" or "koyu" => AppTheme.Dark,
        "light" or "açık" or "acik" => AppTheme.Light,
        _ => null
    };

    public static string Code(AppTheme theme) => theme == AppTheme.Light ? "light" : "dark";

    /// <summary>Windows'un uygulama teması (Ayarlar → Kişiselleştirme → Renkler → Uygulama modu); okunamazsa koyu.</summary>
    public static AppTheme SystemDefault
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 1 ? AppTheme.Light : AppTheme.Dark;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return AppTheme.Dark;
            }
        }
    }

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        Core.LaunchModes.ThemeCode = Code(theme);
        var app = Application.Current;
        if (app is null) return;

        // Yalnızca daha önce buraya eklenen tema sözlüğü kaldırılır (Source adına göre; Contains iç içe sözlüklerde de arar ve
        // koyu sözlüğü içeren Theme.xaml'ı da yakalardı).
        var merged = app.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
            if (merged[i].Source?.OriginalString is { } src &&
                (src.EndsWith("/Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) || src.EndsWith("/Colors.Light.xaml", StringComparison.OrdinalIgnoreCase)))
                merged.RemoveAt(i);
        merged.Add(new ResourceDictionary
        {
            Source = new Uri(Folder + (theme == AppTheme.Light ? "Colors.Light.xaml" : "Colors.Dark.xaml"), UriKind.Absolute)
        });

        foreach (Window window in app.Windows)
            WindowFrame.Apply(window);
        Changed?.Invoke();
    }

    /// <summary>Başlık çubuğundaki tema düğmesi: düğmenin simgesi geçilecek temayı gösterir (koyudayken güneş, açıkta ay).</summary>
    public static string ToggleGlyph => IsLight ? "" : "";

    public static string ToggleToolTip => IsLight
        ? Core.L.T("Koyu temaya geç", "Switch to dark theme")
        : Core.L.T("Açık temaya geç", "Switch to light theme");

    public static void Toggle() => Apply(IsLight ? AppTheme.Dark : AppTheme.Light);
}

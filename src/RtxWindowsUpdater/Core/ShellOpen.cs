using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Web adreslerini, Windows Ayarlar / Güvenlik sayfalarını ve dosyaları varsayılan uygulamalarıyla açar. Doğrudan ShellExecute, bu
/// uygulama yönetici olarak çalışırken açılan programı da YÖNETİCİ YETKİSİYLE başlatır ve ilişkilendirmeyi kullanıcının kendi kayıt
/// defterinden (HKCU\Software\Classes) okur: yönetici olmayan bir program "ms-settings:" / ".log" ilişkilendirmesini değiştirip
/// kendi komutunu yönetici olarak çalıştırtabilirdi (bilinen fodhelper UAC atlatma yöntemiyle aynı sınıf). Bunun yerine istek Windows
/// Gezgini'ne (tam yoluyla) verilir – Gezgin isteği kullanıcının normal yetkili oturumuna devrettiği için açılan program yönetici
/// yetkisi olmadan çalışır. Hangi adreslerin açılabileceğini çağıran taraf ayrıca sınırlar (izin listesi).
/// </summary>
public static class ShellOpen
{
    /// <summary>Yalnızca https adresi açar; açılamazsa gerçek nedeni döndürür (başarılıysa null).</summary>
    public static string? OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Yalnızca https adresleri açılır.";
        return ViaExplorer(uri.AbsoluteUri);
    }

    /// <summary>Windows'un kendi Ayarlar ("ms-settings:…") veya Windows Güvenliği ("windowsdefender:") sayfasını açar.</summary>
    public static string? OpenWindowsUri(string uri)
    {
        const string settings = "ms-settings:";
        var allowed = uri == "windowsdefender:"
                      || (uri.StartsWith(settings, StringComparison.Ordinal)
                          && uri.Skip(settings.Length).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        if (!allowed) return "Yalnızca Windows Ayarlar / Windows Güvenliği sayfaları açılır.";
        return ViaExplorer(uri);
    }

    /// <summary>Var olan bir dosyayı varsayılan uygulamasıyla açar (ör. günlük dosyası → metin düzenleyici).</summary>
    public static string? OpenFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return "Dosya bulunamadı.";
        return ViaExplorer(path);
    }

    private static string? ViaExplorer(string argument)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo(explorer) { UseShellExecute = false };
            psi.ArgumentList.Add(argument);
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}

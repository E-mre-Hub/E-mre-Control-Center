using System.IO;
using System.Text;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Sistem araçlarını Windows komut işlemcisi (cmd.exe) üzerinden çalıştırmak için güvenli komut satırı üretir:
///   cmd.exe /d /s /c ""C:\Windows\System32\sfc.exe" /verifyonly"
///
/// Güvenlik:
///  - Her argüman ayrı ayrı doğrulanır. cmd.exe'nin özel anlam verdiği karakterleri (" % ! ^ &amp; | &lt; &gt; ve satır sonları)
///    içeren bir argüman REDDEDİLİR (komut enjeksiyonu mümkün olmaz). Boşluk / parantez içeren argümanlar tırnaklanır.
///  - /d: AutoRun komutları çalıştırılmaz. /s /c: dış tırnaklar güvenle kaldırılır.
///  - Bu yalnızca bir çalıştırma biçimidir; yetki YÜKSELTMEZ. Uygulama zaten kullanıcının UAC onayıyla yönetici
///    olarak çalışıyorsa cmd.exe de aynı yetkiyle çalışır, değilse değil.
/// </summary>
public static class CmdCommand
{
    private static readonly char[] Forbidden = ['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n', '\0'];

    public static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>cmd.exe'ye verilecek argüman dizesini üretir.</summary>
    /// <param name="waitForGuiApp">
    /// GUI alt sistemli programlar (MRT.exe, kurulum programları) için "start "" /wait" kullanılır;
    /// böylece cmd.exe programın bitmesini bekler ve çıkış kodunu aynen döndürür.
    /// </param>
    public static string BuildArguments(string executable, IEnumerable<string> args, bool waitForGuiApp = false)
    {
        var sb = new StringBuilder();
        if (waitForGuiApp) sb.Append("start \"\" /wait ");
        sb.Append(Quote(executable, alwaysQuote: true));
        foreach (var a in args)
        {
            sb.Append(' ');
            sb.Append(Quote(a, alwaysQuote: false));
        }
        return "/d /s /c \"" + sb + "\"";
    }

    /// <summary>Günlükte / Detaylı Sonuç panelinde gösterilecek tam komut satırı.</summary>
    public static string Display(string cmdArguments) => "cmd.exe " + cmdArguments;

    public static string Quote(string value, bool alwaysQuote)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(Forbidden) >= 0)
            throw new ArgumentException($"Güvensiz karakter içeren argüman reddedildi: {value}");
        var needsQuotes = alwaysQuote || value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or ',' or ';' or '=');
        return needsQuotes ? "\"" + value + "\"" : value;
    }
}

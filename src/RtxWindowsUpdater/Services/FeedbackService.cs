using System.Net.Http;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services;

/// <summary>Google Formu alan kimlikleri (formun "entry.NNN" adları). Form yanıtları Google E-Tablolar'a bağlıdır.</summary>
public sealed record FeedbackForm(Uri Endpoint, string Reasons, string Message, string AppVersion, string Windows)
{
    // Formun herkese açık "formResponse" adresi ve alan kimlikleri. Boşsa gönderim kapalıdır: kaldırma ekranı "Gönder" sunmaz
    // (gönderilmeyen geri bildirim "gönderildi" gösterilmez). Kimlikler gizli değildir; formun açık sayfasında da görünür.
    // Form: "E-mre Control Center – Kaldırma geri bildirimi" (yanıtlar depo sahibinin Google E-Tablosunda). Kimlikler 2026-09-25'te
    // formun açık sayfasındaki soru verisinden okundu; "Uygulama sürümü" zorunlu (yanlış kimlik → HTTP 400, başarı sayılmaz).
    internal const string FormId = "1FAIpQLSeGXw-S_r2QOU_rUrL4g2exvLinfhixRAArQrHEmAkwSnYqpw";
    internal const string EntryReasons = "entry.983068466";      // Nedenler (kısa yanıt)
    internal const string EntryMessage = "entry.1845743233";     // Mesaj (paragraf)
    internal const string EntryAppVersion = "entry.1814883425";  // Uygulama sürümü (kısa yanıt, zorunlu)
    internal const string EntryWindows = "entry.1635444447";     // Windows sürümü (kısa yanıt)

    public static FeedbackForm? Configured { get; } =
        new[] { FormId, EntryReasons, EntryMessage, EntryAppVersion, EntryWindows }.Any(string.IsNullOrWhiteSpace)
            ? null
            : new FeedbackForm(new Uri($"https://docs.google.com/forms/d/e/{FormId}/formResponse"),
                EntryReasons, EntryMessage, EntryAppVersion, EntryWindows);
}

public sealed record FeedbackResult(bool Success, string Message);

/// <summary>
/// Kaldırma geri bildirimini Google Formuna gönderir. Gönderilen: seçilen nedenler, yazılan mesaj, uygulama sürümü ve Windows sürümü
/// (ad, e-posta, bilgisayar adı veya kullanıcı adı eklenmez). Başarı yalnızca Google'ın HTTP 200 yanıtıdır; formda "Uygulama sürümü"
/// zorunlu olduğundan yanlış alan kimlikleri 400 döndürür ve "gönderildi" gösterilmez.
/// </summary>
public sealed class FeedbackService(FeedbackForm form, Action<string> log)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<FeedbackResult> SendAsync(IReadOnlyList<string> reasons, string message, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>
        {
            [form.Reasons] = reasons.Count == 0 ? "-" : string.Join(", ", reasons),
            [form.Message] = string.IsNullOrWhiteSpace(message) ? "-" : message.Trim(),
            [form.AppVersion] = AppInfo.Version,
            [form.Windows] = DescribeWindows()
        };
        using var http = new HttpClient { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"E-mre-Control-Center/{AppInfo.Version}");
        try
        {
            using var response = await http.PostAsync(form.Endpoint, new FormUrlEncodedContent(fields), ct);
            var code = (int)response.StatusCode;
            log($"Geri bildirim gönderimi: HTTP {code} ({form.Endpoint.Host}); neden sayısı {reasons.Count}, mesaj {message.Trim().Length} karakter.");
            return code == 200
                ? new FeedbackResult(true, "Geri bildiriminiz gönderildi. Teşekkürler!")
                : new FeedbackResult(false, $"Google Formlar geri bildirimi kabul etmedi (HTTP {code}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log("Geri bildirim gönderilemedi: zaman aşımı.");
            return new FeedbackResult(false, $"Sunucu {Timeout.TotalSeconds:0} saniye içinde yanıt vermedi.");
        }
        catch (HttpRequestException ex)
        {
            log($"Geri bildirim gönderilemedi: {ex.Message}");
            return new FeedbackResult(false, "İnternete bağlanılamadı: " + ex.Message);
        }
    }

    /// <summary>"Windows 11 24H2 (Derleme 26100.2894)" – kayıt defterinden (WMI gerekmez).</summary>
    internal static string DescribeWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = int.TryParse(key?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
            var display = key?.GetValue("DisplayVersion") as string;
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : string.Empty;
            return $"Windows {(build >= 22000 ? "11" : "10")}{(string.IsNullOrEmpty(display) ? "" : " " + display)} (Derleme {build}{ubr})";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return $"Windows (Derleme {Environment.OSVersion.Version.Build})";
        }
    }
}

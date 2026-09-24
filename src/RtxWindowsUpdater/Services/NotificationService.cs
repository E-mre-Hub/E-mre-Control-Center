using System.Security;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows 11 sistem bildirimleri (toast). Paketlenmemiş masaüstü uygulamaları için Microsoft'un
/// önerdiği yöntem kullanılır: uygulama kimliği (AUMID) HKCU\Software\Classes\AppUserModelId altında
/// görünen ad ve ikonla kaydedilir, bildirim Windows.UI.Notifications API'si ile gösterilir.
/// Kullanıcı bildirimleri uygulamadan (ayar kutusu) veya Windows Ayarları → Bildirimler'den kapatabilir.
/// Gösterim arka planda yapılır; arayüz iş parçacığını bloklamaz. Hata olursa yalnızca günlüğe yazılır.
/// </summary>
public sealed class NotificationService(Logger logger)
{
    public const string AppId = "E-mre.RTXWindowsUpdater";
    public const string DisplayName = "RTX Windows Updater";

    private bool _registered;

    public bool Enabled { get; set; } = true;

    /// <summary>Uygulama kimliğini Windows'a kaydeder (yalnızca geçerli kullanıcı için, HKCU).</summary>
    public void Register(string? iconPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            key.SetValue("DisplayName", DisplayName);
            if (!string.IsNullOrEmpty(iconPath))
                key.SetValue("IconUri", iconPath);
            _registered = true;
        }
        catch (Exception ex)
        {
            logger.Warning($"Windows bildirim kaydı yapılamadı; bildirimler gösterilmeyecek: {ex.Message}");
        }
    }

    /// <summary>Bildirimi arka planda gösterir. Gönderildiyse true döner.</summary>
    public Task<bool> ShowAsync(string title, string body) => Task.Run(() =>
    {
        if (!Enabled || !_registered) return false;
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);

            // Kısayolu olmayan masaüstü uygulamalarında Setting özelliği okunamayabilir (0x80070490);
            // bu durumda ayar bilinmiyor kabul edilir ve bildirim gönderilir (Windows kapalıysa yine göstermez).
            NotificationSetting? setting = null;
            try { setting = notifier.Setting; } catch { /* ayar okunamadı */ }
            if (setting is not null and not NotificationSetting.Enabled)
            {
                logger.Warning($"Windows bildirimi gösterilmedi: Windows ayarlarında bu uygulamanın bildirimleri kapalı ({setting}).");
                return false;
            }

            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text>" +
                $"<text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual></toast>");
            notifier.Show(new ToastNotification(xml) { Group = "rtx", Tag = DateTime.Now.Ticks.ToString() });
            logger.Info($"Windows bildirimi gönderildi: {title}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"Windows bildirimi gösterilemedi: {ex.Message}");
            return false;
        }
    });
}

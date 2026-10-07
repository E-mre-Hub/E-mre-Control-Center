using System.Windows.Input;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Yasal belgeler (v1.9.0, KULLANICI İSTEĞİ 2026-10-07: "gizlilik politikası, sorumluluk, çerezler, sözleşme – global için"):
/// Gizlilik Politikası ve Kullanım Koşulları (Türkçe + İngilizce, depo: legal/). Gereksinim sayfasındaki kabul kutusu bu belgeleri de
/// kapsar; kabul edilen sürüm ve zaman yalnızca yerel olarak (state.json) kaydedilir. Bağlantılar yalnızca izin listesindeki proje
/// adresleridir ve tarayıcı yönetici yetkisi olmadan açılır (<see cref="ShellOpen"/>).
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand OpenLegalLinkCommand { get; private set; } = null!;

    private void InitializeLegal() =>
        OpenLegalLinkCommand = new RelayCommand(p =>
        {
            if (p is not string url || !AppInfo.LegalLinks.Contains(url)) return;
            var error = ShellOpen.OpenUrl(url);
            if (error is null) _logger.Info("Bağlantı açıldı: " + url);
            else _logger.Warning($"Bağlantı açılamadı ({url}): {error}");
        });

    /// <summary>Güncel yasal belge sürümü bu bilgisayarda kabul edilmiş mi (state.json).</summary>
    private bool IsLegalAccepted => _state.State.LegalAcceptedVersion == AppInfo.LegalVersion;

    public string LegalVersionText => $"Belge sürümü {AppInfo.LegalVersion}";

    /// <summary>Genel Ayarlar → Yasal: kabul kaydı (yalnızca bu bilgisayarda).</summary>
    public string LegalAcceptanceText =>
        _state.State.LegalAcceptedVersion is { } v && _state.State.LegalAcceptedAt is { } at
            ? $"Kullanım Koşulları ve Gizlilik Politikası (sürüm {v}) {at:dd.MM.yyyy HH:mm} tarihinde bu bilgisayarda kabul edildi." +
              (v == AppInfo.LegalVersion ? string.Empty : $" Güncel sürüm {AppInfo.LegalVersion}; bir sonraki girişte yeniden kabul edilir.")
            : "Bu bilgisayarda henüz kabul kaydı yok (gereksinim sayfasındaki kutuyla kabul edilir).";

    /// <summary>Gereksinim sayfasından girişte kabul kaydedilir (yalnızca sürüm değiştiyse / ilk kez).</summary>
    private void RecordLegalAcceptance()
    {
        if (IsLegalAccepted) return;
        _state.MarkLegalAccepted(AppInfo.LegalVersion, DateTime.Now);
        _logger.Info($"Kullanım Koşulları ve Gizlilik Politikası kabul edildi (sürüm {AppInfo.LegalVersion}).");
        OnPropertyChanged(nameof(LegalAcceptanceText));
    }
}

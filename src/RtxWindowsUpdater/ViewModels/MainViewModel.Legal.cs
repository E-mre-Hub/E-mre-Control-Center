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
            if (error is null) _logger.Info(L.T("Bağlantı açıldı: ", "Link opened: ") + url);
            else _logger.Warning(L.T($"Bağlantı açılamadı ({url}): {error}", $"Could not open the link ({url}): {error}"));
        });

    /// <summary>Güncel yasal belge sürümü bu bilgisayarda kabul edilmiş mi (state.json).</summary>
    private bool IsLegalAccepted => _state.State.LegalAcceptedVersion == AppInfo.LegalVersion;

    public string LegalVersionText => L.T($"Belge sürümü {AppInfo.LegalVersion}", $"Document version {AppInfo.LegalVersion}");

    /// <summary>Genel Ayarlar → Yasal: kabul kaydı (yalnızca bu bilgisayarda).</summary>
    public string LegalAcceptanceText =>
        _state.State.LegalAcceptedVersion is { } v && _state.State.LegalAcceptedAt is { } at
            ? L.T($"Kullanım Koşulları ve Gizlilik Politikası (sürüm {v}) {at:dd.MM.yyyy HH:mm} tarihinde bu bilgisayarda kabul edildi.", $"The Terms of Use and Privacy Policy (version {v}) were accepted on this computer on {at:yyyy-MM-dd HH:mm}.") +
              (v == AppInfo.LegalVersion ? string.Empty : L.T($" Güncel sürüm {AppInfo.LegalVersion}; bir sonraki girişte yeniden kabul edilir.", $" Current version {AppInfo.LegalVersion}; it will be accepted again at the next entry."))
            : L.T("Bu bilgisayarda henüz kabul kaydı yok (gereksinim sayfasındaki kutuyla kabul edilir).", "No acceptance record on this computer yet (accepted with the box on the requirements page).");

    /// <summary>Gereksinim sayfasından girişte kabul kaydedilir (yalnızca sürüm değiştiyse / ilk kez).</summary>
    private void RecordLegalAcceptance()
    {
        if (IsLegalAccepted) return;
        _state.MarkLegalAccepted(AppInfo.LegalVersion, DateTime.Now);
        _logger.Info(L.T($"Kullanım Koşulları ve Gizlilik Politikası kabul edildi (sürüm {AppInfo.LegalVersion}).", $"Terms of Use and Privacy Policy accepted (version {AppInfo.LegalVersion})."));
        OnPropertyChanged(nameof(LegalAcceptanceText));
    }
}

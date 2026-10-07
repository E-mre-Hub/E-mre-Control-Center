using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Views;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Dil ve tema (v2.0.0, Genel Ayarlar → Kolay Ayar). Tema anında değişir ve kaydedilir. Dil, pencereler ve metinler oluşturulurken
/// okunduğu için uygulama yeniden başlatılarak değiştirilir: kullanıcı onaylar, seçim state.json'a yazılır, yeni örnek açılır ve bu
/// örnek kapanır. Kontrol / güncelleme / hız testi sürerken dil değiştirilmez (işlem yarıda kesilmez).
/// </summary>
public sealed partial class MainViewModel
{
    private bool _languageChangePending;

    public bool IsTurkishLanguage
    {
        get => !L.En;
        set { if (value && L.En) _ = ChangeLanguageAsync(AppLanguage.Turkish); }
    }

    public bool IsEnglishLanguage
    {
        get => L.En;
        set { if (value && !L.En) _ = ChangeLanguageAsync(AppLanguage.English); }
    }

    public bool IsDarkTheme
    {
        get => !ThemeManager.IsLight;
        set { if (value) SetTheme(AppTheme.Dark); }
    }

    public bool IsLightTheme
    {
        get => ThemeManager.IsLight;
        set { if (value) SetTheme(AppTheme.Light); }
    }

    private void SetTheme(AppTheme theme)
    {
        if (ThemeManager.Current == theme) return;
        ThemeManager.Apply(theme);
        _state.SetTheme(ThemeManager.Code(theme));
        _logger.Info(theme == AppTheme.Light
            ? L.T("Tema: açık.", "Theme: light.")
            : L.T("Tema: koyu.", "Theme: dark."));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
    }

    private void RevertLanguageSelection()
    {
        OnPropertyChanged(nameof(IsTurkishLanguage));
        OnPropertyChanged(nameof(IsEnglishLanguage));
    }

    private async Task ChangeLanguageAsync(AppLanguage language)
    {
        if (_languageChangePending) return;
        _languageChangePending = true;
        try
        {
            // Seçim değişmeden önce düğmeler eski dile döner; yeniden başlatma onaylanırsa yeni örnek doğru seçimi gösterir.
            RevertLanguageSelection();
            var english = language == AppLanguage.English;
            if (IsBusy || SpeedTest.IsWorking)
            {
                await Dialog.ShowAsync(
                    L.T("Dil şu anda değiştirilemez", "The language cannot be changed right now"),
                    L.T("Bir kontrol, güncelleme veya hız testi sürüyor. Dil değişikliği uygulamayı yeniden başlatır; işlem bittikten sonra tekrar deneyin.",
                        "A check, update or speed test is running. Changing the language restarts the app; try again after it finishes."),
                    Icons.Warning, DialogKind.Warning, L.T("Tamam", "OK"));
                return;
            }

            var ok = await Dialog.ShowAsync(
                english ? "Switch to English?" : "Türkçeye geçilsin mi?",
                english
                    ? "The app restarts in English. Your settings, history and logs are kept.\n\nUygulama İngilizce olarak yeniden başlatılır. Ayarlarınız, geçmişiniz ve günlükleriniz korunur."
                    : "Uygulama Türkçe olarak yeniden başlatılır. Ayarlarınız, geçmişiniz ve günlükleriniz korunur.\n\nThe app restarts in Turkish. Your settings, history and logs are kept.",
                Icons.Restart, DialogKind.Question,
                english ? "Restart in English" : "Türkçe yeniden başlat",
                L.T("Vazgeç", "Cancel"));
            if (!ok) return;

            _state.SetLanguage(english ? "en" : "tr");
            _logger.Info(L.T($"Dil değiştirildi: {(english ? "English" : "Türkçe")}; uygulama yeniden başlatılıyor.",
                             $"Language changed: {(english ? "English" : "Turkish")}; restarting the app."));
            var (started, error) = AppLifetime.Restart();
            if (started) return;

            // Yeni örnek açılamadı: seçim kayıtlıdır ve bir sonraki açılışta uygulanır.
            _logger.Error(L.T("Uygulama yeniden başlatılamadı: ", "The app could not be restarted: ") + error);
            await Dialog.ShowAsync(
                L.T("Yeniden başlatılamadı", "Could not restart"),
                L.T("Yeni dil kaydedildi ve uygulama bir sonraki açılışında uygulanacak. Hata: ", "The new language was saved and will be applied the next time the app starts. Error: ") + error,
                Icons.Warning, DialogKind.Warning, L.T("Tamam", "OK"));
        }
        finally
        {
            _languageChangePending = false;
        }
    }
}

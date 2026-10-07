using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Views;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// İlk açılış: dil ve görünüm seçimi (v2.0.0). Metinler seçilen dile göre anında değişir (henüz uygulama dili ayarlanmadığı için
/// <see cref="L"/> değil, seçime bakılarak). Tema seçimi canlı önizlenir. Seçim "Devam Et" ile kaydedilir; Ayarlar'dan değiştirilebilir.
/// </summary>
public sealed class WelcomeViewModel : ObservableObject
{
    private AppLanguage _language;
    private AppTheme _theme;

    public WelcomeViewModel(AppLanguage language, AppTheme theme)
    {
        _language = language;
        _theme = theme;
        ContinueCommand = new RelayCommand(() => Confirmed?.Invoke());
    }

    public event Action? Confirmed;

    public ICommand ContinueCommand { get; }

    public AppLanguage Language => _language;

    public AppTheme Theme => _theme;

    public bool IsTurkish
    {
        get => _language == AppLanguage.Turkish;
        set { if (value) SetLanguage(AppLanguage.Turkish); }
    }

    public bool IsEnglish
    {
        get => _language == AppLanguage.English;
        set { if (value) SetLanguage(AppLanguage.English); }
    }

    public bool IsDark
    {
        get => _theme == AppTheme.Dark;
        set { if (value) SetTheme(AppTheme.Dark); }
    }

    public bool IsLight
    {
        get => _theme == AppTheme.Light;
        set { if (value) SetTheme(AppTheme.Light); }
    }

    private bool En => _language == AppLanguage.English;

    public string Heading => En ? "Welcome" : "Hoş geldiniz";
    public string Subheading => En ? "Choose your language and appearance." : "Dilinizi ve görünümü seçin.";
    public string LanguageLabel => En ? "LANGUAGE" : "DİL";
    public string ThemeLabel => En ? "APPEARANCE" : "GÖRÜNÜM";
    public string TurkishHint => En ? "Interface in Turkish" : "Arayüz Türkçe olur";
    public string EnglishHint => En ? "Interface in English" : "Arayüz İngilizce olur";
    public string DarkTitle => En ? "Dark" : "Koyu";
    public string DarkHint => En ? "Black and navy, neon blue" : "Siyah ve lacivert, neon mavi";
    public string LightTitle => En ? "Light" : "Açık";
    public string LightHint => En ? "White and light gray" : "Beyaz ve açık gri";
    public string ContinueText => En ? "Continue" : "Devam Et";
    public string Note => En ? "You can change these at any time in General Settings." : "Bunları istediğiniz zaman Genel Ayarlar'dan değiştirebilirsiniz.";
    public string CloseTip => En ? "Close" : "Kapat";

    private void SetLanguage(AppLanguage language)
    {
        if (_language == language) return;
        _language = language;
        OnPropertyChanged(string.Empty); // tüm metinler seçilen dile göre yeniden okunur
    }

    private void SetTheme(AppTheme theme)
    {
        if (_theme == theme) return;
        _theme = theme;
        ThemeManager.Apply(theme); // canlı önizleme
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsLight));
    }
}

using System.Collections.ObjectModel;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Cihaz Bilgileri'ndeki bir kart (İşlemci, Ekran Kartı, Bellek, Depolama, İşletim Sistemi): mevcut sistem bilgisi alanları.</summary>
public sealed class DeviceInfoSectionViewModel(string title, string glyph, IReadOnlyList<SystemInfoField> fields)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public IReadOnlyList<SystemInfoField> Fields { get; } = fields;
}

/// <summary>Cihaz Durumu'ndaki halka gösterge (ör. İşlemci kullanımı %, ekran kartı sıcaklığı °C). Değer yoksa "okunamıyor".</summary>
public sealed class DeviceGaugeViewModel(string caption, string unit, double maximum, bool isTemperature) : ObservableObject
{
    private double? _value;
    private string? _note;

    public string Caption { get; } = caption;
    public string Unit { get; } = unit;
    public double Maximum { get; } = maximum;
    public bool IsTemperature { get; } = isTemperature;

    public double? Value => _value;
    public string? Note => _note;
    public bool IsAvailable => _value is not null;

    /// <summary>Halkanın doluluğu (0-1); değer yoksa 0.</summary>
    public double Ratio => _value is { } v && Maximum > 0 ? Math.Clamp(v / Maximum, 0, 1) : 0;

    public string DisplayValue => _value is { } v ? v.ToString("0") : "—";
    public string DisplayUnit => IsAvailable ? Unit : string.Empty;
    public string CaptionText => IsAvailable ? Caption : Caption + " · okunamıyor";
    public string? ToolTipText => IsAvailable ? null : Note;

    /// <summary>Renk seviyesi: sıcaklıkta 80 °C ve üstü "warm", 90 °C ve üstü "hot"; kullanımda %90 ve üstü "warm".</summary>
    public string Level => _value is not { } v ? "none"
        : IsTemperature ? (v >= 90 ? "hot" : v >= 80 ? "warm" : "normal")
        : v >= 90 ? "warm" : "normal";

    public void Update(DeviceReading reading)
    {
        if (_value == reading.Value && _note == reading.Note) return;
        _value = reading.Value;
        _note = reading.Note;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(Ratio));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(DisplayUnit));
        OnPropertyChanged(nameof(CaptionText));
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(Level));
    }
}

/// <summary>Cihaz Durumu'ndaki bir satır (İşlemci, Ekran Kartı, Bellek, Depolama, Fan Hızı): başlık + göstergeler veya açıklama.</summary>
public sealed class DeviceStatusRowViewModel(string key, string title) : ObservableObject
{
    private string _subtitle = string.Empty;
    private string? _note;

    /// <summary>Satırın kimliği (yapı değişmedikçe satırlar yeniden oluşturulmaz, yalnızca değerler güncellenir).</summary>
    public string Key { get; } = key;
    public string Title { get; } = title;
    public ObservableCollection<DeviceGaugeViewModel> Gauges { get; } = [];
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    /// <summary>Gösterge yerine gösterilen açıklama (ör. "NVIDIA ekran kartı yok", "Disk sıcaklığı yönetici yetkisi gerektirir").</summary>
    public string? Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(_note);
}

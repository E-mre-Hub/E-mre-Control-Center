using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services.Diagnostics;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Durum renkleri (dönüştürücüler). Fırçalar paylaşılır ve DONDURULMAZ: tema değişince (<see cref="ThemeManager.Changed"/>) renkleri
/// yerinde güncellenir, böylece bu fırçaları kullanan tüm durum yazıları / noktaları anında yeni temaya geçer (bağlamalar yeniden
/// hesaplanmadan). Yalnızca arayüz iş parçacığında kullanılır. Açık temada renkler beyaz zeminde okunacak kadar koyudur.
/// </summary>
internal static class Palette
{
    public static readonly SolidColorBrush Green = new();
    public static readonly SolidColorBrush Orange = new();
    public static readonly SolidColorBrush Red = new();
    public static readonly SolidColorBrush Gray = new();
    public static readonly SolidColorBrush Blue = new();
    public static readonly SolidColorBrush Text = new();
    public static readonly SolidColorBrush Text2 = new();
    public static readonly SolidColorBrush Output = new();

    private static readonly Dictionary<SolidColorBrush, SolidColorBrush> Soft = new();

    static Palette()
    {
        Apply();
        ThemeManager.Changed += Apply;
    }

    private static void Apply()
    {
        var light = ThemeManager.IsLight;
        Green.Color = light ? Rgb(0x12, 0xA1, 0x50) : Rgb(0x2B, 0xD6, 0x7B);
        Orange.Color = light ? Rgb(0xC8, 0x6A, 0x00) : Rgb(0xFF, 0xA6, 0x3D);
        Red.Color = light ? Rgb(0xD3, 0x2F, 0x3F) : Rgb(0xFF, 0x4D, 0x61);
        Gray.Color = light ? Rgb(0x85, 0x92, 0xA8) : Rgb(0x5D, 0x6A, 0x84);
        Blue.Color = light ? Rgb(0x1C, 0x7F, 0xD9) : Rgb(0x3D, 0xA5, 0xFF);
        Text.Color = light ? Rgb(0x0D, 0x16, 0x26) : Rgb(0xE7, 0xEE, 0xF9);
        Text2.Color = light ? Rgb(0x3A, 0x48, 0x60) : Rgb(0x93, 0xA4, 0xC3);
        Output.Color = light ? Rgb(0x5F, 0x6E, 0x87) : Rgb(0x6F, 0x80, 0xA0);
        foreach (var (solid, soft) in Soft) soft.Color = solid.Color;
    }

    /// <summary>Durum rozetlerinin arka planı: aynı renk, 0.13 opaklık (renk temayla birlikte değişir).</summary>
    public static SolidColorBrush SoftOf(SolidColorBrush solid)
    {
        if (!Soft.TryGetValue(solid, out var soft))
            Soft[solid] = soft = new SolidColorBrush(solid.Color) { Opacity = 0.13 };
        return soft;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

/// <summary>ComponentStatus → renk (Yeşil = güncel, Turuncu = güncelleme mevcut, Kırmızı = hata, Gri = kontrol edilmedi / kullanım dışı).</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ComponentStatus.UpToDate or ComponentStatus.Updated => Palette.Green,
        ComponentStatus.UpdateAvailable or ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired or ComponentStatus.Attention => Palette.Orange,
        ComponentStatus.Failed or ComponentStatus.CheckFailed or ComponentStatus.AdminRequired => Palette.Red,
        ComponentStatus.Checking or ComponentStatus.Updating => Palette.Blue,
        RequirementState.Ok => Palette.Green,
        RequirementState.Failed => Palette.Red,
        RequirementState.Warning => Palette.Orange,
        RequirementState.Pending => Palette.Blue,
        CheckState.Healthy => Palette.Green,
        CheckState.Warning => Palette.Orange,
        CheckState.Error => Palette.Red,
        CheckState.Checking => Palette.Blue,
        CheckState.Info => Palette.Blue,
        _ => Palette.Gray
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Durumu 0.12 opaklıkta arka plan rengine çevirir (durum rozetleri için).</summary>
public sealed class StatusToSoftBrushConverter : IValueConverter
{
    private static readonly StatusToBrushConverter Inner = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var solid = (SolidColorBrush)Inner.Convert(value, targetType, parameter, culture);
        return Palette.SoftOf(solid);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Durum → Segoe Fluent Icons simgesi (emoji değil, vektörel ikon).</summary>
public sealed class StatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ComponentStatus.UpToDate or ComponentStatus.Updated => "",          // CheckMark
        ComponentStatus.UpdateAvailable => "",                              // Download
        ComponentStatus.PartiallyUpdated or ComponentStatus.RebootRequired or ComponentStatus.Attention => "", // Warning
        ComponentStatus.CheckFailed => "",                                  // Warning
        ComponentStatus.Failed => "",                                       // Cancel
        ComponentStatus.AdminRequired => "",                                // Lock
        ComponentStatus.Checking or ComponentStatus.Updating => "",         // Sync
        ComponentStatus.Unavailable => "",                            // Blocked: kullanım dışı
        RequirementState.Ok => "",
        RequirementState.Failed => "",
        RequirementState.Warning => "",
        RequirementState.Pending => "",
        CheckState.Healthy => "\uE73E",                                     // CheckMark
        CheckState.Warning => "\uE7BA",                                     // Warning
        CheckState.Error => "\uE783",                                       // Error
        CheckState.Unknown => "\uE9CE",                                     // Unknown: kontrol edilemedi
        CheckState.Skipped => "\uE738",                                     // Remove: atlandı
        CheckState.Checking => "\uE895",                                    // Sync
        CheckState.Info => "\uE946",                                        // Info
        _ => "\uE823"                                                        // Saat: henüz çalıştırılmadı
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusIsBusyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ComponentStatus.Checking or ComponentStatus.Updating or RequirementState.Pending or CheckState.Checking;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Success => Palette.Green,
        LogLevel.Warning => Palette.Orange,
        LogLevel.Error => Palette.Red,
        LogLevel.Output => Palette.Output,
        _ => Palette.Text2
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            null => false,
            _ => true
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DialogKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DialogKind.Warning => Palette.Orange,
        DialogKind.Result => Palette.Green,
        _ => Palette.Blue
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool değeri tersine çevirir (ör. açılır bilgi kutusu açıkken butonun tekrar tıklanmasını engellemek için).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Değer, ConverterParameter'a eşitse true (filtre düğmeleri için). Geri dönüşte seçilen parametreyi yazar.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter?.ToString() ?? Binding.DoNothing : Binding.DoNothing;
}

/// <summary>
/// Değer ConverterParameter'daki anahtarlardan birine ('|' ile ayrılmış) eşitse Visible, değilse Collapsed
/// (kategori ekranında seçili bölmenin içeriğini göstermek için).
/// </summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        if (string.IsNullOrEmpty(text) || parameter is not string keys) return Visibility.Collapsed;
        foreach (var key in keys.Split('|'))
        {
            if (string.Equals(text, key.Trim(), StringComparison.OrdinalIgnoreCase)) return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

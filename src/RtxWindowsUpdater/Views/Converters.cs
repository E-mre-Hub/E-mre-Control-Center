using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.ViewModels;

namespace RtxWindowsUpdater.Views;

internal static class Palette
{
    public static readonly SolidColorBrush Green = Make(0x2B, 0xD6, 0x7B);
    public static readonly SolidColorBrush Orange = Make(0xFF, 0xA6, 0x3D);
    public static readonly SolidColorBrush Red = Make(0xFF, 0x4D, 0x61);
    public static readonly SolidColorBrush Gray = Make(0x5D, 0x6A, 0x84);
    public static readonly SolidColorBrush Blue = Make(0x3D, 0xA5, 0xFF);
    public static readonly SolidColorBrush Text = Make(0xE7, 0xEE, 0xF9);
    public static readonly SolidColorBrush Text2 = Make(0x93, 0xA4, 0xC3);
    public static readonly SolidColorBrush Output = Make(0x6F, 0x80, 0xA0);

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
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
        var b = new SolidColorBrush(solid.Color) { Opacity = 0.13 };
        b.Freeze();
        return b;
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
        _ => "\uE823"                                                        // Saat: henüz çalıştırılmadı
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusIsBusyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ComponentStatus.Checking or ComponentStatus.Updating or RequirementState.Pending;

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

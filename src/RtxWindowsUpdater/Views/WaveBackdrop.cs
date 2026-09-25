using System.Windows;
using System.Windows.Media;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Ana sayfanın alt kısmındaki ince dalga çizgileri (dekoratif). Statiktir: yalnızca boyut değişince yeniden çizilir; XAML'de
/// BitmapCache ile önbelleğe alınır. Çizgiler yukarıdan aşağı belirginleşir, içerik alanını karıştırmaz.
/// </summary>
public sealed class WaveBackdrop : FrameworkElement
{
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(WaveBackdrop),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2A, 0x3C, 0x5C)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineCountProperty = DependencyProperty.Register(
        nameof(LineCount), typeof(int), typeof(WaveBackdrop), new FrameworkPropertyMetadata(42, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public int LineCount { get => (int)GetValue(LineCountProperty); set => SetValue(LineCountProperty, value); }

    /// <summary>Dalga biçimi (0..1 genişlikte): sol-ortada tepe, sağda çukur, hafif salınım.</summary>
    private static double Shape(double u) =>
        0.95 * Math.Exp(-Math.Pow((u - 0.30) / 0.19, 2))
        - 0.40 * Math.Exp(-Math.Pow((u - 0.80) / 0.11, 2))
        + 0.12 * Math.Sin(u * Math.PI * 2.6);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || LineCount <= 0) return;
        var count = LineCount;
        var spacing = h * 0.40 / count;
        const int segments = 90;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / (count - 1);                 // 0 = en üst çizgi, 1 = en alt
            var baseY = h * 0.60 + i * spacing;
            var amplitude = h * 0.15 * (1 - 0.5 * t);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var s = 0; s <= segments; s++)
                {
                    var x = w * s / segments;
                    var p = new Point(x, baseY - amplitude * Shape(x / w));
                    if (s == 0) ctx.BeginFigure(p, false, false);
                    else ctx.LineTo(p, true, true);
                }
            }
            geometry.Freeze();
            var pen = new Pen(LineBrush, 1) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.PushOpacity(0.05 + 0.75 * t * t);              // üstte (öğelerin arkası) neredeyse görünmez, altta belirgin
            dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
        }
    }
}

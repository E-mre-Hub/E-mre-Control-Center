using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Hız testi göstergesi: 270°'lik yay, doğrusal olmayan ölçek (0 · 5 · 10 · 50 · 100 · 250 · 500 · 750 · 1000 Mbps), ibre.
/// Değer yalnızca gerçek ölçümden gelir (saniyede 10 kez); sürekli animasyon yoktur.
/// Test sürerken saniyede 10 kez yeniden çizilir; gölgesi ayrı statik katmanda olan kartta durur.
/// </summary>
public sealed class SpeedGauge : FrameworkElement
{
    public static readonly double[] Marks = [0, 5, 10, 50, 100, 250, 500, 750, 1000];
    private const double StartAngle = -135;
    private const double Sweep = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SpeedGauge), new PropertyMetadata(0d, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(SpeedGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, ClearLabels));

    public static readonly DependencyProperty ActiveLabelBrushProperty = DependencyProperty.Register(
        nameof(ActiveLabelBrush), typeof(Brush), typeof(SpeedGauge), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, ClearLabels));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(SpeedGauge), new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush ActiveLabelBrush { get => (Brush)GetValue(ActiveLabelBrushProperty); set => SetValue(ActiveLabelBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private double DisplayValue => (double)GetValue(DisplayValueProperty);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Animasyon YOK: değer ölçümden saniyede 10 kez gelir ve doğrudan çizilir. (Animasyon saati gösterge gizliyken bile
        // WPF çizim döngüsünü her karede çalıştırıyordu: 2026-09-25 ölçümü, test sürerken arayüz süreci ~%16-20 → aşağıda.)
        ((SpeedGauge)d).SetValue(DisplayValueProperty, Math.Max(0, (double)e.NewValue));
    }

    // Ölçek yazıları her karede yeniden oluşturulmaz (9 işaret × geçildi / geçilmedi).
    private readonly Dictionary<(int Index, bool Active), FormattedText> _labels = new();
    private double _labelDpi;

    private static void ClearLabels(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SpeedGauge)d)._labels.Clear();

    private FormattedText Label(int index, bool active, double dpi)
    {
        if (dpi != _labelDpi)
        {
            _labels.Clear();
            _labelDpi = dpi;
        }
        if (_labels.TryGetValue((index, active), out var text)) return text;
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        text = new FormattedText(Marks[index].ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            face, 13.5, active ? ActiveLabelBrush : LabelBrush, dpi);
        _labels[(index, active)] = text;
        return text;
    }

    /// <summary>Değerin ölçekteki yeri (0..1): işaretler arasında doğrusal, 1000 üstü sonda kalır.</summary>
    public static double Fraction(double value)
    {
        if (value <= 0 || double.IsNaN(value)) return 0;
        for (var i = 1; i < Marks.Length; i++)
        {
            if (value <= Marks[i])
                return (i - 1 + (value - Marks[i - 1]) / (Marks[i] - Marks[i - 1])) / (Marks.Length - 1);
        }
        return 1;
    }

    private static Point At(Point c, double r, double angle)
    {
        var rad = angle * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(rad), c.Y - r * Math.Cos(rad));
    }

    private static Geometry Arc(Point c, double r, double from, double to)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(c, r, from), false, false);
            ctx.ArcTo(At(c, r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var t = Thickness;
        var r = size / 2 - t / 2 - 1;
        var value = DisplayValue;
        var f = Fraction(value);
        var angle = StartAngle + Sweep * f;

        dc.DrawGeometry(null, new Pen(TrackBrush, t), Arc(c, r, StartAngle, StartAngle + Sweep));
        if (f > 0.001) dc.DrawGeometry(null, new Pen(FillBrush, t), Arc(c, r, StartAngle, angle));

        // Ölçek etiketleri (yayın iç tarafında); geçilen değerler parlak
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelRadius = r - t / 2 - 22;
        for (var i = 0; i < Marks.Length; i++)
        {
            var text = Label(i, Marks[i] <= value && value > 0, dpi);
            var p = At(c, labelRadius, StartAngle + Sweep * i / (Marks.Length - 1));
            dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        }

        // İbre: merkeze yakın geniş tabandan etiketlere doğru incelen gövde
        var baseR = r * 0.30;
        var tipR = labelRadius - 18;
        var normal = angle + 90;
        var b1 = At(At(c, baseR, angle), 6, normal);
        var b2 = At(At(c, baseR, angle), -6, normal);
        var t1 = At(At(c, tipR, angle), 1.5, normal);
        var t2 = At(At(c, tipR, angle), -1.5, normal);
        var needle = new StreamGeometry();
        using (var ctx = needle.Open())
        {
            ctx.BeginFigure(b1, true, true);
            ctx.LineTo(t1, true, false);
            ctx.LineTo(t2, true, false);
            ctx.LineTo(b2, true, false);
        }
        needle.Freeze();
        var needleBrush = new LinearGradientBrush(Color.FromArgb(0x00, 0xE7, 0xEE, 0xF9), Color.FromArgb(0xF0, 0xE7, 0xEE, 0xF9),
            new Point(0.5, 0), new Point(0.5, 1))
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = At(c, baseR, angle),
            EndPoint = At(c, tipR, angle)
        };
        needleBrush.Freeze();
        dc.DrawGeometry(needleBrush, null, needle);
    }
}

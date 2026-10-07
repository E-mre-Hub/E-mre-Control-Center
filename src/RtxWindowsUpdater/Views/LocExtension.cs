using System.Windows.Markup;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// XAML'da iki dilli metin: <c>Text="{l:T 'Güncelle', 'Update'}"</c>. Seçili dildeki metni döndürür (dil pencere oluşmadan önce
/// belirlenir; değişince uygulama yeniden başlar). Tırnak içindeki ' karakteri \' olarak yazılır.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string tr, string en)
    {
        Tr = tr;
        En = en;
    }

    public string Tr { get; set; } = string.Empty;

    public string En { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Tr, En);
}

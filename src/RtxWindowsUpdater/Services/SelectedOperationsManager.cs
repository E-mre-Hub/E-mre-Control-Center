namespace RtxWindowsUpdater.Services;

/// <summary>
/// Kullanıcının seçtiği işlemleri (kartları) tek bir merkezde tutar.
/// "Tümünü..." akışları bu sınıfa hiç bakmaz; "Seçilenleri..." akışları yalnızca buradan
/// dönen anahtarlarla çalışır. Seçilen işlemler her zaman orkestratörün güvenli işlem sırasıyla döner.
/// </summary>
public sealed class SelectedOperationsManager
{
    private readonly IReadOnlyList<string> _order;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    public SelectedOperationsManager(IEnumerable<string> orderedKeys)
    {
        _order = orderedKeys.ToList();
    }

    /// <summary>Seçim her değiştiğinde tetiklenir.</summary>
    public event EventHandler? SelectionChanged;

    public int Count => _selected.Count;

    public bool HasSelection => _selected.Count > 0;

    public bool IsSelected(string key) => _selected.Contains(key);

    public void SetSelected(string key, bool selected)
    {
        if (!_order.Contains(key)) return;
        var changed = selected ? _selected.Add(key) : _selected.Remove(key);
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seçilen işlemler (güvenli işlem sırasına göre).</summary>
    public IReadOnlyList<string> SelectedKeys => _order.Where(_selected.Contains).ToList();

    /// <summary>Seçilmeyen işlemler (güvenli işlem sırasına göre).</summary>
    public IReadOnlyList<string> UnselectedKeys => _order.Where(k => !_selected.Contains(k)).ToList();
}

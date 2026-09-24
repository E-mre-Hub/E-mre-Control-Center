using System.Windows.Threading;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Arka plan iş parçacıklarından gelen sık ilerleme bildirimlerini birleştirir: her anahtar için yalnızca EN SON değer,
/// en fazla <c>interval</c> aralıkla ve tek bir arayüz işleminde (Background önceliğinde) uygulanır.
/// Değerler değiştirilmez; yalnızca arada kalan ara değerler atlanır (ör. %41, %42, %43 → %43).
/// Böylece SFC/DISM/MRT gibi araçların her çıktı satırı ayrı bir yerleşim/çizim tetiklemez.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly Func<T, string> _keyOf;
    private readonly Action<T> _apply;
    private readonly object _lock = new();
    private readonly Dictionary<string, T> _pending = [];
    private readonly List<string> _order = [];
    private bool _scheduled;

    public ThrottledProgress(Dispatcher dispatcher, TimeSpan interval, Func<T, string> keyOf, Action<T> apply)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        _keyOf = keyOf;
        _apply = apply;
    }

    public void Report(T value)
    {
        bool schedule;
        lock (_lock)
        {
            var key = _keyOf(value);
            if (!_pending.ContainsKey(key)) _order.Add(key);
            _pending[key] = value;
            schedule = !_scheduled;
            _scheduled = true;
        }
        if (schedule) _ = FlushLaterAsync();
    }

    /// <summary>Bir anahtarın bekleyen (henüz uygulanmamış) değerini atar – ör. modülün son sonucu geldiğinde.</summary>
    public void Discard(string key)
    {
        lock (_lock)
        {
            if (_pending.Remove(key)) _order.Remove(key);
        }
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(_interval).ConfigureAwait(false);
        List<T> batch;
        lock (_lock)
        {
            batch = _order.Select(k => _pending[k]).ToList();
            _pending.Clear();
            _order.Clear();
            _scheduled = false;
        }
        if (batch.Count == 0 || _dispatcher.HasShutdownStarted) return;
        // Sonuç beklenmez: uygulama UI iş parçacığında sıraya alınır, bu arka plan görevi hemen biter.
        _ = _dispatcher.BeginInvoke(() =>
        {
            foreach (var v in batch) _apply(v);
        }, DispatcherPriority.Background);
    }
}

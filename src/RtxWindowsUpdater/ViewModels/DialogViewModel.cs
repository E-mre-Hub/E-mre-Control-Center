using System.Collections.ObjectModel;
using System.Windows.Input;

namespace RtxWindowsUpdater.ViewModels;

public enum DialogKind { Info, Question, Warning, Result }

/// <summary>Onay penceresinde kullanıcının seçip bırakabileceği gerçek bir öğe (ör. geçici dosya kategorisi).</summary>
public sealed class ChoiceItemViewModel(string id, string label, long bytes, string sizeText, bool isChecked = true) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Id { get; } = id;
    public string Label { get; } = label;
    public long Bytes { get; } = bytes;
    public string SizeText { get; } = sizeText;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                CheckedChanged?.Invoke();
        }
    }

    internal Action? CheckedChanged { get; set; }
}

/// <summary>
/// Pencere içi modern iletişim kutusu (onay, UAC açıklaması, sonuç ekranı).
/// ShowAsync kullanıcı bir butona basana kadar bekler.
/// </summary>
public sealed class DialogViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _tcs;
    private bool _isOpen;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _glyph = "";
    private DialogKind _kind;
    private string _primaryText = "Tamam";
    private string? _secondaryText;
    private string? _tertiaryText;
    private Action? _tertiaryAction;

    public DialogViewModel()
    {
        PrimaryCommand = new RelayCommand(() => Close(true));
        SecondaryCommand = new RelayCommand(() => Close(false));
        TertiaryCommand = new RelayCommand(() => _tertiaryAction?.Invoke());
    }

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public DialogKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public string PrimaryText { get => _primaryText; private set => Set(ref _primaryText, value); }

    public string? SecondaryText
    {
        get => _secondaryText;
        private set { Set(ref _secondaryText, value); OnPropertyChanged(nameof(HasSecondary)); }
    }

    public string? TertiaryText
    {
        get => _tertiaryText;
        private set { Set(ref _tertiaryText, value); OnPropertyChanged(nameof(HasTertiary)); }
    }

    public bool HasSecondary => !string.IsNullOrEmpty(SecondaryText);
    public bool HasTertiary => !string.IsNullOrEmpty(TertiaryText);

    /// <summary>Onay listesinde gösterilecek maddeler.</summary>
    public ObservableCollection<string> Bullets { get; } = [];

    /// <summary>Sonuç ekranı satırları.</summary>
    public ObservableCollection<ResultRowViewModel> Results { get; } = [];

    /// <summary>Kullanıcının seçebileceği öğeler (ör. temizlenecek geçici dosya kategorileri).</summary>
    public ObservableCollection<ChoiceItemViewModel> Choices { get; } = [];

    private string _choicesTitle = string.Empty;
    public string ChoicesTitle { get => _choicesTitle; private set => Set(ref _choicesTitle, value); }
    public bool HasChoices => Choices.Count > 0;

    private bool _choicesAreSizes = true;

    /// <summary>Seçili öğelerin gerçek toplam boyutu (boyutlu seçimlerde) veya seçili öğe sayısı.</summary>
    public string ChoicesTotalText => _choicesAreSizes
        ? "Seçili toplam: " + RtxWindowsUpdater.Services.TemporaryFilesManager.FormatSize(Choices.Where(c => c.IsChecked).Sum(c => c.Bytes))
        : $"Seçili: {Choices.Count(c => c.IsChecked)}";

    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }
    public ICommand TertiaryCommand { get; }

    public Task<bool> ShowAsync(
        string title, string message, string glyph, DialogKind kind,
        string primary, string? secondary = null,
        IEnumerable<string>? bullets = null, IEnumerable<ResultRowViewModel>? results = null,
        string? tertiary = null, Action? tertiaryAction = null,
        IEnumerable<ChoiceItemViewModel>? choices = null, string? choicesTitle = null, bool choicesAreSizes = true)
    {
        _choicesAreSizes = choicesAreSizes;
        _tcs?.TrySetResult(false);
        _tcs = new TaskCompletionSource<bool>();

        Title = title;
        Message = message;
        Glyph = glyph;
        Kind = kind;
        PrimaryText = primary;
        SecondaryText = secondary;
        TertiaryText = tertiary;
        _tertiaryAction = tertiaryAction;

        Bullets.Clear();
        foreach (var b in bullets ?? []) Bullets.Add(b);
        Results.Clear();
        foreach (var r in results ?? []) Results.Add(r);

        foreach (var c in Choices) c.CheckedChanged = null;
        Choices.Clear();
        foreach (var c in choices ?? [])
        {
            c.CheckedChanged = () => OnPropertyChanged(nameof(ChoicesTotalText));
            Choices.Add(c);
        }
        ChoicesTitle = choicesTitle ?? string.Empty;
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(ChoicesTotalText));

        IsOpen = true;
        return _tcs.Task;
    }

    public void Close(bool result)
    {
        IsOpen = false;
        _tcs?.TrySetResult(result);
    }
}

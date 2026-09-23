using System.Collections.ObjectModel;
using System.Windows.Input;

namespace RtxWindowsUpdater.ViewModels;

public enum DialogKind { Info, Question, Warning, Result }

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

    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }
    public ICommand TertiaryCommand { get; }

    public Task<bool> ShowAsync(
        string title, string message, string glyph, DialogKind kind,
        string primary, string? secondary = null,
        IEnumerable<string>? bullets = null, IEnumerable<ResultRowViewModel>? results = null,
        string? tertiary = null, Action? tertiaryAction = null)
    {
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

        IsOpen = true;
        return _tcs.Task;
    }

    public void Close(bool result)
    {
        IsOpen = false;
        _tcs?.TrySetResult(result);
    }
}

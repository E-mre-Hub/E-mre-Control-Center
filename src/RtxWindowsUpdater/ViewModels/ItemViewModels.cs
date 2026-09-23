using System.Windows.Input;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Ana ekrandaki bileşen kartı (Windows Update, Winget, ... SFC, DISM, MRT).</summary>
public sealed class ComponentCardViewModel(string key, string title, string glyph) : ObservableObject
{
    private ComponentStatus _status = ComponentStatus.NotChecked;
    private string _summary = NotCheckedText;
    private string _details = string.Empty;
    private string? _reason;
    private bool _isSelected;
    private double _progress;
    private bool _progressKnown;
    private string _activityText = string.Empty;

    private const string NotCheckedText = "Henüz çalıştırılmadı";

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    /// <summary>SFC / DISM / MRT gibi, kendi eylem butonu bir Windows bakım aracını çalıştıran kartlar.</summary>
    public bool IsMaintenance { get; init; }

    /// <summary>Kartın kullandığı gerçek Windows mekanizması / komutu (kısa etiket).</summary>
    public string CommandText { get; init; } = string.Empty;

    /// <summary>Kartın ne yaptığını teknik bilgi gerektirmeden anlatan 1-2 satırlık açıklama.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>"?" bilgi butonunda gösterilen metin.</summary>
    public string InfoText { get; init; } = string.Empty;

    public string ActionText { get; init; } = "Kontrol Et";
    public string ActionGlyph { get; init; } = "\uE721";
    public ICommand? ActionCommand { get; set; }

    /// <summary>Seçim durumunu merkezi seçim yöneticisine ileten geri çağırım.</summary>
    public Action<string, bool>? SelectionChangedCallback { get; set; }

    public ComponentStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
    public string? Reason { get => _reason; set => Set(ref _reason, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
                SelectionChangedCallback?.Invoke(Key, value);
        }
    }

    /// <summary>Seçim yöneticisinden gelen değişikliği geri çağırım tetiklemeden uygular.</summary>
    public void SyncSelected(bool selected)
    {
        if (_isSelected == selected) return;
        _isSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }

    public bool IsBusy => Status is ComponentStatus.Checking or ComponentStatus.Updating;

    /// <summary>Canlı ilerleme (0-100). ProgressKnown false ise belirsiz ilerleme gösterilir.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool ProgressKnown { get => _progressKnown; private set => Set(ref _progressKnown, value); }
    public string ActivityText { get => _activityText; private set => Set(ref _activityText, value); }

    public ModuleResult? LastResult { get; private set; }

    public void Apply(ModuleResult r)
    {
        Status = r.Status;
        Summary = r.Summary;
        if (r.Status is ComponentStatus.Checking or ComponentStatus.Updating)
        {
            Progress = 0;
            ProgressKnown = false;
            ActivityText = r.Summary;
        }
        else
        {
            Details = r.Details;
            Reason = r.Reason;
            LastResult = r;
            ActivityText = string.Empty;
            Progress = 0;
            ProgressKnown = false;
        }
    }

    public void ApplyProgress(ModuleProgress p)
    {
        if (!IsBusy) return;
        ActivityText = p.Text;
        if (p.Percent is { } pct)
        {
            ProgressKnown = true;
            Progress = Math.Clamp(pct, 0, 100);
        }
        else
        {
            ProgressKnown = false;
        }
    }

    public void Reset()
    {
        Status = ComponentStatus.NotChecked;
        Summary = NotCheckedText;
        Details = string.Empty;
        Reason = null;
        LastResult = null;
        ActivityText = string.Empty;
        Progress = 0;
        ProgressKnown = false;
    }
}

public enum RequirementState { Pending, Ok, Failed, Warning }

public sealed class RequirementRowViewModel(string title, string glyph) : ObservableObject
{
    private RequirementState _state = RequirementState.Pending;
    private string _detail = "Kontrol ediliyor...";

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public RequirementState State { get => _state; set => Set(ref _state, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

/// <summary>"Bulunan Güncellemeler" tablosundaki bir satır.</summary>
public sealed class UpdateRowViewModel
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool UpdateAvailable { get; init; }
}

/// <summary>Sonuç ekranındaki bir satır.</summary>
public sealed class ResultRowViewModel
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required ComponentStatus Status { get; init; }
    public required string Summary { get; init; }
    public string? Reason { get; init; }
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
}

using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Ana ekrandaki bileşen kartı (Windows Update, Winget, ...).</summary>
public sealed class ComponentCardViewModel(string key, string title, string glyph) : ObservableObject
{
    private ComponentStatus _status = ComponentStatus.NotChecked;
    private string _summary = "Kontrol edilmedi";
    private string _details = string.Empty;
    private string? _reason;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

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

    public bool IsBusy => Status is ComponentStatus.Checking or ComponentStatus.Updating;

    public ModuleResult? LastResult { get; private set; }

    public void Apply(ModuleResult r)
    {
        Status = r.Status;
        Summary = r.Summary;
        if (r.Status is not (ComponentStatus.Checking or ComponentStatus.Updating))
        {
            Details = r.Details;
            Reason = r.Reason;
            LastResult = r;
        }
    }

    public void Reset()
    {
        Status = ComponentStatus.NotChecked;
        Summary = "Kontrol edilmedi";
        Details = string.Empty;
        Reason = null;
        LastResult = null;
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

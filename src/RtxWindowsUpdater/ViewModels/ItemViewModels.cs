using System.Windows.Input;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services;

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
    private CardSnapshot? _lastSnapshot;
    private bool _snapshotFromHistory;
    private string? _unavailableReason;

    private const string NotCheckedText = "Henüz çalıştırılmadı";
    public const string UnavailableText = "Kullanım dışı";
    private const string NotRunThisSessionText = "Bu oturumda çalıştırılmadı";

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    /// <summary>Sağlık özetinde kullanılan kısa ad (ör. "SFC").</summary>
    public string ShortTitle { get; init; } = title;

    /// <summary>SFC / DISM / MRT gibi, kendi eylem butonu bir Windows bakım aracını çalıştıran kartlar.</summary>
    public bool IsMaintenance { get; init; }

    /// <summary>Kartın kullandığı gerçek Windows mekanizması / komutu (kısa etiket).</summary>
    public string CommandText { get; init; } = string.Empty;

    /// <summary>Kartın ne yaptığını teknik bilgi gerektirmeden anlatan 1-2 satırlık açıklama.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>"?" bilgi butonunda gösterilen metin.</summary>
    public string InfoText { get; init; } = string.Empty;

    public string ActionText { get; init; } = "Kontrol Et";
    public string ActionGlyph { get; init; } = "";
    public ICommand? ActionCommand { get; set; }

    /// <summary>Detaylı Sonuç panelini açar.</summary>
    public ICommand? DetailsCommand { get; set; }

    /// <summary>Seçim durumunu merkezi seçim yöneticisine ileten geri çağırım.</summary>
    public Action<string, bool>? SelectionChangedCallback { get; set; }

    public ComponentStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(HealthText));
            }
        }
    }

    public string Summary
    {
        get => _summary;
        set
        {
            if (Set(ref _summary, value))
                OnPropertyChanged(nameof(HealthText));
        }
    }

    public string Details { get => _details; set => Set(ref _details, value); }
    public string? Reason { get => _reason; set => Set(ref _reason, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && IsUnavailable) return;
            if (Set(ref _isSelected, value))
                SelectionChangedCallback?.Invoke(Key, value);
        }
    }

    /// <summary>
    /// Kart bu sistemde kullanım dışı mı (ör. NVIDIA RTX ekran kartı yok). Kullanım dışı kart seçilemez, çalıştırılamaz
    /// ve toplu işlemlere (Tümünü Kontrol Et / Güncelle) dahil edilmez.
    /// </summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>
    /// Kartı kalıcı olarak (oturum boyunca) kullanım dışı yapar; <paramref name="reason"/> kartta gösterilir.
    /// Birden fazla neden varsa (ör. RTX yok + yönetici yetkisi yok) hepsi sırayla yazılır.
    /// </summary>
    public void MarkUnavailable(string reason)
    {
        IsUnavailable = true;
        OnPropertyChanged(nameof(IsUnavailable));
        _unavailableReason = _unavailableReason is null ? reason : _unavailableReason + " " + reason;
        if (_isSelected)
        {
            _isSelected = false;
            OnPropertyChanged(nameof(IsSelected));
            SelectionChangedCallback?.Invoke(Key, false);
        }
        Reset();
    }

    /// <summary>Seçim yöneticisinden gelen değişikliği geri çağırım tetiklemeden uygular.</summary>
    public void SyncSelected(bool selected)
    {
        if (selected && IsUnavailable) return;
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

    // ------------------------------------------------------------ son çalıştırılma

    /// <summary>Kartın son GERÇEK sonucu (bu oturumdan veya kayıtlı geçmişten).</summary>
    public CardSnapshot? LastSnapshot => _lastSnapshot;

    /// <summary>Son sonuç önceki bir oturumdan mı geliyor?</summary>
    public bool IsSnapshotFromHistory => _snapshotFromHistory;

    public bool HasLastRun => _lastSnapshot is not null;

    /// <summary>"Son kontrol: 23.09.2026 19:42" – gerçek bitiş zamanından.</summary>
    public string LastRunText => _lastSnapshot is null
        ? string.Empty
        : $"{LastRunLabel(Key, _lastSnapshot.Operation)}: {_lastSnapshot.CompletedAt:dd.MM.yyyy HH:mm}" +
          (_snapshotFromHistory ? " · önceki oturum" : string.Empty);

    /// <summary>İşlem türüne uygun "son ..." ifadesi.</summary>
    public static string LastRunLabel(string key, OperationKind op) => (key, op) switch
    {
        (ComponentKeys.Sfc or ComponentKeys.Mrt, _) => "Son tarama",
        (ComponentKeys.RecycleBin, OperationKind.Update) => "Son temizlik",
        (_, OperationKind.Update) => "Son güncelleme",
        _ => "Son kontrol"
    };

    /// <summary>Uygulama açılırken kayıtlı geçmişten son sonucu yükler (durum "çalıştırılmadı" olarak kalır).</summary>
    public void LoadHistory(CardSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _snapshotFromHistory = true;
        if (Status == ComponentStatus.NotChecked && !IsUnavailable) Summary = NotRunThisSessionText;
        RaiseLastRunChanged();
    }

    // ------------------------------------------------------------ sağlık özeti

    /// <summary>Sistem Sağlık Özeti'nde gösterilen kısa ve gerçek duruma dayanan metin.</summary>
    public string HealthText => Status switch
    {
        ComponentStatus.NotChecked => Key is ComponentKeys.Sfc or ComponentKeys.Mrt ? "Taranmadı" : "Kontrol edilmedi",
        ComponentStatus.Checking or ComponentStatus.Updating => "Çalışıyor...",
        ComponentStatus.UpToDate => Key switch
        {
            ComponentKeys.Sfc or ComponentKeys.Dism => "Sağlıklı",
            ComponentKeys.Mrt => "Tehdit bulunmadı",
            ComponentKeys.RecycleBin => "Boş",
            _ => "Güncel"
        },
        ComponentStatus.Updated => Key switch
        {
            ComponentKeys.RecycleBin => "Temizlendi",
            ComponentKeys.Sfc => "Onarıldı",
            ComponentKeys.Mrt => "Temizlendi",
            _ => "Güncellendi"
        },
        ComponentStatus.UpdateAvailable or ComponentStatus.Attention => Summary,
        ComponentStatus.PartiallyUpdated => "Kısmen tamamlandı",
        ComponentStatus.RebootRequired => "Yeniden başlatma gerekli",
        ComponentStatus.AdminRequired => "Yönetici izni gerekli",
        ComponentStatus.CheckFailed => "Kontrol edilemedi",
        ComponentStatus.Failed => "Başarısız",
        ComponentStatus.Skipped => "Atlandı",
        ComponentStatus.Unavailable => UnavailableText,
        _ => Summary
    };

    // ------------------------------------------------------------ durum güncelleme

    public void Apply(ModuleResult r, CardSnapshot? snapshot = null)
    {
        if (IsUnavailable) return; // kullanım dışı kart hiçbir işleme girmez
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

            snapshot ??= CardSnapshot.From(r);
            if (snapshot is not null)
            {
                _lastSnapshot = snapshot;
                _snapshotFromHistory = false;
                RaiseLastRunChanged();
            }
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
        Status = IsUnavailable ? ComponentStatus.Unavailable : ComponentStatus.NotChecked;
        Summary = IsUnavailable ? UnavailableText : _snapshotFromHistory ? NotRunThisSessionText : NotCheckedText;
        Details = string.Empty;
        Reason = IsUnavailable ? _unavailableReason : null;
        LastResult = null;
        ActivityText = string.Empty;
        Progress = 0;
        ProgressKnown = false;
    }

    private void RaiseLastRunChanged()
    {
        OnPropertyChanged(nameof(LastSnapshot));
        OnPropertyChanged(nameof(HasLastRun));
        OnPropertyChanged(nameof(LastRunText));
        OnPropertyChanged(nameof(IsSnapshotFromHistory));
        OnPropertyChanged(nameof(HealthText));
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

using System.Collections.ObjectModel;
using System.Windows.Input;
using RtxWindowsUpdater.Models;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Detaylı Sonuç panelinde tek bir çalıştırılmış komut.</summary>
public sealed class CommandDetailViewModel
{
    public required string Command { get; init; }
    public required string ExitCodeText { get; init; }
    public required bool ExitCodeKnown { get; init; }
    public required string DurationText { get; init; }
    public required string StartedText { get; init; }
    public required string Output { get; init; }
    public string Error { get; init; } = string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

/// <summary>Detaylı Sonuç panelinde tek bir paketin (veya onarımın) GERÇEK güncelleme sonucu.</summary>
public sealed class PackageResultViewModel
{
    public required string Name { get; init; }
    public required string VersionText { get; init; }
    public required string OutcomeLabel { get; init; }
    public required ComponentStatus Status { get; init; }
    public string? ReasonText { get; init; }
    public string? CodeText { get; init; }
    public string? InstallerExitCodeText { get; init; }
    public string? ToolMessageText { get; init; }
    public string? BlockingText { get; init; }
    public string? DetailText { get; init; }

    public bool HasReason => !string.IsNullOrWhiteSpace(ReasonText);
    public bool HasCode => !string.IsNullOrWhiteSpace(CodeText);
    public bool HasInstallerExitCode => !string.IsNullOrWhiteSpace(InstallerExitCodeText);
    public bool HasToolMessage => !string.IsNullOrWhiteSpace(ToolMessageText);
    public bool HasBlocking => !string.IsNullOrWhiteSpace(BlockingText);
    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);
}

/// <summary>
/// Bir kartın son gerçek sonucunu (komutlar, çıkış kodları, stdout/stderr, süre, öğeler) gösteren panel.
/// Yalnızca kayıtlı verileri gösterir; olmayan bilgi "Bilgi alınamadı" olarak yazılır.
/// </summary>
public sealed class DetailViewModel : ObservableObject
{
    public static string NotAvailable => L.T("Bilgi alınamadı", "Information unavailable");

    private bool _isOpen;
    private string _title = string.Empty;
    private string _glyph = string.Empty;
    private ComponentStatus _status;
    private string _statusText = string.Empty;
    private bool _hasData;
    private string _operationText = string.Empty;
    private string _completedText = string.Empty;
    private string _durationText = string.Empty;
    private string _sourceText = string.Empty;
    private string _actionableText = string.Empty;
    private string _details = string.Empty;
    private string? _reason;

    public DetailViewModel()
    {
        CloseCommand = new RelayCommand(() => IsOpen = false);
    }

    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public ComponentStatus Status { get => _status; private set => Set(ref _status, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }
    public string OperationText { get => _operationText; private set => Set(ref _operationText, value); }
    public string CompletedText { get => _completedText; private set => Set(ref _completedText, value); }
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }
    public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }
    public string ActionableText { get => _actionableText; private set => Set(ref _actionableText, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }
    public string? Reason { get => _reason; private set => Set(ref _reason, value); }

    public ObservableCollection<CommandDetailViewModel> Commands { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<UpdateRowViewModel> Items { get; } = [];
    public ObservableCollection<PackageResultViewModel> PackageResults { get; } = [];

    public bool HasCommands => Commands.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasItems => Items.Count > 0;
    public bool HasPackageResults => PackageResults.Count > 0;
    public string PackageResultsTitle { get => _packageResultsTitle; private set => Set(ref _packageResultsTitle, value); }
    private string _packageResultsTitle = L.T("PAKET BAZLI SONUÇLAR", "PER-PACKAGE RESULTS");

    public ICommand CloseCommand { get; }

    public void Show(ComponentCardViewModel card)
    {
        Title = card.Title;
        Glyph = card.Glyph;
        Commands.Clear();
        Notes.Clear();
        Items.Clear();
        PackageResults.Clear();
        PackageResultsTitle = card.Key == ComponentKeys.Dism ? L.T("ONARIM SONUCU", "REPAIR RESULT") : L.T("PAKET BAZLI SONUÇLAR", "PER-PACKAGE RESULTS");

        var s = card.LastSnapshot;
        HasData = s is not null;
        if (s is null)
        {
            Status = card.Status;
            StatusText = card.Summary;
            OperationText = CompletedText = DurationText = ActionableText = string.Empty;
            SourceText = L.T("Bu kart henüz hiç çalıştırılmadı; gösterilecek gerçek sonuç yok.", "This card has never been run yet; there is no real result to show.");
            Details = string.Empty;
            Reason = null;
        }
        else
        {
            Status = s.Status;
            StatusText = s.Summary;
            OperationText = s.Operation switch
            {
                OperationKind.Check => card.Key is ComponentKeys.Sfc ? L.T("Doğrulama taraması (sfc /verifyonly)", "Verification scan (sfc /verifyonly)") :
                                       card.Key is ComponentKeys.Mrt ? L.T("Hızlı tarama (yalnızca tespit)", "Quick scan (detection only)") : L.T("Kontrol", "Check"),
                OperationKind.Update => card.Key switch
                {
                    ComponentKeys.RecycleBin or ComponentKeys.TempFiles => L.T("Temizlik", "Cleanup"),
                    ComponentKeys.Dism => L.T("Onarım (DISM /RestoreHealth) ve doğrulama", "Repair (DISM /RestoreHealth) and verification"),
                    ComponentKeys.Sfc => L.T("Tarama ve onarım (sfc /scannow)", "Scan and repair (sfc /scannow)"),
                    ComponentKeys.Mrt => L.T("Hızlı tarama (temizleme)", "Quick scan (removal)"),
                    _ => L.T("Güncelleme", "Update")
                },
                _ => card.Key switch
                {
                    ComponentKeys.Sfc => L.T("Tarama ve onarım (sfc /scannow)", "Scan and repair (sfc /scannow)"),
                    ComponentKeys.Dism => L.T("Sağlık kontrolü (CheckHealth)", "Health check (CheckHealth)"),
                    ComponentKeys.Mrt => L.T("Hızlı tarama (yalnızca tespit)", "Quick scan (detection only)"),
                    _ => L.T("Kart işlemi", "Card operation")
                }
            };
            CompletedText = s.CompletedAt.ToString(L.T("dd.MM.yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss"));
            DurationText = s.DurationMs > 0
                ? UpdateOrchestrator.FormatDuration(TimeSpan.FromMilliseconds(s.DurationMs))
                : NotAvailable;
            SourceText = card.IsSnapshotFromHistory
                ? L.T("Önceki oturumdan kaydedilen gerçek sonuç (bu oturumda henüz çalıştırılmadı).", "Real result saved from the previous session (not run in this session yet).")
                : L.T("Bu oturumdaki son gerçek sonuç.", "The latest real result in this session.");
            ActionableText = s.ActionableCount > 0 ? s.ActionableCount.ToString() : "0";
            Details = s.Details;
            Reason = s.Reason;

            foreach (var c in s.Commands)
            {
                Commands.Add(new CommandDetailViewModel
                {
                    Command = c.Command,
                    ExitCodeKnown = c.ExitCode is not null,
                    ExitCodeText = c.ExitCode is { } code
                        ? $"{code} (0x{unchecked((uint)code):X8})"
                        : NotAvailable + (c.StartError is not null ? L.T($" – başlatılamadı: {c.StartError}", $" – could not start: {c.StartError}")
                                          : c.TimedOut ? L.T(" – zaman aşımı", " – timed out") : c.Cancelled ? L.T(" – iptal edildi", " – cancelled") : string.Empty),
                    DurationText = UpdateOrchestrator.FormatDuration(TimeSpan.FromMilliseconds(c.DurationMs)),
                    StartedText = c.StartedAt.ToString("HH:mm:ss"),
                    Output = string.IsNullOrWhiteSpace(c.StdOut) ? L.T("(standart çıktı boş)", "(standard output empty)") : c.StdOut,
                    Error = c.StdErr
                });
            }
            foreach (var n in s.Notes) Notes.Add(n);

            // Paket bazlı gerçek sonuçlar (Winget / Microsoft Store paketleri, DISM onarımı).
            if (card.Key is ComponentKeys.Winget or ComponentKeys.Store or ComponentKeys.Dism)
            {
                foreach (var i in s.Items.Where(i => i.Outcome is not null)
                             .OrderBy(i => i.Outcome == ItemOutcome.Failed ? 0 : i.Outcome == ItemOutcome.Unverified ? 1 : 2))
                    PackageResults.Add(ToPackageResult(i));
            }
            foreach (var i in s.Items.OrderByDescending(i => i.UpdateAvailable))
            {
                Items.Add(new UpdateRowViewModel
                {
                    Category = card.ShortTitle,
                    Name = i.Name,
                    CurrentVersion = string.IsNullOrWhiteSpace(i.CurrentVersion) ? "—" : i.CurrentVersion,
                    NewVersion = string.IsNullOrWhiteSpace(i.NewVersion) ? "—" : i.NewVersion,
                    Status = i.StatusText,
                    UpdateAvailable = i.UpdateAvailable
                });
            }
        }

        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasPackageResults));
        IsOpen = true;
    }

    private static PackageResultViewModel ToPackageResult(ItemSnapshot i)
    {
        var (label, status) = i.Outcome switch
        {
            ItemOutcome.Updated => (L.T("Başarılı", "Succeeded"), ComponentStatus.Updated),
            ItemOutcome.UpdatedReboot => (L.T("Başarılı – yeniden başlatma gerekli", "Succeeded – restart required"), ComponentStatus.RebootRequired),
            ItemOutcome.Unverified => (L.T("Doğrulanamadı", "Could not be verified"), ComponentStatus.PartiallyUpdated),
            _ => (L.T("Başarısız", "Failed"), ComponentStatus.Failed)
        };
        var code = string.IsNullOrWhiteSpace(i.ResultCode) ? null
            : string.IsNullOrWhiteSpace(i.ResultSymbol) ? i.ResultCode : $"{i.ResultCode} · {i.ResultSymbol}";
        var reason = i.OutcomeText is { } t && !t.Equals(L.T("Güncellendi", "Updated"), StringComparison.Ordinal) ? t : null;
        return new PackageResultViewModel
        {
            Name = i.Name,
            VersionText = string.IsNullOrWhiteSpace(i.CurrentVersion) && string.IsNullOrWhiteSpace(i.NewVersion)
                ? "—"
                : $"{(string.IsNullOrWhiteSpace(i.CurrentVersion) ? "—" : i.CurrentVersion)} → {(string.IsNullOrWhiteSpace(i.NewVersion) ? "—" : i.NewVersion)}",
            OutcomeLabel = label,
            Status = status,
            ReasonText = reason,
            CodeText = code,
            InstallerExitCodeText = i.InstallerExitCode,
            ToolMessageText = i.ToolMessage,
            BlockingText = i.BlockingProcesses.Count > 0 ? string.Join(", ", i.BlockingProcesses) : null,
            DetailText = i.Outcome is ItemOutcome.Failed or ItemOutcome.Unverified ? i.StatusText : null
        };
    }
}

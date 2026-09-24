using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>Bir kartın son gerçek sonucunun kalıcı kopyası (Detaylı Sonuç paneli ve "Son kontrol" bilgisi).</summary>
public sealed class CardSnapshot
{
    private const int MaxOutputChars = 4000;
    private const int MaxItems = 300;

    public string Key { get; set; } = string.Empty;
    public OperationKind Operation { get; set; }
    public DateTime CompletedAt { get; set; }
    public long DurationMs { get; set; }
    public ComponentStatus Status { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public int ActionableCount { get; set; }
    public List<ItemSnapshot> Items { get; set; } = [];
    public List<CommandSnapshot> Commands { get; set; } = [];
    public List<string> Notes { get; set; } = [];

    /// <summary>Yalnızca gerçekten tamamlanmış (bitiş zamanı olan) sonuçlardan anlık görüntü üretir.</summary>
    public static CardSnapshot? From(ModuleResult r)
    {
        if (r.CompletedAt is null) return null;
        return new CardSnapshot
        {
            Key = r.Key,
            Operation = r.Operation,
            CompletedAt = r.CompletedAt.Value,
            DurationMs = (long)(r.Duration?.TotalMilliseconds ?? 0),
            Status = r.Status,
            Summary = r.Summary,
            Details = r.Details,
            Reason = r.Reason,
            ActionableCount = r.ActionableCount,
            Items = r.Items.Take(MaxItems).Select(i => new ItemSnapshot
            {
                Name = i.Name,
                CurrentVersion = i.CurrentVersion,
                NewVersion = i.NewVersion,
                StatusText = i.StatusText,
                UpdateAvailable = i.UpdateAvailable,
                Outcome = i.Outcome,
                OutcomeText = i.OutcomeText,
                ResultCode = i.ResultCode,
                ResultSymbol = i.ResultSymbol,
                InstallerExitCode = i.InstallerExitCode,
                ToolMessage = i.ToolMessage,
                BlockingProcesses = i.BlockingProcesses.Select(p => p.DisplayText).ToList()
            }).ToList(),
            Commands = r.Commands.Select(c => new CommandSnapshot
            {
                Command = c.Command,
                StartedAt = c.StartedAt,
                DurationMs = (long)c.Duration.TotalMilliseconds,
                ExitCode = c.ExitCode,
                StdOut = Tail(c.StdOut),
                StdErr = Tail(c.StdErr),
                TimedOut = c.TimedOut,
                Cancelled = c.Cancelled,
                StartError = c.StartError
            }).ToList(),
            Notes = r.Notes.ToList()
        };
    }

    private static string Tail(string s) =>
        s.Length <= MaxOutputChars ? s : "… (baştaki kısım kısaltıldı)\n" + s[^MaxOutputChars..];
}

public sealed class ItemSnapshot
{
    public string Name { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }

    // Paket bazlı gerçek sonuç (güncelleme denendiyse).
    public ItemOutcome? Outcome { get; set; }
    public string? OutcomeText { get; set; }
    public string? ResultCode { get; set; }
    public string? ResultSymbol { get; set; }
    public string? InstallerExitCode { get; set; }
    public string? ToolMessage { get; set; }
    public List<string> BlockingProcesses { get; set; } = [];
}

public sealed class CommandSnapshot
{
    public string Command { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public long DurationMs { get; set; }
    public int? ExitCode { get; set; }
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public string? StartError { get; set; }
}

/// <summary>Tamamlanan bir işlemin (tek kart veya toplu) gerçek sonuçlardan hesaplanmış özeti.</summary>
public sealed class OperationRecord
{
    public DateTime CompletedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SummaryText { get; set; } = string.Empty;
    public List<string> Keys { get; set; } = [];
    public int Completed { get; set; }
    public int Updates { get; set; }
    public int Warnings { get; set; }
    public int Errors { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public bool Cancelled { get; set; }

    [JsonIgnore] public string TimeText => CompletedAt.ToString("dd.MM.yyyy HH:mm");
    [JsonIgnore] public string DurationText => UpdateOrchestrator.FormatDuration(TimeSpan.FromMilliseconds(DurationMs));

    /// <summary>Özet rengi için durum (hata > uyarı/güncelleme > başarılı).</summary>
    [JsonIgnore]
    public ComponentStatus Status =>
        Cancelled ? ComponentStatus.Skipped
        : Errors > 0 ? ComponentStatus.Failed
        : Warnings > 0 || Updates > 0 ? ComponentStatus.UpdateAvailable
        : Completed > 0 ? ComponentStatus.UpToDate
        : ComponentStatus.NotChecked;
}

public sealed class AppState
{
    public bool NotificationsEnabled { get; set; } = true;
    public Dictionary<string, CardSnapshot> Cards { get; set; } = new();
    public List<OperationRecord> Recent { get; set; } = [];

    /// <summary>Winget'in 0x8A15008E döndürdüğü paketler ("kaynak|Id" → hedef sürüm); yeniden denemeyi önlemek için.</summary>
    public Dictionary<string, string> WingetTechnologyMismatch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Uygulama durumunu %LOCALAPPDATA%\E-mre Hub\state.json dosyasında saklar.
/// Kaydetme arka planda yapılır; dosya okunamaz/yazılamazsa uygulama çalışmaya devam eder ve nedeni günlüğe yazılır.
/// Yeni klasörde henüz state.json yoksa eski addaki (RTX Windows Updater) geçmiş bir kez KOPYALANIR; eski dosya silinmez.
/// </summary>
public sealed class AppStateStore
{
    private const int MaxRecent = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Logger _logger;
    private readonly object _lock = new();
    private readonly string _path;
    private int _saveVersion;

    public AppState State { get; }

    public AppStateStore(Logger logger) : this(logger, AppInfo.DataDirectory, AppInfo.LegacyDataDirectory)
    {
    }

    internal AppStateStore(Logger logger, string dataDirectory, string? legacyDataDirectory)
    {
        _logger = logger;
        _path = Path.Combine(dataDirectory, "state.json");
        if (legacyDataDirectory is not null) CopyLegacyState(Path.Combine(legacyDataDirectory, "state.json"));
        State = Load();
    }

    /// <summary>Ad değişikliğinden önceki geçmişi yeni klasöre taşımadan kopyalar (yalnızca yeni klasörde geçmiş yoksa).</summary>
    private void CopyLegacyState(string legacyPath)
    {
        try
        {
            if (File.Exists(_path) || !File.Exists(legacyPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.Copy(legacyPath, _path, overwrite: false);
            _logger.Info($"Önceki sürümün işlem geçmişi kopyalandı: {legacyPath} → {_path} (eski dosya silinmedi).");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Önceki sürümün işlem geçmişi kopyalanamadı ({legacyPath}): {ex.Message}");
        }
    }

    private AppState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppState();
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), JsonOptions) ?? new AppState();
            state.Cards ??= new();
            state.Recent ??= [];
            state.WingetTechnologyMismatch ??= new(StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Kayıtlı işlem geçmişi okunamadı, yeni geçmiş başlatılıyor: {ex.Message}");
            return new AppState();
        }
    }

    public void SetCard(CardSnapshot snapshot)
    {
        lock (_lock) State.Cards[snapshot.Key] = snapshot;
        SaveInBackground();
    }

    public void AddOperation(OperationRecord record)
    {
        lock (_lock)
        {
            State.Recent.Insert(0, record);
            if (State.Recent.Count > MaxRecent) State.Recent.RemoveRange(MaxRecent, State.Recent.Count - MaxRecent);
        }
        SaveInBackground();
    }

    /// <summary>Winget uyuşmazlık kayıtlarını yalnızca değiştiyse kaydeder.</summary>
    public void SetWingetTechnologyMismatch(IReadOnlyDictionary<string, string> entries)
    {
        lock (_lock)
        {
            var current = State.WingetTechnologyMismatch;
            if (current.Count == entries.Count &&
                entries.All(e => current.TryGetValue(e.Key, out var v) && string.Equals(v, e.Value, StringComparison.OrdinalIgnoreCase)))
                return;
            State.WingetTechnologyMismatch = new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase);
        }
        SaveInBackground();
    }

    public void SetNotificationsEnabled(bool enabled)
    {
        lock (_lock) State.NotificationsEnabled = enabled;
        SaveInBackground();
    }

    private void SaveInBackground()
    {
        var version = Interlocked.Increment(ref _saveVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(300); // art arda gelen değişiklikleri tek yazıma topla
            if (version != Volatile.Read(ref _saveVersion)) return;
            try
            {
                string json;
                lock (_lock) json = JsonSerializer.Serialize(State, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.Warning($"İşlem geçmişi kaydedilemedi: {ex.Message}");
            }
        });
    }
}

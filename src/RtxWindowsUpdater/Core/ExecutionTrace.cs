using System.Text;
using System.Text.RegularExpressions;

namespace RtxWindowsUpdater.Core;

/// <summary>Bir modül işlemi sırasında gerçekten çalıştırılmış tek bir komutun kaydı.</summary>
public sealed class CommandRecord
{
    public string Command { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Süreç başlatılamadıysa, zaman aşımı/iptalde null.</summary>
    public int? ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public string? StartError { get; init; }
}

/// <summary>
/// Bir modül işlemi (kontrol / güncelleme / tarama) sırasında çalıştırılan komutları ve
/// önemli gerçek ara sonuçları toplar. <see cref="AsyncLocal{T}"/> sayesinde modül kodunu
/// değiştirmeden, orkestratörün başlattığı işlem akışındaki tüm ProcessRunner çağrıları kaydedilir.
/// </summary>
public sealed class ExecutionTrace
{
    private const int MaxOutputChars = 8000;
    private static readonly AsyncLocal<ExecutionTrace?> CurrentTrace = new();

    private readonly object _lock = new();
    private readonly List<CommandRecord> _commands = [];
    private readonly List<string> _notes = [];

    public static ExecutionTrace Begin()
    {
        var trace = new ExecutionTrace();
        CurrentTrace.Value = trace;
        return trace;
    }

    public static void End() => CurrentTrace.Value = null;

    public IReadOnlyList<CommandRecord> Commands { get { lock (_lock) return _commands.ToList(); } }
    public IReadOnlyList<string> Notes { get { lock (_lock) return _notes.ToList(); } }

    /// <summary>Etkin bir iz varsa komut sonucunu kaydeder (yoksa hiçbir şey yapmaz).</summary>
    public static void Record(string command, DateTime startedAt, TimeSpan duration, ProcessResult r)
    {
        var trace = CurrentTrace.Value;
        if (trace is null) return;
        var rec = new CommandRecord
        {
            Command = command,
            StartedAt = startedAt,
            Duration = duration,
            ExitCode = r.Started && !r.TimedOut && !r.Cancelled ? r.ExitCode : null,
            StdOut = CleanOutput(r.StdOut),
            StdErr = CleanOutput(r.StdErr),
            TimedOut = r.TimedOut,
            Cancelled = r.Cancelled,
            StartError = r.StartError
        };
        lock (trace._lock) trace._commands.Add(rec);
    }

    /// <summary>Komut dışı gerçek bir ara sonucu (HTTP yanıtı, API sonucu vb.) kaydeder.</summary>
    public static void Note(string text)
    {
        var trace = CurrentTrace.Value;
        if (trace is null || string.IsNullOrWhiteSpace(text)) return;
        lock (trace._lock) trace._notes.Add(text.Trim());
    }

    private static readonly Regex ProgressLine = new(@"^\D{0,40}\d{1,3}(?:\.\d)?\s*%\D{0,40}$", RegexOptions.Compiled);

    /// <summary>
    /// Çıktıyı panelde okunabilir hâle getirir: ilerleme çubuğu satırları ayıklanır (ardışık yüzde
    /// satırlarından yalnızca sonuncusu kalır), PowerShell protokol önekleri kaldırılır, sonu korunarak kısaltılır.
    /// </summary>
    public static string CleanOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var lines = new List<string>();
        string? pendingProgress = null;
        foreach (var raw in text.Replace("\0", string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var idx = raw.LastIndexOf('\r');
            var line = (idx >= 0 ? raw[(idx + 1)..] : raw).TrimEnd();
            if (line.Trim().Length == 0) continue;
            if (line.Contains('█') || line.Contains('▒')) continue;
            if (line.Trim().All(c => c is '-' or '\\' or '|' or '/' or ' ') && line.Trim().Length < 3) continue;

            if (line.StartsWith("##LOG|", StringComparison.Ordinal)) line = line[6..];
            else if (line.StartsWith("##RESULT|", StringComparison.Ordinal)) line = "Sonuç (JSON): " + line[9..];

            var trimmed = line.Trim();
            if (ProgressLine.IsMatch(trimmed) || (trimmed.StartsWith('[') && trimmed.Contains('%') && trimmed.Contains('=')))
            {
                pendingProgress = trimmed;
                continue;
            }
            if (pendingProgress is not null)
            {
                lines.Add(pendingProgress);
                pendingProgress = null;
            }
            lines.Add(line);
        }
        if (pendingProgress is not null) lines.Add(pendingProgress);

        var sb = new StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        var result = sb.ToString().TrimEnd();
        return result.Length <= MaxOutputChars
            ? result
            : "… (baştaki kısım kısaltıldı)\n" + result[^MaxOutputChars..];
    }
}

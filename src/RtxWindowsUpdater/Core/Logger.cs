using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace RtxWindowsUpdater.Core;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Output
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>Günlük seviyesi etiketi. Araç çıktısı (Output) bilgi seviyesindedir.</summary>
    public string LevelText => LevelLabel(Level);

    public override string ToString() => $"[{TimeText}] [{LevelText}] {Message}";

    public static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Success => "SUCCESS",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };
}

/// <summary>
/// Thread-safe uygulama günlüğü. Her kayıt hem arayüze (LogAdded olayı) hem de
/// %LOCALAPPDATA%\E-mre Hub\Logs altındaki oturum dosyasına yazılır.
/// Dosyaya yazma arka plandaki tek bir yazıcı iş parçacığında yapılır; çağıran (örn. arayüz)
/// iş parçacığı disk G/Ç'si nedeniyle asla beklemez.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writer;
    private readonly object _fileLock = new();
    private volatile bool _fileWritable = true;
    private int _pending;

    public event Action<LogEntry>? LogAdded;

    public string LogFilePath { get; }

    /// <summary>Günlük klasörü (%LOCALAPPDATA%\E-mre Hub\Logs).</summary>
    public string LogDirectory => Path.GetDirectoryName(LogFilePath)!;

    /// <summary>Oturum günlük dosyasının diske yazılabildiği bilgisi.</summary>
    public bool IsFileAvailable => _fileWritable && File.Exists(LogFilePath);

    public Logger()
    {
        var dir = Path.Combine(AppInfo.DataDirectory, "Logs");
        LogFilePath = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            _fileWritable = false;
        }

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "RTX Updater log writer" };
        _writer.Start();
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Success(string message) => Write(LogLevel.Success, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Output(string message) => Write(LogLevel.Output, message);

    public void Write(LogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var entry = new LogEntry(DateTime.Now, level, message.TrimEnd());
        if (_fileWritable && !_queue.IsAddingCompleted)
        {
            Interlocked.Increment(ref _pending);
            try
            {
                _queue.Add($"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] {entry.Message}{Environment.NewLine}");
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pending);
            }
        }

        try
        {
            LogAdded?.Invoke(entry);
        }
        catch
        {
            // Bir UI aboneliği hatası log üretimini asla durdurmamalı.
        }
    }

    /// <summary>Kuyruktaki tüm satırların dosyaya yazılmasını bekler (en fazla verilen süre kadar).</summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(15);
        return Volatile.Read(ref _pending) == 0;
    }

    /// <summary>Oturum günlük dosyasını (tüm satırlar yazıldıktan sonra) başka bir konuma kopyalar.</summary>
    public void ExportTo(string destination)
    {
        Flush(TimeSpan.FromSeconds(3));
        lock (_fileLock)
        {
            File.Copy(LogFilePath, destination, overwrite: true);
        }
    }

    private void WriterLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            var sb = new StringBuilder(line);
            var count = 1;
            // Birikmiş satırları tek seferde yaz.
            while (count < 500 && _queue.TryTake(out var more))
            {
                sb.Append(more);
                count++;
            }

            if (_fileWritable)
            {
                lock (_fileLock)
                {
                    try
                    {
                        File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
                    }
                    catch
                    {
                        _fileWritable = false;
                    }
                }
            }
            Interlocked.Add(ref _pending, -count);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
    }
}

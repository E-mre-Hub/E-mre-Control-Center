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
    public override string ToString() => $"[{TimeText}] {Message}";
}

/// <summary>
/// Thread-safe uygulama günlüğü. Her kayıt hem arayüze (LogAdded olayı) hem de
/// %LOCALAPPDATA%\RTX Windows Updater\Logs altındaki oturum dosyasına yazılır.
/// </summary>
public sealed class Logger
{
    private readonly object _fileLock = new();
    private bool _fileWritable = true;

    public event Action<LogEntry>? LogAdded;

    public string LogFilePath { get; }

    public Logger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RTX Windows Updater", "Logs");
        LogFilePath = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            _fileWritable = false;
        }
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
        AppendToFile(entry);

        try
        {
            LogAdded?.Invoke(entry);
        }
        catch
        {
            // Bir UI aboneliği hatası log üretimini asla durdurmamalı.
        }
    }

    private void AppendToFile(LogEntry entry)
    {
        if (!_fileWritable)
            return;

        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(LogFilePath,
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                _fileWritable = false;
            }
        }
    }
}

using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Günlük arşivi (Genel Ayarlar → Günlük Dosyaları): uygulama her açılışta yeni bir günlük oluşturur ve eskiler kendiliğinden silinmez.
/// Kullanıcı 30 günden eskileri onaylı bir düğmeyle silebilir veya "açılışta otomatik sil" ayarını açabilir (varsayılan KAPALI:
/// kullanıcıdan habersiz dosya silinmez). Açık oturumun günlüğü hiçbir zaman silinmez.
/// </summary>
public sealed partial class MainViewModel
{
    private string _logArchiveText = L.T("Günlükler okunuyor…", "Reading logs…");
    private int _oldLogCount;

    public string LogArchiveText { get => _logArchiveText; private set => Set(ref _logArchiveText, value); }

    public bool AutoDeleteOldLogs
    {
        get => _state.State.AutoDeleteOldLogs;
        set
        {
            if (_state.State.AutoDeleteOldLogs == value) return;
            _state.SetAutoDeleteOldLogs(value);
            _logger.Info(value
                ? L.T($"Ayar: açılışta {LogArchive.RetentionDays} günden eski günlükler silinecek.", $"Setting: logs older than {LogArchive.RetentionDays} days will be deleted at startup.")
                : L.T("Ayar: eski günlükler otomatik silinmeyecek.", "Setting: old logs will not be deleted automatically."));
            OnPropertyChanged();
        }
    }

    public ICommand DeleteOldLogsCommand { get; private set; } = null!;

    private void InitializeLogArchive() =>
        DeleteOldLogsCommand = new AsyncCommand(DeleteOldLogsAsync, () => _oldLogCount > 0, OnCommandError);

    /// <summary>Günlük klasörünün gerçek özeti (sayı, boyut, en eski, silinebilir).</summary>
    public void RefreshLogArchive()
    {
        var s = LogArchive.Read(_logger.LogDirectory, _logger.LogFilePath, LogArchive.DefaultCutoff());
        _oldLogCount = s.OldCount;
        LogArchiveText = s.Count == 0
            ? L.T("Günlük dosyası yok.", "No log files.")
            : L.T($"{s.Count} günlük dosyası · {TemporaryFilesManager.FormatSize(s.Bytes)} · en eski {s.Oldest:dd.MM.yyyy}\n", $"{s.Count} log file(s) · {TemporaryFilesManager.FormatSize(s.Bytes)} · oldest {s.Oldest:yyyy-MM-dd}\n") +
              (s.OldCount > 0
                  ? L.T($"{LogArchive.RetentionDays} günden eski: {s.OldCount} dosya ({TemporaryFilesManager.FormatSize(s.OldBytes)})", $"Older than {LogArchive.RetentionDays} days: {s.OldCount} file(s) ({TemporaryFilesManager.FormatSize(s.OldBytes)})")
                  : L.T($"{LogArchive.RetentionDays} günden eski günlük yok.", $"No logs older than {LogArchive.RetentionDays} days."));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DeleteOldLogsAsync()
    {
        var cutoff = LogArchive.DefaultCutoff();
        var s = LogArchive.Read(_logger.LogDirectory, _logger.LogFilePath, cutoff);
        if (s.OldCount == 0)
        {
            RefreshLogArchive();
            return;
        }
        var ok = await Dialog.ShowAsync(L.T("Eski günlükleri sil", "Delete old logs"),
            L.T($"{LogArchive.RetentionDays} günden eski {s.OldCount} günlük dosyası ({TemporaryFilesManager.FormatSize(s.OldBytes)}) kalıcı olarak silinecek. ", $"{s.OldCount} log file(s) older than {LogArchive.RetentionDays} days ({TemporaryFilesManager.FormatSize(s.OldBytes)}) will be deleted permanently. ") +
            L.T("Son 30 günün günlükleri ve bu oturumun günlüğü korunur; işlem geçmişi ve ayarlar (state.json) etkilenmez.", "The logs of the last 30 days and this session's log are kept; the operation history and settings (state.json) are not affected."),
            Icons.Warning, DialogKind.Warning, L.T("Sil", "Delete"), L.T("Vazgeç", "Cancel"));
        if (!ok) return;
        var r = LogArchive.DeleteOld(_logger.LogDirectory, _logger.LogFilePath, cutoff);
        _logger.Info(L.T($"Eski günlükler silindi: {r.Deleted} dosya ({TemporaryFilesManager.FormatSize(r.Bytes)})", $"Old logs deleted: {r.Deleted} file(s) ({TemporaryFilesManager.FormatSize(r.Bytes)})") +
                     (r.Failed > 0 ? L.T($"; {r.Failed} dosya silinemedi (kullanımda olabilir).", $"; {r.Failed} file(s) could not be deleted (they may be in use).") : "."));
        RefreshLogArchive();
    }

    /// <summary>Kullanıcı ayarı açtıysa açılışta 30 günden eski günlükleri siler (gerçek sonuç günlüğe yazılır).</summary>
    private void AutoDeleteOldLogsAtStartup()
    {
        if (!AutoDeleteOldLogs) return;
        var r = LogArchive.DeleteOld(_logger.LogDirectory, _logger.LogFilePath, LogArchive.DefaultCutoff());
        if (r.Deleted > 0 || r.Failed > 0)
            _logger.Info(L.T($"Açılış: {LogArchive.RetentionDays} günden eski {r.Deleted} günlük silindi ({TemporaryFilesManager.FormatSize(r.Bytes)})", $"Startup: {r.Deleted} log(s) older than {LogArchive.RetentionDays} days deleted ({TemporaryFilesManager.FormatSize(r.Bytes)})") +
                         (r.Failed > 0 ? L.T($"; {r.Failed} dosya silinemedi.", $"; {r.Failed} file(s) could not be deleted.") : "."));
    }
}

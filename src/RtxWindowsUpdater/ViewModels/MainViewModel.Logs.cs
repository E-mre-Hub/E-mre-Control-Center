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
    private string _logArchiveText = "Günlükler okunuyor…";
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
                ? $"Ayar: açılışta {LogArchive.RetentionDays} günden eski günlükler silinecek."
                : "Ayar: eski günlükler otomatik silinmeyecek.");
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
            ? "Günlük dosyası yok."
            : $"{s.Count} günlük dosyası · {TemporaryFilesManager.FormatSize(s.Bytes)} · en eski {s.Oldest:dd.MM.yyyy}\n" +
              (s.OldCount > 0
                  ? $"{LogArchive.RetentionDays} günden eski: {s.OldCount} dosya ({TemporaryFilesManager.FormatSize(s.OldBytes)})"
                  : $"{LogArchive.RetentionDays} günden eski günlük yok.");
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
        var ok = await Dialog.ShowAsync("Eski günlükleri sil",
            $"{LogArchive.RetentionDays} günden eski {s.OldCount} günlük dosyası ({TemporaryFilesManager.FormatSize(s.OldBytes)}) kalıcı olarak silinecek. " +
            "Son 30 günün günlükleri ve bu oturumun günlüğü korunur; işlem geçmişi ve ayarlar (state.json) etkilenmez.",
            Icons.Warning, DialogKind.Warning, "Sil", "Vazgeç");
        if (!ok) return;
        var r = LogArchive.DeleteOld(_logger.LogDirectory, _logger.LogFilePath, cutoff);
        _logger.Info($"Eski günlükler silindi: {r.Deleted} dosya ({TemporaryFilesManager.FormatSize(r.Bytes)})" +
                     (r.Failed > 0 ? $"; {r.Failed} dosya silinemedi (kullanımda olabilir)." : "."));
        RefreshLogArchive();
    }

    /// <summary>Kullanıcı ayarı açtıysa açılışta 30 günden eski günlükleri siler (gerçek sonuç günlüğe yazılır).</summary>
    private void AutoDeleteOldLogsAtStartup()
    {
        if (!AutoDeleteOldLogs) return;
        var r = LogArchive.DeleteOld(_logger.LogDirectory, _logger.LogFilePath, LogArchive.DefaultCutoff());
        if (r.Deleted > 0 || r.Failed > 0)
            _logger.Info($"Açılış: {LogArchive.RetentionDays} günden eski {r.Deleted} günlük silindi ({TemporaryFilesManager.FormatSize(r.Bytes)})" +
                         (r.Failed > 0 ? $"; {r.Failed} dosya silinemedi." : "."));
    }
}

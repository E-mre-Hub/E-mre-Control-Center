using System.Diagnostics;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>Çıkış istendiğinde süren işin türü (çıkış davranışı buna göre seçilir).</summary>
public enum ExitBlocker
{
    None,
    /// <summary>Kontrol / okuma: iptal edilebilir, araç süreçleri sonlandırılır.</summary>
    Check,
    /// <summary>Güncelleme / kurulum / onarım / temizlik: yarıda kesilmez.</summary>
    Update,
    SpeedTest,
    /// <summary>Speedtest by Ookla aracının kurulumu (winget): yarıda kesilmez.</summary>
    OoklaInstall
}

/// <summary>Çıkış isteği (bildirim alanı "Çıkış", kapatma düğmesi, kurulum / kaldırma isteği) sırasında süren işin güvenle bitirilmesi.</summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan CheckStopTimeout = TimeSpan.FromSeconds(30);

    public ExitBlocker CurrentExitBlocker =>
        IsBusy ? (IsUpdatePhase ? ExitBlocker.Update : ExitBlocker.Check)
        : SpeedTest.IsInstalling ? ExitBlocker.OoklaInstall
        : SpeedTest.IsRunning ? ExitBlocker.SpeedTest
        : ExitBlocker.None;

    /// <summary>
    /// Çıkış onayının metni – uygulamanın GERÇEK davranışına göre. Uygulamadaki her kurulum, onarım ve silme işlemi iptal
    /// edilemez başlatılır (yarıda kalırsa programı veya sistemi bozabilir); kontroller ise iptal edilebilir. Süren iş yoksa null.
    /// </summary>
    public (string Title, string Message, string Primary)? ExitPrompt()
    {
        var now = string.IsNullOrWhiteSpace(StepText) ? string.Empty : L.T($"Şu anda: {StepText}\n\n", $"Currently: {StepText}\n\n");
        return CurrentExitBlocker switch
        {
            ExitBlocker.Check => (L.T("Kontrol sürüyor", "A check is running"),
                now + L.T("Çıkarsanız kontrol durdurulur (çalışan kontrol araçları sonlandırılır; sistemde değişiklik yapan bir işlem ", "If you exit, the check is stopped (running check tools are ended; no operation that changes the system is ") +
                L.T("sürmüyor) ve uygulama kapanır.", "running) and the app closes."),
                L.T("Durdur ve çık", "Stop and exit")),
            ExitBlocker.Update => (L.T("Güncelleme / onarım sürüyor", "An update / repair is running"),
                now + L.T("Kurulum, onarım ve temizlik adımları yarıda kesilmez; yarım kalan bir kurulum programı veya sistemi bozabilir. ", "Installation, repair and cleanup steps are never interrupted; a half-finished installer can break the program or the system. ") +
                L.T("Sürmekte olan kurulum bitince kalan adımlar (ve sıradaki Winget paketleri) atlanır ve uygulama kendiliğinden kapanır.", "When the running installation finishes, the remaining steps (and the queued winget packages) are skipped and the app closes by itself."),
                L.T("Bitince kapat", "Close when finished")),
            ExitBlocker.SpeedTest => (L.T("Hız testi sürüyor", "A speed test is running"),
                L.T("Çıkarsanız hız testi durdurulur (sonuç kaydedilmez) ve uygulama kapanır.", "If you exit, the speed test is stopped (the result is not saved) and the app closes."),
                L.T("Durdur ve çık", "Stop and exit")),
            ExitBlocker.OoklaInstall => (L.T("Ookla aracı kuruluyor", "The Ookla tool is being installed"),
                L.T("Speedtest by Ookla aracının kurulumu yarıda kesilmez. Kurulum bitince uygulama kendiliğinden kapanır.", "The installation of the Speedtest by Ookla tool is never interrupted. The app closes by itself when the installation finishes."),
                L.T("Bitince kapat", "Close when finished")),
            _ => null
        };
    }

    /// <summary>
    /// Çıkıştan önce süren işi güvenle durdurur: kontrol ve hız testi iptal edilir ve süreçlerinin gerçekten sonlanması beklenir
    /// (en fazla 30 sn); kurulum / onarım adımı yarıda kesilmez – bitmesi beklenir (süre sınırı yok), kalan adımlar atlanır.
    /// </summary>
    public async Task StopForExitAsync()
    {
        var blocker = CurrentExitBlocker;
        if (blocker == ExitBlocker.None) return;
        _logger.Info(blocker switch
        {
            ExitBlocker.Check => L.T("Çıkış istendi: kontrol durduruluyor, ardından uygulama kapanacak.", "Exit requested: stopping the check, then the app will close."),
            ExitBlocker.Update => L.T("Çıkış istendi: sürmekte olan kurulum yarıda kesilmeyecek; bitince kalan adımlar ve sıradaki paketler atlanıp uygulama kapanacak.", "Exit requested: the running installation will not be interrupted; when it finishes, the remaining steps and queued packages are skipped and the app closes."),
            ExitBlocker.SpeedTest => L.T("Çıkış istendi: hız testi durduruluyor, ardından uygulama kapanacak.", "Exit requested: stopping the speed test, then the app will close."),
            _ => L.T("Çıkış istendi: Ookla aracının kurulumu bitince uygulama kapanacak.", "Exit requested: the app will close when the Ookla tool installation finishes.")
        });

        if (IsBusy) Cancel();
        if (SpeedTest.IsRunning && SpeedTest.CancelCommand.CanExecute(null)) SpeedTest.CancelCommand.Execute(null);

        var watch = Stopwatch.StartNew();
        while (CurrentExitBlocker != ExitBlocker.None)
        {
            var current = CurrentExitBlocker;
            if (current is ExitBlocker.Check or ExitBlocker.SpeedTest && watch.Elapsed > CheckStopTimeout)
            {
                _logger.Warning(L.T($"{(current == ExitBlocker.Check ? "Kontrol" : "Hız testi")} {CheckStopTimeout.TotalSeconds:0} sn içinde ", $"{(current == ExitBlocker.Check ? "The check" : "The speed test")} did not stop within {CheckStopTimeout.TotalSeconds:0} sec") +
                                L.T("durmadı; uygulama kapanıyor (kalan kontrol araçları kapanışta sonlandırılır).", "; the app is closing (remaining check tools are ended at shutdown)."));
                break;
            }
            await Task.Delay(200);
        }
    }
}

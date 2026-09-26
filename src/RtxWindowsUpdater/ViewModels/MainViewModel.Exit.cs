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
        var now = string.IsNullOrWhiteSpace(StepText) ? string.Empty : $"Şu anda: {StepText}\n\n";
        return CurrentExitBlocker switch
        {
            ExitBlocker.Check => ("Kontrol sürüyor",
                now + "Çıkarsanız kontrol durdurulur (çalışan kontrol araçları sonlandırılır; sistemde değişiklik yapan bir işlem " +
                "sürmüyor) ve uygulama kapanır.",
                "Durdur ve çık"),
            ExitBlocker.Update => ("Güncelleme / onarım sürüyor",
                now + "Kurulum, onarım ve temizlik adımları yarıda kesilmez; yarım kalan bir kurulum programı veya sistemi bozabilir. " +
                "Bu adım bitince kalan adımlar atlanır ve uygulama kendiliğinden kapanır.",
                "Bitince kapat"),
            ExitBlocker.SpeedTest => ("Hız testi sürüyor",
                "Çıkarsanız hız testi durdurulur (sonuç kaydedilmez) ve uygulama kapanır.",
                "Durdur ve çık"),
            ExitBlocker.OoklaInstall => ("Ookla aracı kuruluyor",
                "Speedtest by Ookla aracının kurulumu yarıda kesilmez. Kurulum bitince uygulama kendiliğinden kapanır.",
                "Bitince kapat"),
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
            ExitBlocker.Check => "Çıkış istendi: kontrol durduruluyor, ardından uygulama kapanacak.",
            ExitBlocker.Update => "Çıkış istendi: devam eden adım yarıda kesilmeyecek; bitince kalan adımlar atlanıp uygulama kapanacak.",
            ExitBlocker.SpeedTest => "Çıkış istendi: hız testi durduruluyor, ardından uygulama kapanacak.",
            _ => "Çıkış istendi: Ookla aracının kurulumu bitince uygulama kapanacak."
        });

        if (IsBusy) Cancel();
        if (SpeedTest.IsRunning && SpeedTest.CancelCommand.CanExecute(null)) SpeedTest.CancelCommand.Execute(null);

        var watch = Stopwatch.StartNew();
        while (CurrentExitBlocker != ExitBlocker.None)
        {
            var current = CurrentExitBlocker;
            if (current is ExitBlocker.Check or ExitBlocker.SpeedTest && watch.Elapsed > CheckStopTimeout)
            {
                _logger.Warning($"{(current == ExitBlocker.Check ? "Kontrol" : "Hız testi")} {CheckStopTimeout.TotalSeconds:0} sn içinde " +
                                "durmadı; uygulama kapanıyor (kalan kontrol araçları kapanışta sonlandırılır).");
                break;
            }
            await Task.Delay(200);
        }
    }
}

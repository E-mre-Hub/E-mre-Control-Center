using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Microsoft Edge ve Microsoft Edge WebView2 Çalışma Zamanı için Microsoft'un kendi güncelleyicisi (Microsoft Edge Update).
/// NEDEN: Windows 11'de ikisi de sistem bileşenidir – Edge kurulum programı kaldırmayı reddeder (winget uninstall → kurulum
/// programı çıkış kodu 93, 0x8A150030) ve winget yerinde yükseltemez (0x8A15008E, kurulum teknolojisi farklı). Bu yüzden
/// "kaldır + yeniden kur" bu paketlerde hiçbir zaman çalışmaz.
/// Güncelleme, Edge'in "Ayarlar → Microsoft Edge hakkında" sayfasının kullandığı resmi COM arayüzüyle yapılır
/// (MicrosoftEdgeUpdate.Update3WebMachine: createAppBundleWeb → initialize → createInstalledApp → checkForUpdate [→ install]).
/// Durum, sunulan sürüm, hata kodu ve kurulum programı sonucu doğrudan Edge Update'ten okunur; kurulumun sonucu ayrıca kayıt
/// defterindeki kurulu sürümle (EdgeUpdate\Clients\{uygulama}\pv) doğrulanır.
/// Denetim sistemi değiştirmez. Kurulum yalnızca kullanıcı güncellemeyi başlattığında çağrılır; yükseltme (UAC) istenmez ve
/// atlatılmaz – süreç yönetici değilse Edge Update kurulumu reddeder ve bu gerçek hata bildirilir.
/// </summary>
public static class EdgeUpdateService
{
    /// <param name="WingetId">winget paket kimliği.</param>
    /// <param name="AppGuid">Edge Update uygulama kimliği.</param>
    /// <param name="ProcessName">Uygulamanın çalışan işlem adı (uzantısız; yeniden başlatma notu için).</param>
    public sealed record EdgeApp(string WingetId, string AppGuid, string Name, string ProcessName);

    private static readonly EdgeApp[] Known =
    [
        new("Microsoft.Edge", "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}", "Microsoft Edge", "msedge"),
        new("Microsoft.EdgeWebView2Runtime", "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "Microsoft Edge WebView2 Çalışma Zamanı", "msedgewebview2")
    ];

    /// <summary>winget kimliği Microsoft Edge Update ile güncellenen bir pakete aitse karşılığı; değilse null.</summary>
    public static EdgeApp? Find(string wingetId) =>
        Known.FirstOrDefault(a => a.WingetId.Equals(wingetId, StringComparison.OrdinalIgnoreCase));

    private const string ProgId = "MicrosoftEdgeUpdate.Update3WebMachine";
    private const string ClientsKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\";

    /// <summary>ICurrentState.stateValue (Omaha CurrentState).</summary>
    internal enum UpdateState
    {
        Init = 1, WaitingToCheck = 2, Checking = 3, UpdateAvailable = 4, WaitingToDownload = 5, RetryingDownload = 6,
        Downloading = 7, DownloadComplete = 8, Extracting = 9, ApplyingPatch = 10, ReadyToInstall = 11, WaitingToInstall = 12,
        Installing = 13, InstallComplete = 14, Paused = 15, NoUpdate = 16, Error = 17
    }

    public enum EdgeUpdateOutcome
    {
        /// <summary>Denetim: Edge Update bu cihaza yeni sürüm sunuyor.</summary>
        UpdateAvailable,
        /// <summary>Edge Update bu cihaz için yeni sürüm sunmuyor.</summary>
        NoUpdate,
        /// <summary>Edge Update kurulumun tamamlandığını bildirdi (ayrıca kayıt defterinden doğrulanmalı).</summary>
        Installed,
        /// <summary>Edge Update hata durumu bildirdi.</summary>
        Error,
        TimedOut,
        /// <summary>Edge Update'in COM arayüzüne ulaşılamadı.</summary>
        Unavailable
    }

    public sealed record EdgeUpdateResult(
        EdgeUpdateOutcome Outcome,
        string? AvailableVersion = null,
        int ErrorCode = 0,
        int InstallerResultCode = 0,
        string? Message = null)
    {
        public string ErrorCodeHex => $"0x{ErrorCode:X8}";

        /// <summary>Başarısız sonucun gerçek açıklaması (hata kodu, kurulum programı sonucu, Edge Update'in kendi mesajı).</summary>
        public string Describe()
        {
            switch (Outcome)
            {
                case EdgeUpdateOutcome.Error:
                    var text = $"Microsoft Edge Update hata bildirdi ({ErrorCodeHex})";
                    if (InstallerResultCode != 0) text += $", kurulum programı çıkış kodu {InstallerResultCode}";
                    if (!string.IsNullOrWhiteSpace(Message)) text += $" – \"{Message.Trim()}\"";
                    if (unchecked((uint)ErrorCode) == 0x80070005) text += ". Kurulum için yönetici yetkisi gerekir";
                    return text + ".";
                case EdgeUpdateOutcome.TimedOut:
                case EdgeUpdateOutcome.Unavailable:
                    return Message ?? Outcome.ToString();
                case EdgeUpdateOutcome.NoUpdate:
                    return "Microsoft Edge Update bu cihaz için yeni sürüm sunmuyor.";
                default:
                    return Message ?? Outcome.ToString();
            }
        }
    }

    // ------------------------------------------------------------------ KAYIT DEFTERİ (salt okunur)

    /// <summary>Edge Update'in kaydettiği kurulu sürüm (HKLM 32 bit görünüm, Clients\{uygulama}\pv); okunamazsa null.</summary>
    public static string? ReadInstalledVersion(EdgeApp app)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(ClientsKey + app.AppGuid);
            return (key?.GetValue("pv") as string)?.Trim() is { Length: > 0 } pv ? pv : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Yeni sürüm kuruldu ama uygulama açık olduğu için dosya değişimi (yeniden adlandırma) uygulama kapanınca yapılacak mı?
    /// Chromium/Edge kurulum programı bu durumda Clients anahtarına eski sürümü ("opv") ve yeniden adlandırma komutunu yazar.
    /// </summary>
    public static bool IsRestartPending(EdgeApp app)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(ClientsKey + app.AppGuid);
            return key?.GetValue("opv") is string { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Uygulamanın çalışan işlem sayısı (yalnızca bilgi; hiçbir işlem kapatılmaz).</summary>
    public static int CountRunning(EdgeApp app)
    {
        var processes = Process.GetProcessesByName(app.ProcessName);
        foreach (var p in processes) p.Dispose();
        return processes.Length;
    }

    /// <summary>a ≥ b (sürüm karşılaştırması; ayrıştırılamazsa false).</summary>
    public static bool IsAtLeast(string? a, string? b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) && va >= vb;

    // ------------------------------------------------------------------ EDGE UPDATE (COM)

    /// <summary>Yalnızca denetim: Edge Update'e bu cihaz için yeni sürüm olup olmadığını sorar. Sistemi değiştirmez.</summary>
    public static Task<EdgeUpdateResult> CheckAsync(EdgeApp app, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(app, install: false, progress: null, timeout, ct);

    /// <summary>
    /// Denetim + (yeni sürüm sunuluyorsa) indirme ve kurulum. Başladıktan sonra iptal edilmez (kurulum yarıda kesilmez);
    /// zaman aşımında da Edge Update'e iptal gönderilmez, sonuç "zaman aşımı" olarak bildirilir.
    /// </summary>
    /// <param name="progress">Edge Update'in bildirdiği GERÇEK durum / bayt / yüzde değişimleri (günlük için).</param>
    public static Task<EdgeUpdateResult> InstallAsync(EdgeApp app, Action<string>? progress, TimeSpan timeout) =>
        RunAsync(app, install: true, progress, timeout, CancellationToken.None);

    /// <summary>COM oturumu ayrı bir STA iş parçacığında yürür (çağıranın iş parçacığı bloklanmaz).</summary>
    private static Task<EdgeUpdateResult> RunAsync(EdgeApp app, bool install, Action<string>? progress, TimeSpan timeout,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<EdgeUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(Session(app, install, progress, timeout, ct));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Microsoft Edge Update"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static EdgeUpdateResult Session(EdgeApp app, bool install, Action<string>? progress, TimeSpan timeout,
        CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (type is null)
            return new(EdgeUpdateOutcome.Unavailable,
                Message: $"Microsoft Edge Update'in COM arayüzü ({ProgId}) bu sistemde kayıtlı değil.");

        object? server = null, bundle = null, appWeb = null;
        try
        {
            try
            {
                server = Activator.CreateInstance(type);
            }
            catch (COMException ex)
            {
                return new(EdgeUpdateOutcome.Unavailable, ErrorCode: ex.HResult,
                    Message: $"Microsoft Edge Update başlatılamadı (0x{ex.HResult:X8}): {ex.Message}");
            }
            if (server is null)
                return new(EdgeUpdateOutcome.Unavailable, Message: "Microsoft Edge Update başlatılamadı (nesne oluşturulamadı).");
            SetProxyBlanket(server);

            dynamic updater = server;
            bundle = (object)updater.createAppBundleWeb();
            SetProxyBlanket(bundle);
            dynamic b = bundle;
            b.initialize();
            b.createInstalledApp(app.AppGuid);
            appWeb = (object)b.appWeb(0);
            SetProxyBlanket(appWeb);
            dynamic a = appWeb;

            b.checkForUpdate();

            var watch = Stopwatch.StartNew();
            var installRequested = false;
            string? available = null;
            UpdateState? last = null;
            var lastProgressPercent = -1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = ReadState(a);
                if (snapshot.State != last)
                {
                    progress?.Invoke(StateText(snapshot.State, available));
                    last = snapshot.State;
                    lastProgressPercent = -1;
                }
                if (!string.IsNullOrWhiteSpace(snapshot.AvailableVersion)) available = snapshot.AvailableVersion;

                switch (snapshot.State)
                {
                    case UpdateState.UpdateAvailable:
                        if (!install) return new(EdgeUpdateOutcome.UpdateAvailable, available);
                        if (!installRequested)
                        {
                            b.install(); // indirme + kurulum (Edge'in "Hakkında" sayfasıyla aynı akış)
                            installRequested = true;
                        }
                        break;
                    case UpdateState.NoUpdate:
                        return new(EdgeUpdateOutcome.NoUpdate, available);
                    case UpdateState.InstallComplete:
                        return new(EdgeUpdateOutcome.Installed, available);
                    case UpdateState.Error:
                        return new(EdgeUpdateOutcome.Error, available, snapshot.ErrorCode, snapshot.InstallerResultCode,
                            snapshot.Message);
                    case UpdateState.Downloading when snapshot.TotalBytes > 0:
                        var percent = (int)(snapshot.Bytes * 100 / snapshot.TotalBytes);
                        if (percent / 10 != lastProgressPercent / 10)
                        {
                            progress?.Invoke($"İndiriliyor: {snapshot.Bytes / 1048576.0:0.0} / {snapshot.TotalBytes / 1048576.0:0.0} MB (%{percent})");
                            lastProgressPercent = percent;
                        }
                        break;
                    case UpdateState.Installing when snapshot.InstallPercent >= 0:
                        if (snapshot.InstallPercent / 25 != lastProgressPercent / 25)
                        {
                            progress?.Invoke($"Kuruluyor: %{snapshot.InstallPercent}");
                            lastProgressPercent = snapshot.InstallPercent;
                        }
                        break;
                }

                if (watch.Elapsed > timeout)
                {
                    return new(EdgeUpdateOutcome.TimedOut, available,
                        Message: $"Microsoft Edge Update {timeout.TotalMinutes:0} dakika içinde sonuç bildirmedi (son durum: {StateText(snapshot.State, available)}).");
                }
                Thread.Sleep(250);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex)
        {
            return new(EdgeUpdateOutcome.Error, ErrorCode: ex.HResult, Message: ex.Message);
        }
        catch (Exception ex)
        {
            // Beklenmeyen COM bağlama / tür hataları başarı sayılmaz; gerçek mesajla bildirilir.
            return new(EdgeUpdateOutcome.Error, ErrorCode: ex.HResult, Message: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Release(appWeb);
            Release(bundle);
            Release(server);
        }
    }

    private readonly record struct StateSnapshot(
        UpdateState State, string? AvailableVersion, long Bytes, long TotalBytes, int InstallPercent,
        int ErrorCode, int InstallerResultCode, string? Message);

    /// <summary>ICurrentState'i okur; özellikler yalnızca ilgili durumda okunur (diğer durumlarda Edge Update hata döndürebilir).</summary>
    private static StateSnapshot ReadState(dynamic app)
    {
        object stateObject = app.currentState;
        try
        {
            SetProxyBlanket(stateObject);
            dynamic s = stateObject;
            var state = (UpdateState)Convert.ToInt32((object?)s.stateValue);
            string? available = null;
            long bytes = 0, total = 0;
            int installPercent = -1, error = 0, installer = 0;
            string? message = null;
            if (state is UpdateState.UpdateAvailable or >= UpdateState.WaitingToDownload and <= UpdateState.InstallComplete)
                available = Try(() => (string?)s.availableVersion);
            if (state == UpdateState.Downloading)
            {
                bytes = Try(() => Convert.ToInt64((object?)s.bytesDownloaded));
                total = Try(() => Convert.ToInt64((object?)s.totalBytesToDownload));
            }
            if (state == UpdateState.Installing)
                installPercent = Try(() => Convert.ToInt32((object?)s.installProgress), -1);
            if (state == UpdateState.Error)
            {
                error = Try(() => Convert.ToInt32((object?)s.errorCode));
                installer = Try(() => Convert.ToInt32((object?)s.installerResultCode));
                message = Try(() => (string?)s.completionMessage);
            }
            return new(state, available, bytes, total, installPercent, error, installer, message);
        }
        finally
        {
            Release(stateObject);
        }
    }

    private static T Try<T>(Func<T> read, T fallback = default!)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or OverflowException or
                                       Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return fallback;
        }
    }

    private static string StateText(UpdateState state, string? available) => state switch
    {
        UpdateState.Init or UpdateState.WaitingToCheck => "Denetim başlatılıyor",
        UpdateState.Checking => "Güncelleme denetleniyor",
        UpdateState.UpdateAvailable => $"Yeni sürüm sunuluyor: {available ?? "?"}",
        UpdateState.WaitingToDownload => "İndirme sırası bekleniyor",
        UpdateState.RetryingDownload => "İndirme yeniden deneniyor",
        UpdateState.Downloading => "İndiriliyor",
        UpdateState.DownloadComplete => "İndirme tamamlandı",
        UpdateState.Extracting => "Paket açılıyor",
        UpdateState.ApplyingPatch => "Fark yaması uygulanıyor",
        UpdateState.ReadyToInstall or UpdateState.WaitingToInstall => "Kuruluma hazırlanıyor",
        UpdateState.Installing => "Kuruluyor",
        UpdateState.InstallComplete => "Kurulum tamamlandı",
        UpdateState.Paused => "Duraklatıldı",
        UpdateState.NoUpdate => "Bu cihaz için yeni sürüm yok",
        UpdateState.Error => "Hata",
        _ => $"Durum {(int)state}"
    };

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    // Chromium'un Google Update / Edge Update istemcisiyle aynı vekil güvenliği: sunucu, işlemi çağıranın kimliğiyle
    // doğrulayabilsin (makine düzeyi kurulumda yönetici denetimi). Başarısız olursa varsayılan güvenlikle devam edilir.
    private const uint RpcCAuthnDefault = 0xFFFFFFFF;
    private const uint RpcCAuthzDefault = 0xFFFFFFFF;
    private const uint RpcCAuthnLevelPktPrivacy = 6;
    private const uint RpcCImpLevelImpersonate = 3;
    private const uint EoacDynamicCloaking = 0x40;

    [DllImport("ole32.dll")]
    private static extern int CoSetProxyBlanket(IntPtr proxy, uint authnSvc, uint authzSvc, IntPtr serverPrincName,
        uint authnLevel, uint impLevel, IntPtr authInfo, uint capabilities);

    private static void SetProxyBlanket(object comObject)
    {
        foreach (var pointer in new[] { Marshal.GetIUnknownForObject(comObject), Marshal.GetIDispatchForObject(comObject) })
        {
            try
            {
                CoSetProxyBlanket(pointer, RpcCAuthnDefault, RpcCAuthzDefault, new IntPtr(-1), RpcCAuthnLevelPktPrivacy,
                    RpcCImpLevelImpersonate, IntPtr.Zero, EoacDynamicCloaking);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
    }
}

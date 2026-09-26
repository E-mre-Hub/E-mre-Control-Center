using System.Diagnostics;
using System.Windows.Input;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Services.Diagnostics;

namespace RtxWindowsUpdater.ViewModels.Tools;

/// <summary>Araç ekranlarının ana pencereden kullandığı hizmetler (MainViewModel uygular).</summary>
public interface IToolHost
{
    Logger Logger { get; }
    DialogViewModel Dialog { get; }

    /// <summary>Sistem işlemi (kart kontrolü / güncelleme) veya hız testi sürmüyor: değişiklik yapan araç işlemleri başlatılabilir.</summary>
    bool CanStartTool { get; }

    bool IsAdmin { get; }
    void OpenSection(string categoryKey, string sectionKey);

    /// <summary>İşlem geçmişine gerçek sonuçla kayıt (başarısız işlem başarılı kaydedilmez).</summary>
    void RecordToolOperation(string title, CheckState state, string summary, TimeSpan duration, string? error = null, bool cancelled = false);

    /// <summary>Özet ekranındaki tanılama satırını son gerçek sonuçla günceller.</summary>
    void ReportDiagnostic(string key, CheckResult result);
}

/// <summary>
/// Araç ekranlarının ortak temeli: yükleme / yenileme, iptal, meşgul durumu, gerçek hata metni. Ekran açılınca (<see cref="Activate"/>)
/// gerekiyorsa bir kez yüklenir; kapanınca (<see cref="Deactivate"/>) izleme ve süren işlem durdurulur. Sistem çağrıları servislerde,
/// arka plan iş parçacığında yapılır; burada yalnızca sonuç ekrana uygulanır.
/// </summary>
public abstract class ToolViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private string _statusText = "Henüz okunmadı";
    private string? _errorText;
    private bool _hasLoaded;
    private DateTime? _lastRun;

    protected ToolViewModel(IToolHost host)
    {
        Host = host;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
    }

    protected IToolHost Host { get; }
    protected Logger Logger => Host.Logger;

    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText { get => _statusText; protected set => Set(ref _statusText, value); }

    /// <summary>Okuma / işlem başarısızsa gerçek neden (ekranda kırmızı şerit).</summary>
    public string? ErrorText
    {
        get => _errorText;
        protected set
        {
            if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);
    public bool HasLoaded { get => _hasLoaded; private set => Set(ref _hasLoaded, value); }
    public DateTime? LastRun { get => _lastRun; private set { Set(ref _lastRun, value); OnPropertyChanged(nameof(LastRunText)); } }
    public string LastRunText => LastRun is { } t ? "Son okuma: " + t.ToString("HH:mm:ss") : "";

    /// <summary>Üst şeritte "Yenile" gösterilsin mi (okuma yapmayan ekranlarda gizli).</summary>
    public virtual bool ShowRefresh => true;

    /// <summary>Ekran açılınca kendiliğinden okunur mu (ağ testi / tarama gibi işlemler yalnızca kullanıcı başlatınca).</summary>
    protected virtual bool AutoLoad => true;

    public virtual void Activate()
    {
        if (!HasLoaded && AutoLoad && !IsBusy) _ = RefreshAsync();
    }

    public virtual void Deactivate() { }

    /// <summary>Yükleme gövdesi: servis çağrıları <c>await Task.Run(...)</c> ile yapılır, sonuç UI iş parçacığında uygulanır.</summary>
    protected abstract Task LoadAsync(CancellationToken ct);

    /// <summary>Yenile butonu ve ilk açılış.</summary>
    public Task RefreshAsync() => RunAsync(LoadAsync, markLoaded: true);

    /// <summary>İptal edilebilir, meşgul durumunu yöneten ortak çalıştırıcı. Hata yakalanır, gerçek mesaj ekranda kalır.</summary>
    protected async Task<bool> RunAsync(Func<CancellationToken, Task> body, bool markLoaded = false)
    {
        if (IsBusy) return false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        ErrorText = null;
        var ok = false;
        try
        {
            await body(cts.Token);
            ok = true;
            if (markLoaded) HasLoaded = true;
            LastRun = DateTime.Now;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = "İptal edildi";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "Okunamadı";
            Logger.Error($"{GetType().Name}: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsBusy = false;
        }
        return ok;
    }

    protected void CancelRunning() => _cts?.Cancel();

    /// <summary>Kısa süreli ölçüm (işlem geçmişi kaydı için).</summary>
    protected static Stopwatch Start() => Stopwatch.StartNew();

    /// <summary>Kullanıcının açık onayı (pencere içi iletişim kutusu).</summary>
    protected Task<bool> ConfirmAsync(string title, string message, string primary, IEnumerable<string>? bullets = null, bool warning = true) =>
        Host.Dialog.ShowAsync(title, message, warning ? "" : "", warning ? DialogKind.Warning : DialogKind.Question,
            primary, "Vazgeç", bullets);

    protected Task InformAsync(string title, string message, bool error = false) =>
        Host.Dialog.ShowAsync(title, message, error ? "" : "", error ? DialogKind.Warning : DialogKind.Info, "Tamam");

    /// <summary>Windows'un kendi Ayarlar / Güvenlik sayfasını açar (yalnızca sabit, uygulamanın tanıdığı adresler).</summary>
    protected void OpenWindowsUri(string uri)
    {
        if (!(uri.StartsWith("ms-settings:", StringComparison.Ordinal) || uri == "windowsdefender:")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            Logger.Info("Windows ayar sayfası açıldı: " + uri);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warning($"Ayar sayfası açılamadı ({uri}): {ex.Message}");
        }
    }

    /// <summary>Dosyayı Gezgin'de seçili gösterir (yalnızca var olan dosya; argüman tırnaklanır, komut birleştirilmez).</summary>
    protected void ShowInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false
            };
            if (System.IO.File.Exists(path)) psi.ArgumentList.Add("/select," + path);
            else if (System.IO.Directory.Exists(path)) psi.ArgumentList.Add(path);
            else return;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warning("Gezgin açılamadı: " + ex.Message);
        }
    }
}

/// <summary>Tanılama sonucu satırı (Sistem Sağlığı, Güvenlik, Tek Tıkla Tanıla, ağ testleri). Tıklanınca ayrıntı ekranı açılır.</summary>
public sealed class CheckRowViewModel : ObservableObject
{
    private CheckResult _result;

    public CheckRowViewModel(CheckResult result, Action<string, string>? navigate = null, int index = 0)
    {
        _result = result;
        Index = index;
        OpenCommand = new RelayCommand(() =>
        {
            if (_result.TargetCategory is { } c && _result.TargetSection is { } s) navigate?.Invoke(c, s);
        }, () => navigate is not null && _result.TargetCategory is not null && _result.TargetSection is not null);
    }

    public int Index { get; }
    public string Title => _result.Title;
    public CheckState State => _result.State;
    public string StateText => CheckStates.Text(_result.State);
    public string Summary => _result.Summary;
    public string? Detail => _result.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(_result.Detail);
    public bool CanOpen => _result.TargetCategory is not null && _result.TargetSection is not null;
    public ICommand OpenCommand { get; }
    public CheckResult Result => _result;

    public void Update(CheckResult result)
    {
        _result = result;
        OnPropertyChanged(string.Empty);
    }
}

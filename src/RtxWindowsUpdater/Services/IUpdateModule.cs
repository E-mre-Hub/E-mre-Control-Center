using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Orkestratörün sırayla çalıştırdığı her bileşen bu arayüzü uygular.
/// Uygulamalar istisna fırlatmamalı; tüm hataları <see cref="ModuleResult"/> içinde döndürmelidir
/// (orkestratör yine de beklenmeyen istisnalara karşı ayrıca korur).
/// </summary>
public interface IUpdateModule
{
    string Key { get; }
    string DisplayName { get; }

    /// <summary>Sistemi değiştirmeden gerçek durumu okur.</summary>
    Task<ModuleResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Yalnızca kontrolde bulunan uygulanabilir güncellemeleri / işlemleri gerçekleştirir.</summary>
    Task<ModuleResult> UpdateAsync(ModuleResult checkResult, CancellationToken cancellationToken);
}

/// <summary>
/// Kartında kendi eylem butonu bulunan sistem bakım modülleri (SFC, DISM, MRT).
/// </summary>
public interface IMaintenanceModule : IUpdateModule
{
    /// <summary>Kart butonuna basıldığında çalışan işlem (SFC: /scannow, DISM: /CheckHealth, MRT: hızlı tarama).</summary>
    Task<ModuleResult> RunActionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Güncellemesi "uygulama çalışıyor / dosyalar kullanımda" nedeniyle başarısız olan öğeleri, kullanıcının onayladığı
/// engelleyen uygulamaları kapattıktan sonra yeniden deneyebilen modüller (Winget, Microsoft Store).
/// </summary>
public interface IInUseRetryModule
{
    /// <param name="previous">Önceki güncellemenin gerçek sonucu (engelleyen işlemler öğelerde kayıtlı).</param>
    /// <param name="approved">Kullanıcının kapatılmasını onayladığı işlemler (PID + başlangıç zamanıyla).</param>
    Task<ModuleResult> RetryAfterClosingAsync(ModuleResult previous, IReadOnlyCollection<RunningProcessInfo> approved,
        CancellationToken cancellationToken);
}

/// <summary>
/// Otomatik uygulanmayan güncellemeleri (açık hedefleme gerekli / kurulum teknolojisi farklı) yalnızca kullanıcı
/// bunları AYRICA seçtiğinde uygulayabilen modüller (Winget, Microsoft Store).
/// </summary>
public interface IManualUpdateModule
{
    /// <param name="check">Kontrolün gerçek sonucu (öğelerde <see cref="UpdateItem.Manual"/> türü kayıtlı).</param>
    /// <param name="ids">Kullanıcının seçtiği paket kimlikleri.</param>
    Task<ModuleResult> UpdateManualAsync(ModuleResult check, IReadOnlyCollection<string> ids, CancellationToken cancellationToken);
}

/// <summary>Çalışırken canlı ilerleme bildiren modüller.</summary>
public interface IProgressReportingModule
{
    event Action<ModuleProgress>? ProgressChanged;
}

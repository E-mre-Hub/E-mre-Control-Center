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

/// <summary>Çalışırken canlı ilerleme bildiren modüller.</summary>
public interface IProgressReportingModule
{
    event Action<ModuleProgress>? ProgressChanged;
}

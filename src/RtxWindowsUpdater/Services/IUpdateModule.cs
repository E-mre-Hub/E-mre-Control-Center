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

    /// <summary>Yalnızca kontrolde bulunan uygulanabilir güncellemeleri kurar.</summary>
    Task<ModuleResult> UpdateAsync(ModuleResult checkResult, CancellationToken cancellationToken);
}

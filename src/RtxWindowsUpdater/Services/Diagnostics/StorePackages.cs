namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Bu kullanıcının paketli (Microsoft Store / MSIX) uygulaması.</summary>
public sealed record StorePackage(string FamilyName, string DisplayName, string Publisher, string Version, string? InstalledPath, DateTime? InstalledDate,
    bool FromStore);

/// <summary>
/// Paketli uygulamalar: Windows'un PackageManager API'si (Ayarlar → Uygulamalar ile aynı kaynak; görünen ad ve yayıncı paket bildiriminden,
/// yerelleştirilmiş). Yönetici gerekmez; yalnızca geçerli kullanıcının paketleri okunur. Çerçeve / kaynak paketleri listelenmez.
/// </summary>
public static class StorePackages
{
    public static IReadOnlyList<StorePackage> Read()
    {
        var list = new List<StorePackage>();
        var manager = new Windows.Management.Deployment.PackageManager();
        foreach (var p in manager.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                var id = p.Id;
                string display, publisher;
                try { display = p.DisplayName; } catch (Exception) { display = string.Empty; }
                try { publisher = p.PublisherDisplayName; } catch (Exception) { publisher = string.Empty; }
                string? path = null;
                try { path = p.InstalledPath; } catch (Exception) { /* kaldırılmakta olan paket */ }
                DateTime? installed = null;
                try { installed = p.InstalledDate.LocalDateTime; } catch (Exception) { }
                var v = id.Version;
                list.Add(new StorePackage(id.FamilyName, string.IsNullOrWhiteSpace(display) ? id.Name : display.Trim(),
                    string.IsNullOrWhiteSpace(publisher) ? "—" : publisher.Trim(), $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", path,
                    installed is { Year: > 2000 } ? installed : null, p.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store));
            }
            catch (Exception)
            {
                // tek paketin bildirimi okunamadı (bozuk / kaldırılıyor): diğerleri listelenir
            }
        }
        return list;
    }

    /// <summary>Paket aile adı → görünen ad (başlangıç görevleri ve gizlilik erişim kayıtları için). Okunamazsa boş sözlük.</summary>
    public static IReadOnlyDictionary<string, string> DisplayNamesByFamily()
    {
        try
        {
            return Read().GroupBy(p => p.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }
}

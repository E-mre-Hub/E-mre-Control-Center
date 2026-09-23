using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// NVIDIA RTX sürücü entegrasyonu.
///
/// Tespit : WMI Win32_VideoController (GPU) + nvidia-smi (sürücüyle gelen resmi NVIDIA aracı) ile kurulu sürüm.
/// NVIDIA App: Kayıt defteri "Uninstall" girdilerinden tespit edilir (yalnızca bilgi).
///   NVIDIA App'in herkese açık bir komut satırı / API arayüzü YOKTUR; bu yüzden güncelleme kontrolü
///   nvidia.com sürücü indirme sayfasının kullandığı resmi NVIDIA servisleriyle yapılır:
///     - https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3   (ürün kimlikleri)
///     - https://gfwsl.geforce.com/.../AjaxDriverService.php?func=DriverManualLookup (en son WHQL DCH sürücü)
/// Kurulum: Sürücü yalnızca *.download.nvidia.com (HTTPS) adresinden indirilir, Authenticode imzası
///   doğrulanır (imzalayan "NVIDIA Corporation" olmalı) ve NVIDIA kurulum programı sessiz kipte
///   (-s -noreboot) çalıştırılır. Kurulum sonrası sürüm nvidia-smi ile yeniden okunarak doğrulanır.
/// </summary>
public sealed class NvidiaDriverManager(Logger logger, HttpClient http) : IUpdateModule
{
    public string Key => ComponentKeys.Nvidia;
    public string DisplayName => "NVIDIA Driver";

    private const string ProductLookupUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string DriverLookupUrl =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        "?func=DriverManualLookup&psid={0}&pfid={1}&osID=135&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);

    private static string NvidiaSmiPath => Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info("NVIDIA sürücüsü kontrol ediliyor...");

        // 1) GPU
        GpuInfo? gpu;
        try
        {
            var gpus = SystemRequirementsChecker.ReadGpus();
            gpu = gpus.FirstOrDefault(g => g.IsRtx);
            if (gpu is null)
            {
                var nv = gpus.FirstOrDefault(g => g.IsNvidia);
                var reason = nv is null
                    ? "NVIDIA ekran kartı bulunamadı."
                    : $"{nv.Name} RTX serisi değil; bu uygulama yalnızca RTX kartları destekler.";
                logger.Error(reason);
                return ModuleResult.CheckFailed(Key, reason);
            }
        }
        catch (Exception ex)
        {
            var reason = "Ekran kartı bilgisi okunamadı: " + ex.Message;
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason);
        }
        logger.Info($"GPU: {gpu.Name}");

        // 2) Kurulu sürücü sürümü
        var current = await ReadInstalledDriverVersionAsync(gpu, ct);
        if (current is null)
        {
            const string reason = "Kurulu NVIDIA sürücü sürümü okunamadı (nvidia-smi ve WMI başarısız).";
            logger.Error(reason);
            return ModuleResult.CheckFailed(Key, reason, $"GPU: {gpu.Name}");
        }
        logger.Info($"Mevcut sürücü: {current}");

        // 3) NVIDIA App (bilgi)
        var (appInstalled, appVersion) = DetectNvidiaApp();
        var appText = !appInstalled ? "Bulunamadı" : appVersion is null ? "Kurulu" : $"Kurulu ({appVersion})";
        logger.Info(appInstalled
            ? $"NVIDIA App: {appText}. (NVIDIA App'in herkese açık bir komut satırı arayüzü olmadığından kontrol NVIDIA'nın resmi sürücü servisiyle yapılır.)"
            : "NVIDIA App bulunamadı; kontrol NVIDIA'nın resmi sürücü servisi üzerinden yapılacak.");

        // 4) NVIDIA resmi servisinden en son sürücü
        DriverInfo latest;
        try
        {
            latest = await LookupLatestDriverAsync(gpu.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var reason = ex is HttpRequestException or TaskCanceledException
                ? $"NVIDIA sürücü servisine ulaşılamadı (internet bağlantısını kontrol edin): {ex.Message}"
                : ex.Message;
            logger.Error("NVIDIA sürücüsü kontrolü gerçekleştirilemedi: " + reason);
            return ModuleResult.CheckFailed(Key, reason,
                $"GPU: {ShortName(gpu.Name)}\nMevcut sürücü: {current}\nNVIDIA App: {appText}");
        }

        logger.Info($"NVIDIA'nın yayımladığı en son sürücü: {latest.Version} ({latest.Name}, {latest.ReleaseDate})");

        var updateAvailable = CompareDriver(current, latest.Version) < 0;
        var details =
            $"GPU: {ShortName(gpu.Name)}\n" +
            $"Mevcut sürücü: {current}\n" +
            $"Yeni sürücü: {latest.Version}\n" +
            $"Durum: {(updateAvailable ? "Güncelleme mevcut" : "Güncel")}\n" +
            $"NVIDIA App: {appText}";

        if (updateAvailable) logger.Warning($"NVIDIA sürücü güncellemesi mevcut: {current} → {latest.Version}");
        else logger.Success("Sürücü güncel.");

        return new ModuleResult
        {
            Key = Key,
            Status = updateAvailable ? ComponentStatus.UpdateAvailable : ComponentStatus.UpToDate,
            Summary = updateAvailable ? "Güncelleme mevcut" : "Güncel",
            Details = details,
            ActionableCount = updateAvailable ? 1 : 0,
            Items =
            [
                new UpdateItem
                {
                    Name = $"{gpu.Name} – {latest.Name}",
                    Id = gpu.Name,
                    CurrentVersion = current,
                    NewVersion = latest.Version,
                    UpdateAvailable = updateAvailable,
                    StatusText = updateAvailable ? $"Güncelleme mevcut ({latest.Size})" : "Güncel",
                    Tag = latest.DownloadUrl
                }
            ]
        };
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var item = check.Items.FirstOrDefault(i => i.UpdateAvailable);
        if (item?.Tag is null) return check;

        // --- URL güvenlik denetimi ---
        if (!Uri.TryCreate(item.Tag, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("download.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase)))
        {
            var reason = $"Güvenlik: indirme adresi resmi NVIDIA sunucusu değil, işlem durduruldu ({item.Tag}).";
            logger.Error(reason);
            return ModuleResult.Failed(Key, reason, check.Details);
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RTX Windows Updater", "Downloads");
        var file = Path.Combine(dir, Path.GetFileName(uri.LocalPath));

        try
        {
            Directory.CreateDirectory(dir);

            // --- İndirme ---
            logger.Info($"NVIDIA sürücüsü indiriliyor: {uri}");
            var downloadError = await DownloadAsync(uri, file, ct);
            if (downloadError is not null)
            {
                logger.Error(downloadError);
                return ModuleResult.Failed(Key, downloadError, check.Details);
            }

            // --- İmza doğrulama ---
            var sig = VerifyNvidiaSignature(file);
            if (sig is not null)
            {
                var reason = "Güvenlik: indirilen dosyanın dijital imzası doğrulanamadı – kurulum yapılmadı. " + sig;
                logger.Error(reason);
                return ModuleResult.Failed(Key, reason, check.Details);
            }
            logger.Success("Dijital imza doğrulandı: NVIDIA Corporation.");

            // --- Kurulum ---
            logger.Info("NVIDIA sürücü kurulumu başlatılıyor (sessiz kurulum, otomatik yeniden başlatma YOK). Ekran birkaç kez kararabilir...");
            var run = await ProcessRunner.RunAsync(file, "-s -noreboot", InstallTimeout, CancellationToken.None);
            if (!run.Started || run.TimedOut)
            {
                var reason = ProcessRunner.Describe(run, "NVIDIA kurulum programı");
                logger.Error(reason);
                return ModuleResult.Failed(Key, reason, check.Details);
            }
            logger.Info($"NVIDIA kurulum programı çıkış kodu: {run.ExitCode} ({run.ExitCodeHex})");

            // --- Doğrulama ---
            var gpu = SystemRequirementsChecker.ReadGpus().FirstOrDefault(g => g.IsRtx);
            var now = gpu is null ? null : await ReadInstalledDriverVersionAsync(gpu, CancellationToken.None);
            logger.Info($"Kurulum sonrası sürücü sürümü: {now ?? "okunamadı"}");

            if (now is not null && CompareDriver(now, item.NewVersion) >= 0)
            {
                logger.Success($"NVIDIA sürücüsü güncellendi: {item.CurrentVersion} → {now}");
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.Updated,
                    Summary = "Güncellendi",
                    Details = $"GPU: {ShortName(item.Id)}\nÖnceki sürücü: {item.CurrentVersion}\nYeni sürücü: {now}"
                };
            }

            if (run.ExitCode == 0)
            {
                const string reason = "Kurulum programı başarıyla sonlandı ancak yeni sürücü henüz etkin değil; yeniden başlatma gerekiyor.";
                logger.Warning(reason);
                return new ModuleResult
                {
                    Key = Key,
                    Status = ComponentStatus.RebootRequired,
                    Summary = "Yeniden başlatma gerekiyor",
                    Details = $"GPU: {ShortName(item.Id)}\nMevcut sürücü: {now ?? item.CurrentVersion}\nKurulan sürücü: {item.NewVersion}",
                    Reason = reason,
                    RebootRequired = true
                };
            }

            var fail = $"NVIDIA kurulum programı başarısız oldu (çıkış kodu {run.ExitCode}). Sürücü değişmedi: {now ?? item.CurrentVersion}.";
            logger.Error(fail);
            return ModuleResult.Failed(Key, fail, check.Details);
        }
        catch (Exception ex)
        {
            var reason = "NVIDIA sürücü güncellemesi sırasında hata: " + ex.Message;
            logger.Error(reason);
            return ModuleResult.Failed(Key, reason, check.Details);
        }
        finally
        {
            // Yalnızca bu uygulamanın kendi indirdiği geçici kurulum dosyası temizlenir.
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    logger.Info("Uygulamanın indirdiği geçici NVIDIA kurulum dosyası silindi.");
                }
            }
            catch { /* kilitliyse bir sonraki çalıştırmada üzerine yazılır */ }
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record DriverInfo(string Version, string DownloadUrl, string Name, string ReleaseDate, string Size);

    private async Task<DriverInfo> LookupLatestDriverAsync(string gpuName, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));

        var xml = await http.GetStringAsync(ProductLookupUrl, cts.Token);
        var wanted = Normalize(gpuName);
        var match = XDocument.Parse(xml)
            .Descendants("LookupValue")
            .Select(v => new
            {
                Name = v.Element("Name")?.Value ?? string.Empty,
                Pfid = v.Element("Value")?.Value,
                Psid = v.Attribute("ParentID")?.Value
            })
            .FirstOrDefault(v => Normalize(v.Name) == wanted && v.Pfid is not null && v.Psid is not null);

        if (match is null)
            throw new InvalidOperationException($"'{gpuName}' NVIDIA ürün listesinde eşleştirilemedi; en son sürücü belirlenemedi.");

        logger.Info($"NVIDIA ürün eşleşmesi: {match.Name} (psid={match.Psid}, pfid={match.Pfid})");

        var json = await http.GetStringAsync(string.Format(DriverLookupUrl, match.Psid, match.Pfid), cts.Token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.Str("Success") != "1" || !root.TryGetProperty("IDS", out var ids) ||
            ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
            throw new InvalidOperationException("NVIDIA sürücü servisi bu GPU için sürücü döndürmedi.");

        var info = ids[0].GetProperty("downloadInfo");
        var version = info.Str("Version");
        var url = info.Str("DownloadURL");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("NVIDIA sürücü servisi eksik veri döndürdü.");

        return new DriverInfo(
            version.Trim(),
            url.Trim(),
            Uri.UnescapeDataString(info.Str("Name") ?? "NVIDIA sürücüsü"),
            info.Str("ReleaseDateTime") ?? string.Empty,
            info.Str("DownloadURLFileSize") ?? string.Empty);
    }

    private static string Normalize(string name)
    {
        var n = Regex.Replace(name, @"\s+", " ").Trim();
        if (n.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) n = n[7..];
        return n.ToUpperInvariant();
    }

    private static string ShortName(string name) =>
        Regex.Replace(name, @"^NVIDIA\s+(GeForce\s+)?", string.Empty, RegexOptions.IgnoreCase);

    private async Task<string?> ReadInstalledDriverVersionAsync(GpuInfo gpu, CancellationToken ct)
    {
        if (File.Exists(NvidiaSmiPath))
        {
            var r = await ProcessRunner.RunAsync(NvidiaSmiPath,
                "--query-gpu=name,driver_version --format=csv,noheader", TimeSpan.FromSeconds(30), ct);
            if (r.Succeeded)
            {
                var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var line = lines.FirstOrDefault(l => l.StartsWith(gpu.Name, StringComparison.OrdinalIgnoreCase)) ??
                           lines.FirstOrDefault();
                var v = line?.Split(',').LastOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(v) && Regex.IsMatch(v, @"^\d+\.\d+$")) return v;
            }
            else
            {
                logger.Warning("nvidia-smi çalıştırılamadı: " + ProcessRunner.Describe(r, "nvidia-smi"));
            }
        }

        // WMI biçimi: 32.0.16.1714 → son 5 hane "61714" → 617.14
        var digits = new string(gpu.WmiDriverVersion.Where(char.IsDigit).ToArray());
        if (digits.Length >= 5)
        {
            var last5 = digits[^5..];
            return $"{int.Parse(last5[..3])}.{last5[3..]}";
        }
        return null;
    }

    private static int CompareDriver(string a, string b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)) return va.CompareTo(vb);
        if (decimal.TryParse(a, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var da) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var db))
            return da.CompareTo(db);
        return string.CompareOrdinal(a, b);
    }

    private static (bool Installed, string? Version) DetectNvidiaApp()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];
        foreach (var root in roots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var s = key.OpenSubKey(sub);
                    var name = s?.GetValue("DisplayName") as string;
                    if (name is not null && name.Trim().Equals("NVIDIA App", StringComparison.OrdinalIgnoreCase))
                        return (true, s!.GetValue("DisplayVersion") as string);
                }
            }
            catch { /* erişilemeyen anahtar */ }
        }

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe");
        if (!File.Exists(exe)) return (false, null);
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        return (true, string.IsNullOrWhiteSpace(fileVersion) ? null : fileVersion);
    }

    private async Task<string?> DownloadAsync(Uri uri, string file, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return $"NVIDIA sunucusu indirmeyi reddetti: HTTP {(int)response.StatusCode}.";

            var total = response.Content.Headers.ContentLength;
            if (total is > 0)
            {
                var drive = new DriveInfo(Path.GetPathRoot(file)!);
                if (drive.AvailableFreeSpace < total.Value * 3)
                    return $"Yetersiz disk alanı: sürücü paketi için yaklaşık {total.Value * 3 / 1048576} MB boş alan gerekli.";
            }

            await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            var buffer = new byte[1 << 20];
            long read = 0;
            var nextReport = 10;
            int n;
            while ((n = await input.ReadAsync(buffer, cts.Token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                read += n;
                if (total is > 0)
                {
                    var pct = (int)(read * 100 / total.Value);
                    if (pct >= nextReport)
                    {
                        logger.Info($"  İndiriliyor: %{pct} ({read / 1048576} / {total.Value / 1048576} MB)");
                        nextReport = pct / 10 * 10 + 10;
                    }
                }
            }

            if (total is > 0 && read != total.Value)
                return $"İndirme eksik kaldı ({read} / {total.Value} bayt).";

            logger.Success($"İndirme tamamlandı ({read / 1048576} MB).");
            return null;
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested ? "İndirme iptal edildi." : "İndirme zaman aşımına uğradı.";
        }
        catch (HttpRequestException ex)
        {
            return $"İndirme başarısız (ağ hatası): {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"İndirme dosyası yazılamadı: {ex.Message}";
        }
    }

    // --- Authenticode (WinVerifyTrust) ---

    /// <returns>null = imza geçerli ve NVIDIA Corporation'a ait; aksi hâlde hata açıklaması.</returns>
    private static string? VerifyNvidiaSignature(string path)
    {
        var trust = WinTrust.Verify(path);
        if (trust != 0)
            return $"WinVerifyTrust sonucu 0x{unchecked((uint)trust):X8}.";

        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var org = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!cert.Subject.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase))
                return $"Dosya NVIDIA tarafından imzalanmamış (imzalayan: {org}).";
            return null;
        }
        catch (Exception ex)
        {
            return "İmza sertifikası okunamadı: " + ex.Message;
        }
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WinTrustData pWvtData);

        public static int Verify(string path)
        {
            var pathPtr = Marshal.StringToHGlobalUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPtr
            };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, filePtr, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = filePtr,
                    dwStateAction = 1        // WTD_STATEACTION_VERIFY
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(filePtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }
    }
}

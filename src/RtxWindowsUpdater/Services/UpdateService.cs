using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services;

/// <summary>Yayımlanmış yeni sürüm (GitHub Releases API'sinin gerçek yanıtından).</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    DateTimeOffset? PublishedAt,
    Uri PageUrl,
    string SetupName,
    Uri SetupUrl,
    long SetupSize,
    string Sha256);

/// <summary>Denetim sonucu: Success=false → denetlenemedi (Message gerçek neden); Update=null → güncel.</summary>
public sealed record UpdateCheckResult(bool Success, UpdateInfo? Update, string Message);

/// <summary>
/// Uygulama içi güncelleme. Kaynak kod deposu özel olduğu için sürümler ayrı, herkese açık sürüm deposundan okunur
/// (<see cref="AppInfo.ReleasesRepository"/>; GitHub Actions her etiketle kurulum dosyasını oraya da yayınlar). Denetim anonimdir
/// (GitHub API, IP başına saatte 60 istek). İndirilen kurulum dosyası yalnızca şu üç doğrulamadan geçerse kullanılır:
/// GitHub'ın bildirdiği boyut, GitHub'ın bildirdiği SHA-256 özeti ve EXE'nin içindeki ürün adı + sürüm (etiketle aynı olmalı).
/// </summary>
public sealed class UpdateService(Uri latestReleaseUrl, Action<string> log)
{
    /// <summary>İndirilen kurulum dosyalarının klasör adı (%TEMP% altında, yöneticiyken C:\ProgramData altında önek).</summary>
    public const string DownloadFolderName = AppInfo.Name + " Güncelleme";

    public static Uri DefaultLatestReleaseUrl { get; } = new($"https://api.github.com/repos/{AppInfo.ReleasesRepository}/releases/latest");

    private static readonly Regex SetupAssetName = new(@"^E-mre-Control-Center-Setup-v?\d+\.\d+\.\d+\.exe$", RegexOptions.IgnoreCase);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(CheckTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, ct);
            var code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Fail($"Sürüm deposunda yayımlanmış sürüm bulunamadı (HTTP 404: {AppInfo.ReleasesRepository}).");
            if (code is 403 or 429 && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            {
                var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var r) && long.TryParse(r.FirstOrDefault(), out var epoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("HH:mm")
                    : "bir süre";
                return Fail($"GitHub istek sınırı doldu (HTTP {code}); {reset} sonra yeniden denenebilir.");
            }
            if (code != 200) return Fail($"GitHub beklenmeyen yanıt verdi (HTTP {code}).");

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            var tag = Str(root, "tag_name") ?? throw new FormatException("tag_name yok");
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                return Fail($"Sürüm etiketi okunamadı: \"{tag}\".");
            if (Normalize(version) <= Normalize(current))
            {
                log($"Güncelleme denetimi: güncel (yüklü {current.ToString(3)}, son yayın {tag}).");
                return new UpdateCheckResult(true, null, $"Güncel (son yayın {tag})");
            }

            var asset = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray().FirstOrDefault(a => SetupAssetName.IsMatch(Str(a, "name") ?? ""))
                : default;
            if (asset.ValueKind != JsonValueKind.Object)
                return Fail($"Yeni sürüm {tag} yayımlanmış ama kurulum dosyası (E-mre-Control-Center-Setup-{tag}.exe) bulunamadı.");
            var digest = Str(asset, "digest");
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
                return Fail($"Yeni sürüm {tag} bulundu ama GitHub dosyanın SHA-256 özetini bildirmedi; doğrulanamayan dosya indirilmez.");
            // İndirme yalnızca HTTPS'ten (yerel testlerde 127.0.0.1 sahte sunucusu); dosya ayrıca SHA-256 ile doğrulanır.
            if (!Uri.TryCreate(Str(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps &&
                !url.IsLoopback)
                return Fail("Kurulum dosyasının indirme adresi geçersiz.");
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            if (size <= 0) return Fail("Kurulum dosyasının boyutu bildirilmedi.");

            var info = new UpdateInfo(
                Normalize(version), tag, CleanNotes(Str(root, "body") ?? ""),
                DateTimeOffset.TryParse(Str(root, "published_at"), out var published) ? published : null,
                Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var page) ? page : latestReleaseUrl,
                Str(asset, "name")!, url, size, digest[7..].ToUpperInvariant());
            log($"Güncelleme denetimi: yeni sürüm {tag} (yüklü {current.ToString(3)}; {info.SetupName}, {size / 1048576.0:0.0} MB).");
            return new UpdateCheckResult(true, info, $"Yeni sürüm: {tag}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"GitHub {CheckTimeout.TotalSeconds:0} saniye içinde yanıt vermedi.");
        }
        catch (HttpRequestException ex)
        {
            return Fail("İnternete bağlanılamadı: " + ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Fail("GitHub yanıtı okunamadı: " + ex.Message);
        }

        // Denetlenemedi sonucunu çağıran taraf günlüğe uyarı olarak yazar (burada yazılırsa satır iki kez görünür).
        static UpdateCheckResult Fail(string message) => new(false, null, message);
    }

    /// <summary>
    /// Kurulum dosyasını indirir ve doğrular (boyut, SHA-256, ürün adı, sürüm). Doğrulanamazsa dosya silinir ve hata fırlatılır.
    /// Yönetici olarak çalışırken yalnızca Yöneticiler + SYSTEM erişimli klasöre (C:\ProgramData) indirilir: doğrulamadan sonra
    /// çalıştırılana kadar standart kullanıcı yetkisiyle değiştirilemez.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string folder, bool protectFolder, IProgress<(long Done, long Total)>? progress,
        CancellationToken ct = default)
    {
        if (!SetupAssetName.IsMatch(info.SetupName)) throw new InvalidDataException("Kurulum dosyasının adı beklenen biçimde değil.");
        if (protectFolder) ProtectedDirectory.Create(folder);
        else Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, info.SetupName);
        log($"Güncelleme indiriliyor: {info.SetupUrl} → {path}");
        try
        {
            using var http = CreateClient(TimeSpan.FromMinutes(15));
            using var response = await http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"İndirme başarısız (HTTP {(int)response.StatusCode}).");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 17];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (done + read > info.SetupSize) throw new InvalidDataException("İndirilen dosya bildirilen boyuttan büyük.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((done, info.SetupSize));
                }
                await output.FlushAsync(ct);
            }
            if (done != info.SetupSize)
                throw new InvalidDataException($"İndirilen dosya eksik ({done} / {info.SetupSize} bayt).");
            var sha = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 özeti GitHub'ın bildirdiğiyle eşleşmedi ({sha[..12]}… ≠ {info.Sha256[..12]}…).");

            var fileInfo = FileVersionInfo.GetVersionInfo(path);
            if (fileInfo.ProductName != AppInfo.Name)
                throw new InvalidDataException($"İndirilen dosya {AppInfo.Name} kurulum dosyası değil (ürün: {fileInfo.ProductName ?? "yok"}).");
            var fileVersion = new Version(fileInfo.FileMajorPart, fileInfo.FileMinorPart, fileInfo.FileBuildPart);
            if (fileVersion != info.Version)
                throw new InvalidDataException($"Kurulum dosyasının sürümü {fileVersion}, beklenen {info.Version}.");

            log($"Güncelleme indirildi ve doğrulandı: {path} ({done} bayt, SHA-256 {sha}).");
            return path;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sonraki açılışta temizlenir */ }
            throw;
        }
    }

    /// <summary>Önceki güncellemelerden kalan indirme klasörlerini siler (kullanımdakiler bırakılır; yalnızca bu uygulamanın öneki).</summary>
    public void CleanupDownloads(IEnumerable<string> roots)
    {
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root, DownloadFolderName + "*"))
            {
                try
                {
                    Directory.Delete(dir, true); // .NET bağlantıları (junction) izlemez, yalnızca bağlantıyı siler
                    log("Eski güncelleme indirmesi silindi: " + dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // kurulum hâlâ çalışıyor olabilir; bir sonraki açılışta yeniden denenir
                }
            }
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"E-mre-Control-Center/{AppInfo.Version}");
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Sürüm notları (Markdown) → düz metin: kalın / kod işaretleri kaldırılır, maddeler "•" olur; en fazla 4000 karakter.</summary>
    internal static string CleanNotes(string markdown)
    {
        var text = markdown.Replace("\r", "");
        text = Regex.Replace(text, @"\n[ \t]{2,}(?![-*] )", " "); // README'de alt satıra kayan madde devamı → aynı satır
        text = Regex.Replace(text, @"\*\*|__|`", "");
        text = Regex.Replace(text, @"^([ \t]*)[-*] ", "$1• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^#+[ \t]*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text.Length > 4000 ? text[..4000] + "…" : text;
    }
}

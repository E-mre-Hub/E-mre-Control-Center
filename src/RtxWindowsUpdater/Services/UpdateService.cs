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
/// <param name="NotModified">GitHub "değişmedi" (HTTP 304) dedi; sonuç bir önceki gerçek yanıttan.</param>
/// <param name="RetryAt">GitHub istek sınırı doldu: bu zamandan önce yeniden denenmemeli.</param>
public sealed record UpdateCheckResult(bool Success, UpdateInfo? Update, string Message, bool NotModified = false, DateTimeOffset? RetryAt = null);

/// <summary>
/// Uygulama içi güncelleme. Sürümler herkese açık ana deponun GitHub Releases'ından okunur (<see cref="AppInfo.ReleasesRepository"/>;
/// GitHub Actions her etiketle kurulum dosyasını ve CHANGELOG.md'deki sürüm notunu oraya yayınlar). Denetim anonimdir
/// (GitHub API, IP başına saatte 60 istek). İndirilen kurulum dosyası yalnızca şu üç doğrulamadan geçerse kullanılır:
/// GitHub'ın bildirdiği boyut, GitHub'ın bildirdiği SHA-256 özeti ve EXE'nin içindeki ürün adı + sürüm (etiketle aynı olmalı).
/// </summary>
public sealed class UpdateService(Uri latestReleaseUrl, Action<string> log)
{
    /// <summary>İndirilen kurulum dosyalarının klasör adı (%TEMP% altında, yöneticiyken C:\ProgramData altında önek).</summary>
    public const string DownloadFolderName = AppInfo.Name + " Güncelleme";

    public static Uri DefaultLatestReleaseUrl { get; } = new($"https://api.github.com/repos/{AppInfo.ReleasesRepository}/releases/latest");

    /// <summary>
    /// Yayındaki kurulum dosyaları (v2.0.0): "E-mre-Control-Center-Setup-TR-vX.Y.Z.exe" (Türkçe), "…-Setup-EN-vX.Y.Z.exe" (English) ve
    /// eski adlı "…-Setup-vX.Y.Z.exe" (v1.9.3 ve önceki sürümlerin uygulama içi güncellemesi YALNIZCA bu adı arar; uyumluluk kopyası).
    /// Üçü aynı EXE'dir; uygulama seçili dildekini, yoksa Türkçeyi, yoksa eski adlıyı indirir (dil ayrıca "--lang" ile iletilir).
    /// </summary>
    private static readonly Regex SetupAssetName = new(@"^E-mre-Control-Center-Setup(?:-(TR|EN))?-v?\d+\.\d+\.\d+\.exe$", RegexOptions.IgnoreCase);

    /// <summary>Kurulum dosyası önceliği: seçili dil 3, Türkçe 2, eski ad 1, diğer dil 0; kurulum dosyası değilse -1.</summary>
    internal static int SetupAssetRank(string name)
    {
        var m = SetupAssetName.Match(name);
        if (!m.Success) return -1;
        var lang = m.Groups[1].Value;
        if (lang.Length == 0) return 1;
        if (lang.Equals(L.Code, StringComparison.OrdinalIgnoreCase)) return 3;
        return lang.Equals("TR", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    // Koşullu istek: son gerçek yanıtın ETag'i gönderilir; yayın değişmediyse GitHub 304 döner ve bu istek IP başına saatlik
    // 60 isteklik sınırdan DÜŞMEZ (uygulama açıkken düzenli denetim için). Sonuç, o ETag'e ait gerçek yanıttan üretilmiş sonuçtur.
    private readonly object _cacheLock = new();
    private string? _etag;
    private UpdateCheckResult? _etagResult;
    private string? _lastLogged;

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            string? etag;
            UpdateCheckResult? cached;
            lock (_cacheLock)
            {
                etag = _etag;
                cached = _etagResult;
            }
            using var http = CreateClient(CheckTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (etag is not null && cached is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await http.SendAsync(request, ct);
            var code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                return cached with { NotModified = true };
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Fail(L.T($"Sürüm deposunda yayımlanmış sürüm bulunamadı (HTTP 404: {AppInfo.ReleasesRepository}).", $"No published version was found in the release repository (HTTP 404: {AppInfo.ReleasesRepository})."));
            if (code is 403 or 429 && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            {
                var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var r) && long.TryParse(r.FirstOrDefault(), out var epoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("HH:mm")
                    : L.T("bir süre", "a while");
                return new UpdateCheckResult(false, null, L.T($"GitHub istek sınırı doldu (HTTP {code}); {reset} sonra yeniden denenebilir.", $"The GitHub request limit has been reached (HTTP {code}); it can be retried after {reset}."),
                    RetryAt: long.TryParse(response.Headers.TryGetValues("x-ratelimit-reset", out var rr) ? rr.FirstOrDefault() : null, out var at)
                        ? DateTimeOffset.FromUnixTimeSeconds(at)
                        : DateTimeOffset.Now.AddMinutes(15));
            }
            if (code != 200) return Fail(L.T($"GitHub beklenmeyen yanıt verdi (HTTP {code}).", $"GitHub returned an unexpected response (HTTP {code})."));

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            var tag = Str(root, "tag_name") ?? throw new FormatException(L.T("tag_name yok", "no tag_name"));
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                return Fail(L.T($"Sürüm etiketi okunamadı: \"{tag}\".", $"Could not read the version tag: \"{tag}\"."));
            if (Normalize(version) <= Normalize(current))
            {
                LogOnce(L.T($"Güncelleme denetimi: güncel (yüklü {current.ToString(3)}, son yayın {tag}).", $"Update check: up to date (installed {current.ToString(3)}, latest release {tag})."));
                return Remember(response, new UpdateCheckResult(true, null, L.T($"Güncel (son yayın {tag})", $"Up to date (latest release {tag})")));
            }

            var asset = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray().Where(a => SetupAssetRank(Str(a, "name") ?? "") >= 0)
                    .OrderByDescending(a => SetupAssetRank(Str(a, "name")!)).FirstOrDefault()
                : default;
            if (asset.ValueKind != JsonValueKind.Object)
                return Fail(L.T($"Yeni sürüm {tag} yayımlanmış ama kurulum dosyası (E-mre-Control-Center-Setup-TR-{tag}.exe) bulunamadı.", $"New version {tag} has been released but the setup file (E-mre-Control-Center-Setup-EN-{tag}.exe) was not found."));
            var digest = Str(asset, "digest");
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
                return Fail(L.T($"Yeni sürüm {tag} bulundu ama GitHub dosyanın SHA-256 özetini bildirmedi; doğrulanamayan dosya indirilmez.", $"New version {tag} was found but GitHub did not report the file's SHA-256 hash; an unverifiable file is not downloaded."));
            // İndirme yalnızca HTTPS'ten (yerel testlerde 127.0.0.1 sahte sunucusu); dosya ayrıca SHA-256 ile doğrulanır.
            if (!Uri.TryCreate(Str(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps &&
                !url.IsLoopback)
                return Fail(L.T("Kurulum dosyasının indirme adresi geçersiz.", "The download address of the setup file is invalid."));
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            if (size <= 0) return Fail(L.T("Kurulum dosyasının boyutu bildirilmedi.", "The size of the setup file was not reported."));

            var info = new UpdateInfo(
                Normalize(version), tag, CleanNotes(Str(root, "body") ?? ""),
                DateTimeOffset.TryParse(Str(root, "published_at"), out var published) ? published : null,
                Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var page) ? page : latestReleaseUrl,
                Str(asset, "name")!, url, size, digest[7..].ToUpperInvariant());
            LogOnce(L.T($"Güncelleme denetimi: yeni sürüm {tag} (yüklü {current.ToString(3)}; {info.SetupName}, {size / 1048576.0:0.0} MB).", $"Update check: new version {tag} (installed {current.ToString(3)}; {info.SetupName}, {size / 1048576.0:0.0} MB)."));
            return Remember(response, new UpdateCheckResult(true, info, L.T($"Yeni sürüm: {tag}", $"New version: {tag}")));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(L.T($"GitHub {CheckTimeout.TotalSeconds:0} saniye içinde yanıt vermedi.", $"GitHub did not respond within {CheckTimeout.TotalSeconds:0} seconds."));
        }
        catch (HttpRequestException ex)
        {
            return Fail(L.T("İnternete bağlanılamadı: ", "Could not connect to the internet: ") + ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Fail(L.T("GitHub yanıtı okunamadı: ", "Could not read the GitHub response: ") + ex.Message);
        }

        // Denetlenemedi sonucunu çağıran taraf günlüğe uyarı olarak yazar (burada yazılırsa satır iki kez görünür).
        static UpdateCheckResult Fail(string message) => new(false, null, message);
    }

    /// <summary>Başarılı gerçek yanıtın sonucunu ETag'iyle saklar (sonraki koşullu istek için).</summary>
    private UpdateCheckResult Remember(HttpResponseMessage response, UpdateCheckResult result)
    {
        lock (_cacheLock)
        {
            _etag = response.Headers.ETag?.ToString();
            _etagResult = _etag is null ? null : result;
        }
        return result;
    }

    /// <summary>Düzenli denetimde aynı sonuç her seferinde günlüğe yazılmaz; yalnızca değişince yazılır.</summary>
    private void LogOnce(string line)
    {
        lock (_cacheLock)
        {
            if (line == _lastLogged) return;
            _lastLogged = line;
        }
        log(line);
    }

    /// <summary>
    /// Kurulum dosyasını indirir ve doğrular (boyut, SHA-256, ürün adı, sürüm). Doğrulanamazsa dosya silinir ve hata fırlatılır.
    /// Yönetici olarak çalışırken yalnızca Yöneticiler + SYSTEM erişimli klasöre (C:\ProgramData) indirilir: doğrulamadan sonra
    /// çalıştırılana kadar standart kullanıcı yetkisiyle değiştirilemez.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string folder, bool protectFolder, IProgress<(long Done, long Total)>? progress,
        CancellationToken ct = default)
    {
        if (!SetupAssetName.IsMatch(info.SetupName)) throw new InvalidDataException(L.T("Kurulum dosyasının adı beklenen biçimde değil.", "The setup file name is not in the expected format."));
        if (protectFolder) ProtectedDirectory.Create(folder);
        else Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, info.SetupName);
        log(L.T($"Güncelleme indiriliyor: {info.SetupUrl} → {path}", $"Downloading the update: {info.SetupUrl} → {path}"));
        try
        {
            using var http = CreateClient(TimeSpan.FromMinutes(15));
            using var response = await http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException(L.T($"İndirme başarısız (HTTP {(int)response.StatusCode}).", $"Download failed (HTTP {(int)response.StatusCode})."));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 17];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (done + read > info.SetupSize) throw new InvalidDataException(L.T("İndirilen dosya bildirilen boyuttan büyük.", "The downloaded file is larger than the reported size."));
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((done, info.SetupSize));
                }
                await output.FlushAsync(ct);
            }
            if (done != info.SetupSize)
                throw new InvalidDataException(L.T($"İndirilen dosya eksik ({done} / {info.SetupSize} bayt).", $"The downloaded file is incomplete ({done} / {info.SetupSize} bytes)."));
            var sha = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T($"SHA-256 özeti GitHub'ın bildirdiğiyle eşleşmedi ({sha[..12]}… ≠ {info.Sha256[..12]}…).", $"The SHA-256 hash did not match the one reported by GitHub ({sha[..12]}… ≠ {info.Sha256[..12]}…)."));

            var fileInfo = FileVersionInfo.GetVersionInfo(path);
            if (fileInfo.ProductName != AppInfo.Name)
                throw new InvalidDataException(L.T($"İndirilen dosya {AppInfo.Name} kurulum dosyası değil (ürün: {fileInfo.ProductName ?? "yok"}).", $"The downloaded file is not an {AppInfo.Name} setup file (product: {fileInfo.ProductName ?? "none"})."));
            var fileVersion = new Version(fileInfo.FileMajorPart, fileInfo.FileMinorPart, fileInfo.FileBuildPart);
            if (fileVersion != info.Version)
                throw new InvalidDataException(L.T($"Kurulum dosyasının sürümü {fileVersion}, beklenen {info.Version}.", $"The setup file version is {fileVersion}, expected {info.Version}."));

            log(L.T($"Güncelleme indirildi ve doğrulandı: {path} ({done} bayt, SHA-256 {sha}).", $"Update downloaded and verified: {path} ({done} bytes, SHA-256 {sha})."));
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
                    log(L.T("Eski güncelleme indirmesi silindi: ", "Old update download deleted: ") + dir);
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
        // v2.0.0: iki dilli sürüm notu. CHANGELOG bölümünde "**English**" satırından öncesi Türkçe, sonrası İngilizce; pencere yalnızca
        // seçili dildekini gösterir (GitHub sayfasında ve eski sürümlerin penceresinde ikisi de, "English" başlığıyla ayrılmış görünür).
        // "<!-- en -->" de kabul edilir. İşaret yoksa tamamı.
        var marker = Regex.Match(text, @"^[ \t]*(?:<!--[ \t]*en[ \t]*-->|\*\*English\*\*)[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (marker.Success)
            text = L.En ? text[(marker.Index + marker.Length)..] : text[..marker.Index];
        text = Regex.Replace(text, @"^[ \t]*<!--.*?-->[ \t]*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[ \t]*-{3,}[ \t]*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n[ \t]{2,}(?![-*] )", " "); // sürüm notunda alt satıra kayan madde devamı → aynı satır
        text = Regex.Replace(text, @"\*\*|__|`", "");
        // GitHub'ın otomatik eklediği satırlar ("Full Changelog: https://…", "What's Changed") pencerede gösterilmez.
        text = Regex.Replace(text, @"^(Full Changelog|What's Changed)\b.*(\n|$)", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^([ \t]*)[-*] ", "$1• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^#+[ \t]*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text.Length > 4000 ? text[..4000] + "…" : text;
    }
}

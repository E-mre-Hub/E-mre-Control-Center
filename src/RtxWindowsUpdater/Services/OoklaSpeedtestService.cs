using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services;

/// <summary>Ookla Speedtest sunucusu (aracın "yakın sunucular" listesinden).</summary>
public sealed record OoklaServer(int Id, string Sponsor, string Location, string Country, string Host);

/// <summary>Bulunan ve doğrulanan Ookla aracı.</summary>
public sealed record OoklaCli(string Path, string Version);

/// <summary>
/// Ookla'nın resmi komut satırı aracı (Speedtest CLI, winget paketi <see cref="PackageId"/>) ile hız testi.
/// Araç yalnızca kullanıcının onayıyla winget'ten kurulur; lisans / gizlilik koşulları uygulamada kabul edilmeden
/// araç çalıştırılmaz (lisans bayrakları yalnızca kabulden sonra gönderilir). Tüm değerler aracın JSON çıktısından okunur.
/// Çıktıdaki yerel IP ve MAC adresi okunmaz, gösterilmez, günlüğe yazılmaz.
/// </summary>
public sealed class OoklaSpeedtestService(Logger logger)
{
    public const string PackageId = "Ookla.Speedtest.CLI";

    /// <summary>Aracın kendi lisans bildirimi (değiştirilmeden gösterilir).</summary>
    public const string LicenseNotice =
        "You may only use this Speedtest software and information generated from it for personal, non-commercial use, " +
        "through a command line interface on a personal computer. Your use of this software is subject to the End User " +
        "License Agreement, Terms of Use and Privacy Policy at these URLs:";

    public static readonly IReadOnlyList<(string Title, string Url)> LicenseLinks =
    [
        ("Son Kullanıcı Lisans Sözleşmesi", "https://www.speedtest.net/about/eula"),
        ("Kullanım Koşulları", "https://www.speedtest.net/about/terms"),
        ("Gizlilik Politikası", "https://www.speedtest.net/about/privacy")
    ];

    private static readonly string[] AcceptArgs = ["--accept-license", "--accept-gdpr"];
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    // ------------------------------------------------------------------ araç bulma / kurma

    /// <summary>Aday konumlar: winget (kullanıcı / makine) paket klasörü, winget kısayolu, PATH.</summary>
    private static IEnumerable<string> Candidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[] { Path.Combine(local, "Microsoft", "WinGet", "Packages"), Path.Combine(programFiles, "WinGet", "Packages") })
        {
            string[] dirs;
            try { dirs = Directory.Exists(root) ? Directory.GetDirectories(root, PackageId + "_*") : []; }
            catch { dirs = []; }
            foreach (var d in dirs) yield return Path.Combine(d, "speedtest.exe");
        }
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "speedtest.exe");
        yield return Path.Combine(programFiles, "WinGet", "Links", "speedtest.exe");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string p;
            try { p = Path.Combine(dir.Trim(), "speedtest.exe"); }
            catch { continue; }
            yield return p;
        }
    }

    /// <summary>
    /// Kurulu Ookla aracını bulur ve "--version" çıktısıyla doğrular ("Speedtest by Ookla …"). Aynı adlı başka bir araç
    /// (ör. Python speedtest-cli) kabul edilmez. Bulunamazsa null.
    /// </summary>
    public async Task<OoklaCli?> LocateAsync(CancellationToken ct)
    {
        foreach (var path in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var r = await ProcessRunner.RunAsync(path, "--version", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            if (r.Succeeded && line.StartsWith("Speedtest by Ookla", StringComparison.Ordinal))
            {
                var version = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3) ?? line;
                return new OoklaCli(path, version);
            }
            logger.Warning($"Ookla aracı adayı reddedildi ({path}): " + (r.Succeeded ? $"beklenmeyen sürüm çıktısı \"{line}\"" : ProcessRunner.Describe(r, "speedtest")));
        }
        return null;
    }

    /// <summary>
    /// Aracı winget ile kurar (tam paket kimliği, winget kaynağı, kullanıcı kapsamı). Başarı yalnızca kurulumdan sonra
    /// winget listesinde paket görünür VE araç bulunup doğrulanırsa kabul edilir.
    /// </summary>
    public async Task<(OoklaCli? Cli, string Message)> InstallAsync(CancellationToken ct)
    {
        var winget = WingetManager.LocateWinget();
        if (winget is null) return (null, "winget bulunamadı (Uygulama Yükleyicisi kurulu değil); araç kurulamadı.");
        logger.Info($"Ookla Speedtest aracı kuruluyor: winget install --id {PackageId} --exact --source winget --scope user");
        var install = await ProcessRunner.RunCmdAsync(winget,
            ["install", "--id", PackageId, "--exact", "--source", "winget", "--scope", "user",
             "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(5), ct, onStdOut: Forward, onStdErr: Forward).ConfigureAwait(false);
        if (install.Cancelled) return (null, "Kurulum iptal edildi.");

        var list = await ProcessRunner.RunCmdAsync(winget,
            ["list", "--id", PackageId, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var listed = list.Succeeded && list.StdOut.Contains(PackageId, StringComparison.OrdinalIgnoreCase);
        var cli = await LocateAsync(ct).ConfigureAwait(false);
        if (listed && cli is not null)
        {
            var msg = $"Ookla Speedtest aracı kuruldu ve doğrulandı: {PackageId} {cli.Version} ({cli.Path}).";
            logger.Success(msg);
            return (cli, msg);
        }

        var reason = !install.Succeeded
            ? WingetManager.DescribeFailure(install)
            : !listed ? "winget kurulumu bildirdi ancak paket kurulu paketler listesinde görünmüyor."
            : "paket kurulu görünüyor ancak speedtest.exe bulunamadı veya doğrulanamadı.";
        logger.Error("Ookla Speedtest aracı kurulamadı: " + reason);
        return (null, "Kurulamadı: " + reason);

        void Forward(string line)
        {
            var clean = WingetTableParser.CleanLine(line);
            if (!string.IsNullOrWhiteSpace(clean)) logger.Output("  " + clean.Trim());
        }
    }

    // ------------------------------------------------------------------ sunucu listesi

    /// <summary>Aracın en yakın sunucular listesi ("--servers"). Hata durumunda aracın gerçek mesajı döner.</summary>
    public async Task<(List<OoklaServer>? Servers, string? Error)> ListServersAsync(string exe, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, string.Join(' ', ["--servers", "--format=json", .. AcceptArgs]),
            TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!r.Succeeded) return (null, ErrorText(r));
        try
        {
            var servers = ParseServers(r.StdOut);
            logger.Info($"Ookla sunucu listesi alındı: {servers.Count} sunucu (" +
                        string.Join(", ", servers.Take(4).Select(s => $"{s.Location} - {s.Sponsor}")) + (servers.Count > 4 ? ", …" : "") + ").");
            return servers.Count == 0 ? (null, "Ookla yakında sunucu bildirmedi.") : (servers, null);
        }
        catch (JsonException ex)
        {
            return (null, "Sunucu listesi yorumlanamadı: " + ex.Message);
        }
    }

    internal static List<OoklaServer> ParseServers(string json)
    {
        using var doc = JsonDocument.Parse(json.Trim());
        var list = new List<OoklaServer>();
        if (!doc.RootElement.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in servers.EnumerateArray())
        {
            if (!s.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var sid)) continue;
            list.Add(new OoklaServer(sid, Str(s, "name"), Str(s, "location"), Str(s, "country"), Str(s, "host")));
        }
        return list;
    }

    // ------------------------------------------------------------------ test

    /// <summary>
    /// Testi çalıştırır: "--format=jsonl" olayları (testStart / ping / download / upload / result / log) satır satır okunur.
    /// <paramref name="serverId"/> null ise sunucuyu Ookla seçer (Otomatik).
    /// </summary>
    public async Task<SpeedTestResult> RunAsync(string exe, int? serverId, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var parser = new OoklaEventParser(progress);
        var args = new List<string> { "--format=jsonl", "--progress=yes", "--progress-update-interval=100" };
        if (serverId is { } id) args.Add("--server-id=" + id.ToString(CultureInfo.InvariantCulture));
        args.AddRange(AcceptArgs);
        logger.Info($"Hız testi başladı: {SpeedTestProviders.Ookla}, sunucu {(serverId is { } sid ? sid.ToString(CultureInfo.InvariantCulture) : "otomatik")}.");
        progress?.Report(new SpeedTestProgress(SpeedTestPhase.Connecting, 0, null, null, null));

        var r = await ProcessRunner.RunAsync(exe, string.Join(' ', args), TestTimeout, ct,
            onStdOut: parser.OnLine, onStdErr: parser.OnLine).ConfigureAwait(false);
        var result = parser.Build(total.Elapsed);

        if (r.Cancelled || ct.IsCancellationRequested)
        {
            result.Phase = SpeedTestPhase.Cancelled;
            result.Error = "Test iptal edildi.";
            logger.Warning($"Hız testi iptal edildi ({result.Duration.TotalSeconds:0} sn).");
            return result;
        }
        if (!r.Succeeded || !parser.HasResult)
        {
            result.Phase = SpeedTestPhase.Failed;
            result.Error = parser.Errors.Count > 0 ? string.Join(" · ", parser.Errors)
                : !r.Succeeded ? ErrorText(r)
                : "Ookla aracı sonuç bildirmedi.";
            logger.Error("Hız testi başarısız: " + result.Error);
            return result;
        }

        result.Phase = SpeedTestPhase.Completed;
        logger.Success($"Hız testi tamamlandı ({result.Duration.TotalSeconds:0} sn, {SpeedTestProviders.Ookla}): sunucu {result.Server?.ServerName} " +
                       $"(id {result.Server?.ServerId}), indirme {result.Download?.Mbps:0.00} Mbps, yükleme {result.Upload?.Mbps:0.00} Mbps, " +
                       $"ping {result.IdleLatency?.MedianMs:0.0} ms, paket kaybı " +
                       (result.PacketLoss?.LossPercent is { } l ? $"%{l:0.#}" : "ölçülemedi") +
                       $", kullanılan veri {result.DataUsedBytes / 1_000_000.0:0.0} MB" + (result.ResultUrl is { } u ? $", sonuç {u}" : ""));
        return result;
    }

    /// <summary>Aracın hata metni: JSON "log" satırları (hata / uyarı) varsa onlar, yoksa lisans bildirimi dışındaki son satır.</summary>
    private static string ErrorText(ProcessResult r)
    {
        if (!r.Started || r.TimedOut) return ProcessRunner.Describe(r, "speedtest");
        var logs = (r.StdErr + "\n" + r.StdOut).Split('\n')
            .Select(OoklaEventParser.LogMessage).Where(m => m is not null).Cast<string>().ToList();
        if (logs.Count > 0) return $"Ookla aracı hata bildirdi (çıkış kodu {r.ExitCode}): {string.Join(" · ", logs)}";
        if (r.StdErr.Contains("--accept-license", StringComparison.Ordinal))
            return "Ookla lisansı kabul edilmediği için araç çalışmadı.";
        var line = (r.StdErr + "\n" + r.StdOut).Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 2 && l.Any(char.IsLetter) && !l.StartsWith("http", StringComparison.Ordinal) && !l.All(c => c == '='));
        return line is null ? $"Ookla aracı {r.ExitCode} çıkış koduyla sonlandı." : $"Ookla aracı {r.ExitCode} çıkış koduyla sonlandı: {line}";
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

/// <summary>
/// Ookla JSON satır olaylarını ilerlemeye ve sonuca çevirir. Bant genişliği bayt/sn'dir (Mbps = × 8 / 1 000 000).
/// Yerel IP / MAC okunmaz.
/// </summary>
internal sealed class OoklaEventParser(IProgress<SpeedTestProgress>? progress)
{
    private readonly object _lock = new();
    private readonly SpeedTestResult _result = new() { Provider = SpeedTestProviders.Ookla, MultipleConnections = true };
    private SpeedTestPhase _phase = SpeedTestPhase.Connecting;
    private LatencyStats? _pingSoFar;
    private (TransferStats Stats, LatencyStats? Latency)? _downloadSoFar;

    public List<string> Errors { get; } = [];
    public bool HasResult { get; private set; }

    public static string? LogMessage(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Str(root, "type") != "log") return null;
            var level = Str(root, "level");
            return level is "error" or "warning" ? Str(root, "message") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void OnLine(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            lock (_lock)
            {
                switch (Str(root, "type"))
                {
                    case "testStart":
                        _result.Server = ReadServer(root);
                        Report(SpeedTestPhase.Latency, 0, null, null);
                        break;
                    case "ping" when root.TryGetProperty("ping", out var ping):
                        var latency = Num(ping, "latency");
                        if (latency is { } lat) _pingSoFar = new LatencyStats(lat, Num(ping, "low") ?? lat, Num(ping, "jitter") ?? 0, 0, 0);
                        Report(SpeedTestPhase.Latency, Num(ping, "progress") ?? 0, null, latency);
                        break;
                    case "download" when root.TryGetProperty("download", out var d):
                        _downloadSoFar = (Transfer(d), Latency(d));
                        Report(SpeedTestPhase.Download, Num(d, "progress") ?? 0, Mbps(d), null);
                        break;
                    case "upload" when root.TryGetProperty("upload", out var u):
                        Report(SpeedTestPhase.Upload, Num(u, "progress") ?? 0, Mbps(u), null);
                        break;
                    case "result":
                        ReadResult(root);
                        break;
                    case "log":
                        var level = Str(root, "level");
                        if (level is "error" or "warning") Errors.Add(Str(root, "message"));
                        break;
                }
            }
        }
    }

    /// <summary>Aşama değişince biten aşamanın sonucu ilk bildirimle taşınır (ekran testi beklemeden gösterir).</summary>
    private void Report(SpeedTestPhase phase, double fraction, double? mbps, double? latencyMs)
    {
        var changed = phase != _phase;
        _phase = phase;
        progress?.Report(new SpeedTestProgress(phase, Math.Clamp(fraction, 0, 1), mbps, latencyMs, _result.Server)
        {
            IdleLatency = changed && phase is SpeedTestPhase.Download or SpeedTestPhase.Upload ? _pingSoFar : null,
            Download = changed && phase == SpeedTestPhase.Upload ? _downloadSoFar?.Stats : null,
            DownloadLatency = changed && phase == SpeedTestPhase.Upload ? _downloadSoFar?.Latency : null
        });
    }

    private void ReadResult(JsonElement root)
    {
        HasResult = true;
        _result.Server = ReadServer(root) ?? _result.Server;
        if (root.TryGetProperty("ping", out var ping) && Num(ping, "latency") is { } lat)
            _result.IdleLatency = new LatencyStats(lat, Num(ping, "low") ?? lat, Num(ping, "jitter") ?? 0, 0, 0);
        if (root.TryGetProperty("download", out var d) && Num(d, "bandwidth") is > 0)
        {
            _result.Download = Transfer(d);
            _result.DownloadLatency = Latency(d);
        }
        if (root.TryGetProperty("upload", out var u) && Num(u, "bandwidth") is > 0)
        {
            _result.Upload = Transfer(u);
            _result.UploadLatency = Latency(u);
        }
        if (Num(root, "packetLoss") is { } loss)
            _result.PacketLoss = new PacketLossStats(0, 0, null) { ReportedPercent = loss };
        else
            _result.PacketLossNote = "Ookla bu ağda paket kaybını ölçemedi (\"Not available\")";
        if (root.TryGetProperty("result", out var res))
        {
            var url = Str(res, "url");
            if (url.StartsWith("https://www.speedtest.net/", StringComparison.Ordinal)) _result.ResultUrl = url;
        }
    }

    public SpeedTestResult Build(TimeSpan duration)
    {
        lock (_lock)
        {
            _result.Duration = duration;
            return _result;
        }
    }

    private static SpeedTestServer? ReadServer(JsonElement root)
    {
        if (!root.TryGetProperty("server", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var iface = root.TryGetProperty("interface", out var i) ? i : default;
        return new SpeedTestServer
        {
            Provider = SpeedTestProviders.Ookla,
            Sponsor = Str(s, "name"),
            ServerId = s.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var sid) ? sid : null,
            ServerCity = Str(s, "location"),
            ServerCountry = Str(s, "country"),
            ServerAddress = Str(s, "ip"),
            HttpProtocol = Str(s, "host"),
            Isp = Str(root, "isp"),
            ClientIp = Str(iface, "externalIp") // yalnızca genel IP (ekranda gösterilir, kaydedilmez); yerel IP / MAC okunmaz
        };
    }

    private static TransferStats Transfer(JsonElement e)
    {
        var bytes = (long)(Num(e, "bytes") ?? 0);
        var seconds = (Num(e, "elapsed") ?? 0) / 1000.0;
        return new TransferStats((Num(e, "bandwidth") ?? 0) * 8 / 1_000_000, bytes, seconds, bytes, 0, 0);
    }

    private static LatencyStats? Latency(JsonElement e) =>
        e.TryGetProperty("latency", out var l) && Num(l, "iqm") is { } iqm
            ? new LatencyStats(iqm, Num(l, "low") ?? iqm, Num(l, "jitter") ?? 0, 0, 0)
            : null;

    private static double? Mbps(JsonElement e) => Num(e, "bandwidth") is { } b ? b * 8 / 1_000_000 : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

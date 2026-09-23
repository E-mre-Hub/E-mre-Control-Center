using System.IO;
using RtxWindowsUpdater.Core;
using RtxWindowsUpdater.Models;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Windows Paket Yöneticisi (winget) entegrasyonu.
/// Kontrol : winget upgrade --source &lt;kaynak&gt;   (+ winget list ile güncel paketler)
/// Güncelle: winget upgrade --id &lt;Id&gt; --exact --silent ...  (yalnızca bulunan paketler, tek tek)
/// Aynı sınıf "msstore" kaynağı ile Microsoft Store uygulamaları için de kullanılır.
/// </summary>
public sealed class WingetManager(Logger logger, string source, string key, string displayName) : IUpdateModule
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(30);

    public string Key => key;
    public string DisplayName => displayName;
    public string Source => source;

    private const string CommonArgs = "--accept-source-agreements --disable-interactivity";

    // winget dönüş kodları (APPINSTALLER_CLI_ERROR_*)
    private static readonly int NoApplicationsFound = unchecked((int)0x8A150014);
    private static readonly int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private static readonly int RebootRequiredToFinish = unchecked((int)0x8A150109);
    private static readonly int RebootRequiredForInstall = unchecked((int)0x8A15010A);
    private static readonly int RebootInitiated = unchecked((int)0x8A15010B);

    private static readonly Dictionary<int, string> KnownErrors = new()
    {
        [unchecked((int)0x8A150101)] = "Uygulama şu anda çalışıyor; kapatıp tekrar deneyin.",
        [unchecked((int)0x8A150102)] = "Başka bir kurulum işlemi sürüyor.",
        [unchecked((int)0x8A150103)] = "Güncellenecek dosyalardan biri kullanımda.",
        [unchecked((int)0x8A150104)] = "Eksik bağımlılık nedeniyle kurulamadı.",
        [unchecked((int)0x8A150105)] = "Disk alanı yetersiz.",
        [unchecked((int)0x8A150106)] = "Bellek yetersiz.",
        [unchecked((int)0x8A150107)] = "Ağ bağlantısı yok.",
        [unchecked((int)0x8A150108)] = "Kurulum başarısız; üretici desteğine başvurulması gerekiyor.",
        [unchecked((int)0x8A15010C)] = "Kurulum kullanıcı tarafından iptal edildi.",
        [unchecked((int)0x8A15010D)] = "Paket zaten kurulu.",
        [unchecked((int)0x8A15010E)] = "Daha yeni bir sürüm zaten kurulu.",
        [unchecked((int)0x8A15010F)] = "Kurulum ilke (policy) tarafından engellendi.",
        [unchecked((int)0x8A15002B)] = "Uygulanabilir güncelleme bulunamadı (paket kurulum türü desteklenmiyor olabilir)."
    };

    public static string? LocateWinget()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var p = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* geçersiz PATH girdisi */ }
        }
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    public async Task<ModuleResult> CheckAsync(CancellationToken ct)
    {
        logger.Info($"{displayName}: winget kontrol ediliyor...");
        var winget = LocateWinget();
        if (winget is null)
        {
            const string reason = "Winget (Windows Paket Yöneticisi) bulunamadı. Microsoft Store'dan 'Uygulama Yükleyicisi' (App Installer) kurulmalı.";
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }

        var ver = await ProcessRunner.RunAsync(winget, "--version", TimeSpan.FromSeconds(30), ct);
        if (!ver.Succeeded)
        {
            var reason = "Winget çalıştırılamadı: " + ProcessRunner.Describe(ver, "winget");
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }
        logger.Success($"Winget bulundu ({ver.StdOut.Trim()}).");

        logger.Info($"{displayName}: '{source}' kaynağında güncellemeler aranıyor...");
        var up = await ProcessRunner.RunAsync(winget, $"upgrade --source {source} {CommonArgs}", ListTimeout, ct);
        if (!up.Started || up.TimedOut || up.Cancelled)
        {
            var reason = ProcessRunner.Describe(up, "winget");
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }

        var tables = WingetTableParser.Parse(up.StdOut);
        if (tables.Count == 0 && up.ExitCode != 0 && up.ExitCode != NoApplicationsFound && up.ExitCode != UpdateNotApplicable)
        {
            var reason = $"'{source}' kaynağı sorgulanamadı: " + ProcessRunner.Describe(up, "winget");
            logger.Error(reason);
            return ModuleResult.CheckFailed(key, reason);
        }
        if (up.ExitCode != 0 && tables.Count > 0)
            logger.Warning($"winget uyarı koduyla döndü ({up.ExitCodeHex}); bulunan liste kullanılıyor.");

        var items = new List<UpdateItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                if (string.IsNullOrEmpty(row.Available) || !seen.Add(row.Id)) continue;
                items.Add(new UpdateItem
                {
                    Name = row.Name,
                    Id = row.Id,
                    CurrentVersion = row.Version,
                    NewVersion = row.Available,
                    UpdateAvailable = true,
                    AutoUpdatable = !table.RequiresExplicitTargeting,
                    StatusText = table.RequiresExplicitTargeting
                        ? "Güncelleme mevcut (açık hedefleme gerekli – otomatik güncellenmez)"
                        : "Güncelleme mevcut"
                });
            }
        }

        var actionable = items.Count(i => i.AutoUpdatable);
        var explicitCount = items.Count - actionable;
        foreach (var i in items)
            logger.Info($"  {i.Name}: {i.CurrentVersion} → {i.NewVersion} ({i.StatusText})");

        // Güncel paketleri de göstermek için (yalnızca bilgi amaçlı, başarısız olması kontrolü bozmaz).
        var upToDate = new List<UpdateItem>();
        var list = await ProcessRunner.RunAsync(winget, $"list --source {source} {CommonArgs}", ListTimeout, ct);
        if (list.Started && !list.TimedOut && !list.Cancelled)
        {
            foreach (var row in WingetTableParser.Parse(list.StdOut).SelectMany(t => t.Rows))
            {
                if (seen.Contains(row.Id)) continue;
                if (!string.IsNullOrEmpty(row.Available) &&
                    !row.Available.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(row.Id)) continue;
                upToDate.Add(new UpdateItem
                {
                    Name = row.Name,
                    Id = row.Id,
                    CurrentVersion = row.Version,
                    NewVersion = row.Version,
                    UpdateAvailable = false,
                    AutoUpdatable = false,
                    StatusText = "Güncel"
                });
            }
        }
        else
        {
            logger.Warning("Kurulu paket listesi alınamadı: " + ProcessRunner.Describe(list, "winget list"));
        }

        if (actionable > 0) logger.Warning($"{displayName}: {actionable} güncelleme bulundu.");
        else logger.Success($"{displayName}: otomatik uygulanabilir güncelleme bulunamadı.");
        if (explicitCount > 0)
            logger.Info($"{displayName}: {explicitCount} paket yalnızca açık hedeflemeyle güncellenebilir (sabitlenmiş/özel paket); otomatik güncellenmeyecek.");

        var details = $"Güncellenebilir: {actionable}";
        if (explicitCount > 0) details += $"\nAçık hedefleme gerekli: {explicitCount}";
        if (upToDate.Count > 0) details += $"\nGüncel paket: {upToDate.Count}";

        return new ModuleResult
        {
            Key = key,
            Status = actionable > 0 ? ComponentStatus.UpdateAvailable : ComponentStatus.UpToDate,
            Summary = actionable > 0 ? $"{actionable} güncelleme mevcut" : "Güncel",
            Details = details,
            Items = items.Concat(upToDate).ToList(),
            ActionableCount = actionable
        };
    }

    public async Task<ModuleResult> UpdateAsync(ModuleResult check, CancellationToken ct)
    {
        var winget = LocateWinget();
        if (winget is null)
            return ModuleResult.Failed(key, "Winget bulunamadı.");

        var targets = check.Items.Where(i => i.UpdateAvailable && i.AutoUpdatable).ToList();
        if (targets.Count == 0)
            return check;

        int ok = 0, reboot = 0;
        var failures = new List<string>();
        var truncated = new List<UpdateItem>();
        var resultItems = check.Items.Select(Clone).ToList();

        foreach (var item in targets)
        {
            ct.ThrowIfCancellationRequested();
            var target = resultItems.First(r => r.Id == item.Id);

            if (item.Id.EndsWith('…') || item.Id.EndsWith("..."))
            {
                truncated.Add(target);
                continue;
            }

            logger.Info($"{item.Name} güncelleniyor ({item.CurrentVersion} → {item.NewVersion})...");
            var args = $"upgrade --id \"{item.Id}\" --exact --source {source} --silent --accept-package-agreements {CommonArgs}";
            var r = await ProcessRunner.RunAsync(winget, args, PackageTimeout, CancellationToken.None,
                onStdOut: ForwardOutput, onStdErr: ForwardOutput);
            Evaluate(r, target, ref ok, ref reboot, failures);
        }

        if (truncated.Count > 0)
        {
            // Kimliği çıktı genişliği nedeniyle kısaltılmış paketler tek tek hedeflenemez;
            // aynı listeyi (winget'in kendi "otomatik güncellenebilir" kümesini) toplu komutla güncelle.
            logger.Info($"Kimliği kısaltılmış {truncated.Count} paket için toplu winget güncellemesi çalıştırılıyor...");
            var r = await ProcessRunner.RunAsync(winget,
                $"upgrade --all --source {source} --silent --accept-package-agreements {CommonArgs}",
                PackageTimeout * 2, CancellationToken.None, onStdOut: ForwardOutput, onStdErr: ForwardOutput);
            foreach (var t in truncated)
                Evaluate(r, t, ref ok, ref reboot, failures);
        }

        var total = targets.Count;
        ComponentStatus status;
        string summary;
        if (failures.Count == 0)
        {
            status = reboot > 0 ? ComponentStatus.RebootRequired : ComponentStatus.Updated;
            summary = reboot > 0 ? $"{ok} paket güncellendi – yeniden başlatma gerekli" : $"{ok} paket güncellendi";
        }
        else if (ok > 0)
        {
            status = ComponentStatus.PartiallyUpdated;
            summary = $"{ok}/{total} güncellendi, {failures.Count} başarısız";
        }
        else
        {
            status = ComponentStatus.Failed;
            summary = "Güncelleme başarısız";
        }

        if (failures.Count == 0) logger.Success($"{displayName}: {summary}.");
        else logger.Error($"{displayName}: {summary}.");

        return new ModuleResult
        {
            Key = key,
            Status = status,
            Summary = summary,
            Details = $"Başarılı: {ok}\nBaşarısız: {failures.Count}" + (reboot > 0 ? $"\nYeniden başlatma bekleyen: {reboot}" : ""),
            Reason = failures.Count > 0 ? string.Join("\n", failures) : (reboot > 0 ? "Bazı paketlerin tamamlanması için yeniden başlatma gerekiyor." : null),
            Items = resultItems,
            RebootRequired = reboot > 0
        };
    }

    private void Evaluate(ProcessResult r, UpdateItem target, ref int ok, ref int reboot, List<string> failures)
    {
        if (r.Succeeded)
        {
            ok++;
            target.StatusText = "Güncellendi";
            logger.Success($"{target.Name} güncellendi.");
        }
        else if (r.Started && !r.TimedOut && (r.ExitCode == RebootRequiredToFinish ||
                                              r.ExitCode == RebootRequiredForInstall ||
                                              r.ExitCode == RebootInitiated))
        {
            ok++;
            reboot++;
            target.StatusText = "Güncellendi – yeniden başlatma gerekli";
            logger.Warning($"{target.Name}: kurulumun tamamlanması için yeniden başlatma gerekli.");
        }
        else
        {
            var reason = r.Started && !r.TimedOut && KnownErrors.TryGetValue(r.ExitCode, out var known)
                ? $"{known} ({r.ExitCodeHex})"
                : ProcessRunner.Describe(r, "winget");
            target.StatusText = "Başarısız: " + reason;
            failures.Add($"{target.Name}: {reason}");
            logger.Error($"{target.Name} güncellenemedi: {reason}");
        }
    }

    private void ForwardOutput(string line)
    {
        var clean = WingetTableParser.CleanLine(line);
        if (clean is not null)
            logger.Output("  winget> " + clean);
    }

    private static UpdateItem Clone(UpdateItem i) => new()
    {
        Name = i.Name,
        Id = i.Id,
        CurrentVersion = i.CurrentVersion,
        NewVersion = i.NewVersion,
        UpdateAvailable = i.UpdateAvailable,
        AutoUpdatable = i.AutoUpdatable,
        StatusText = i.StatusText,
        Tag = i.Tag
    };
}

public sealed record WingetRow(string Name, string Id, string Version, string Available);

public sealed class WingetTable
{
    public bool RequiresExplicitTargeting { get; init; }
    public List<WingetRow> Rows { get; } = [];
}

/// <summary>
/// winget'in metin tablosu çıktısını ayrıştırır. Başlık satırı, altındaki "-----" çizgisinden
/// tanınır; sütun başlangıçları başlıktaki kelime konumlarından alınır (dil bağımsız).
/// </summary>
public static class WingetTableParser
{
    public static List<WingetTable> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l =>
            {
                var idx = l.LastIndexOf('\r');
                return idx >= 0 ? l[(idx + 1)..] : l;
            })
            .ToList();

        var tables = new List<WingetTable>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (!IsDashLine(lines[i])) continue;

            var header = lines[i - 1];
            var starts = ColumnStarts(header);
            if (starts.Count < 3) continue;

            // Açıklama cümlesi (':' ile biten) tablonun hemen üstündeyse bu "açık hedefleme" tablosudur.
            // Normal güncelleme tablosunun üstünde metin bulunmaz.
            var prev = lines.Take(i - 1).LastOrDefault(l => CleanLine(l) is not null);
            var table = new WingetTable
            {
                RequiresExplicitTargeting = prev is not null && prev.TrimEnd().EndsWith(':')
            };

            for (var j = i + 1; j < lines.Count; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line) || line.Length <= starts[2]) break;

                var name = Field(line, starts, 0);
                var id = Field(line, starts, 1);
                var version = Field(line, starts, 2);
                var available = starts.Count >= 4 ? Field(line, starts, 3) : string.Empty;

                if (id.Length == 0 || id.Contains(' ') || version.Length == 0) break;
                if (starts[1] > 0 && line.Length > starts[1] && line[starts[1] - 1] != ' ') break; // hizalama bozuk

                table.Rows.Add(new WingetRow(name, id, version, available));
            }
            tables.Add(table);
        }
        return tables;
    }

    private static bool IsDashLine(string line)
    {
        var t = line.Trim();
        return t.Length >= 10 && t.All(c => c == '-');
    }

    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (var p = 0; p < header.Length; p++)
        {
            if (header[p] != ' ' && (p == 0 || header[p - 1] == ' '))
                starts.Add(p);
        }
        return starts;
    }

    private static string Field(string line, List<int> starts, int index)
    {
        var start = starts[index];
        if (start >= line.Length) return string.Empty;
        var end = index + 1 < starts.Count ? Math.Min(starts[index + 1], line.Length) : line.Length;
        return line[start..end].Trim();
    }

    /// <summary>İlerleme çubuğu / dönen imleç satırlarını ayıklar; anlamlı satırı döndürür.</summary>
    public static string? CleanLine(string line)
    {
        var idx = line.LastIndexOf('\r');
        var s = (idx >= 0 ? line[(idx + 1)..] : line).Trim();
        if (s.Length == 0) return null;
        if (s.Contains('█') || s.Contains('▒')) return null;
        if (s.All(c => c is '-' or '\\' or '|' or '/' or ' ')) return null;
        return s;
    }
}

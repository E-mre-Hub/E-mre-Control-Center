using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Windows Güvenlik Merkezi'ne kayıtlı ürün (virüsten koruma / güvenlik duvarı).</summary>
public sealed record SecurityProduct(string Kind, string Name, bool? Enabled, bool? UpToDate)
{
    public string StateText => Enabled switch { true => "Açık", false => "Kapalı", _ => "Bilinmiyor" } +
                               (UpToDate is false ? " · tanımlar güncel değil" : "");
}

public sealed record SecurityReport(IReadOnlyList<CheckResult> Checks, IReadOnlyList<SecurityProduct> Products)
{
    public CheckState Overall => CheckStates.Worst(Checks.Select(c => c.State));
}

/// <summary>
/// Güvenlik durumu – YALNIZCA okuma: Windows Güvenlik Merkezi (root\SecurityCenter2 ürünleri), Microsoft Defender
/// (Get-MpComputerStatus: koruma, gerçek zamanlı koruma, tanım yaşı, son taramalar, kurcalama koruması), Windows Güvenlik Duvarı
/// profilleri (etkin depo), Güvenli Önyükleme, Kullanıcı Hesabı Denetimi ve Bellek bütünlüğü. Hiçbir güvenlik ayarı değiştirilmez;
/// değişiklik kullanıcının açtığı Windows Güvenliği uygulamasında yapılır.
/// </summary>
public sealed class SecurityStatusService(Logger logger)
{
    private const string Script = """
        $mp = $null; $mpErr = $null
        try {
            $s = Get-MpComputerStatus
            $q = $null; if ($s.QuickScanEndTime) { $q = $s.QuickScanEndTime.ToString('yyyy-MM-dd HH:mm') }
            $f = $null; if ($s.FullScanEndTime) { $f = $s.FullScanEndTime.ToString('yyyy-MM-dd HH:mm') }
            $t = $null; if ($s.PSObject.Properties.Name -contains 'IsTamperProtected') { $t = [bool]$s.IsTamperProtected }
            $mp = @{ service = [bool]$s.AMServiceEnabled; av = [bool]$s.AntivirusEnabled; rt = [bool]$s.RealTimeProtectionEnabled
                     behavior = [bool]$s.BehaviorMonitorEnabled; mode = [string]$s.AMRunningMode; sigAge = [int]$s.AntivirusSignatureAge
                     quick = $q; full = $f; tamper = $t }
        } catch { $mpErr = $_.Exception.Message }
        $fw = New-Object System.Collections.ArrayList; $fwErr = $null
        try {
            foreach ($p in (Get-NetFirewallProfile -PolicyStore ActiveStore)) { [void]$fw.Add(@{ name = [string]$p.Name; enabled = [string]$p.Enabled }) }
        } catch { $fwErr = $_.Exception.Message }
        Write-Result @{ mp = $mp; mpError = $mpErr; firewall = @($fw); firewallError = $fwErr }
        """;

    public async Task<SecurityReport> ReadAsync(CancellationToken ct)
    {
        var checks = new List<CheckResult>();
        var (products, productError) = await Task.Run(ReadProducts, ct);
        var ps = await PowerShellRunner.RunAsync(Script, TimeSpan.FromSeconds(60), ct, traceName: "Get-MpComputerStatus / Get-NetFirewallProfile");

        // Virüsten koruma (Güvenlik Merkezi)
        var av = products.Where(p => p.Kind == "Virüsten koruma").ToList();
        if (products.Count == 0 && productError is not null)
            checks.Add(new CheckResult("Virüsten koruma", CheckState.Unknown, "Windows Güvenlik Merkezi okunamadı.", productError));
        else if (av.Any(p => p.Enabled == true))
        {
            var active = av.Where(p => p.Enabled == true).ToList();
            checks.Add(new CheckResult("Virüsten koruma", active.Any(p => p.UpToDate == false) ? CheckState.Warning : CheckState.Healthy,
                "Etkin: " + string.Join(", ", active.Select(p => p.Name)),
                active.Any(p => p.UpToDate == false) ? "Güvenlik Merkezi tanımların güncel olmadığını bildiriyor." : null));
        }
        else
            checks.Add(new CheckResult("Virüsten koruma", CheckState.Error, av.Count == 0 ? "Kayıtlı virüsten koruma ürünü yok" : "Kayıtlı ürünlerin hiçbiri açık değil",
                string.Join("\n", av.Select(p => $"{p.Name}: {p.StateText}"))));

        // Microsoft Defender ayrıntıları
        if (!ps.Ok)
            checks.Add(new CheckResult("Microsoft Defender", CheckState.Unknown, "Defender durumu okunamadı.", ps.DescribeFailure("Get-MpComputerStatus")));
        else
        {
            var data = ps.Data!.Value;
            var mpErr = data.Str("mpError");
            if (mpErr is not null || !data.TryGetProperty("mp", out var mp) || mp.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                var thirdParty = av.Any(p => p.Enabled == true && !p.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase));
                checks.Add(new CheckResult("Microsoft Defender", thirdParty ? CheckState.Info : CheckState.Unknown,
                    thirdParty ? "Başka bir virüsten koruma etkin; Defender durumu okunamadı" : "Defender durumu okunamadı.", mpErr));
            }
            else
            {
                var mode = mp.Str("mode") ?? "—";
                var passive = mode.Contains("Passive", StringComparison.OrdinalIgnoreCase) || mode.Contains("EDR", StringComparison.OrdinalIgnoreCase);
                var rt = mp.Bool("rt") == true;
                var avOn = mp.Bool("av") == true;
                checks.Add(new CheckResult("Microsoft Defender",
                    passive ? CheckState.Info : avOn && rt ? CheckState.Healthy : CheckState.Warning,
                    passive ? $"Pasif mod ({mode}) – başka ürün koruyor" : !avOn ? "Virüsten koruma kapalı" : rt ? "Açık · gerçek zamanlı koruma açık" : "Gerçek zamanlı koruma KAPALI",
                    $"Çalışma modu: {mode} · davranış izleme: {OnOff(mp.Bool("behavior"))} · kurcalama koruması: {OnOff(mp.Bool("tamper"))}"));
                var age = mp.Long("sigAge");
                if (!passive && age is not null)
                    checks.Add(new CheckResult("Defender tanımları", age > 7 ? CheckState.Warning : CheckState.Healthy,
                        age == 0 ? "Bugün güncellendi" : $"{age} gün önce güncellendi", null, Nav.Health, Nav.Cards));
                var quick = mp.Str("quick");
                var full = mp.Str("full");
                checks.Add(new CheckResult("Son tarama", CheckState.Info,
                    quick is null ? "Hızlı tarama kaydı yok" : "Hızlı tarama: " + quick,
                    full is null ? "Tam tarama kaydı yok" : "Tam tarama: " + full));
            }

            // Güvenlik duvarı
            var fwErr = data.Str("firewallError");
            var profiles = data.Arr("firewall").Select(p => (Name: p.Str("name") ?? "?", Enabled: p.Str("enabled"))).ToList();
            var thirdFw = products.Where(p => p.Kind == "Güvenlik duvarı" && p.Enabled == true).ToList();
            if (fwErr is not null || profiles.Count == 0)
                checks.Add(new CheckResult("Güvenlik duvarı", CheckState.Unknown, "Güvenlik duvarı profilleri okunamadı.", fwErr));
            else
            {
                var off = profiles.Where(p => !string.Equals(p.Enabled, "True", StringComparison.OrdinalIgnoreCase)).Select(p => ProfileName(p.Name)).ToList();
                var summary = string.Join(" · ", profiles.Select(p => $"{ProfileName(p.Name)}: {(string.Equals(p.Enabled, "True", StringComparison.OrdinalIgnoreCase) ? "açık" : "kapalı")}"));
                checks.Add(off.Count == 0
                    ? new CheckResult("Güvenlik duvarı", CheckState.Healthy, "Tüm profillerde açık", summary)
                    : thirdFw.Count > 0
                        ? new CheckResult("Güvenlik duvarı", CheckState.Info, "Windows Güvenlik Duvarı bazı profillerde kapalı; başka ürün etkin: " + string.Join(", ", thirdFw.Select(p => p.Name)), summary)
                        : new CheckResult("Güvenlik duvarı", CheckState.Warning, "Kapalı profil: " + string.Join(", ", off), summary));
            }
        }

        checks.Add(ReadSecureBoot());
        checks.Add(ReadUac());
        checks.Add(ReadMemoryIntegrity());
        logger.Info("Güvenlik durumu: " + string.Join(" | ", checks.Select(c => $"{c.Title}: {CheckStates.Text(c.State)} – {c.Summary}")));
        return new SecurityReport(checks, products);

        static string OnOff(bool? v) => v switch { true => "açık", false => "kapalı", _ => "—" };
        static string ProfileName(string n) => n switch { "Domain" => "Etki alanı", "Private" => "Özel", "Public" => "Ortak", _ => n };
    }

    /// <summary>root\SecurityCenter2 ürünleri. productState: 12-15. bitler 1 = açık, 4-7. bitler 0 = tanımlar güncel.</summary>
    private static (List<SecurityProduct> Products, string? Error) ReadProducts()
    {
        var list = new List<SecurityProduct>();
        string? error = null;
        foreach (var (cls, kind) in new[] { ("AntiVirusProduct", "Virüsten koruma"), ("FirewallProduct", "Güvenlik duvarı") })
        {
            var r = Wmi.Query(@"\\.\root\SecurityCenter2", $"SELECT displayName, productState FROM {cls}", Wmi.DefaultTimeout);
            if (!r.Ok)
            {
                error = r.Error;
                continue;
            }
            foreach (var row in r.Rows)
            {
                var state = row.Long("productState");
                bool? enabled = state is null ? null : ((state.Value >> 12) & 0xF) == 1;
                bool? upToDate = state is null || kind != "Virüsten koruma" ? null : ((state.Value >> 4) & 0xF) == 0;
                list.Add(new SecurityProduct(kind, row.Str("displayName") ?? "—", enabled, upToDate));
            }
        }
        return (list, error);
    }

    private static CheckResult ReadSecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return k?.GetValue("UEFISecureBootEnabled") switch
        {
            1 => new CheckResult("Güvenli Önyükleme", CheckState.Healthy, "Açık"),
            0 => new CheckResult("Güvenli Önyükleme", CheckState.Warning, "Kapalı (UEFI ayarlarından açılabilir)"),
            _ => new CheckResult("Güvenli Önyükleme", CheckState.Info, "Windows bildirmedi (eski BIOS modu veya desteklenmiyor)")
        };
    }

    private static CheckResult ReadUac()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        return k?.GetValue("EnableLUA") switch
        {
            0 => new CheckResult("Kullanıcı Hesabı Denetimi (UAC)", CheckState.Warning, "Kapalı"),
            1 => new CheckResult("Kullanıcı Hesabı Denetimi (UAC)", CheckState.Healthy, "Açık"),
            _ => new CheckResult("Kullanıcı Hesabı Denetimi (UAC)", CheckState.Unknown, "Okunamadı")
        };
    }

    private static CheckResult ReadMemoryIntegrity()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return k?.GetValue("Enabled") switch
        {
            1 => new CheckResult("Bellek bütünlüğü (Çekirdek yalıtımı)", CheckState.Info, "Açık"),
            0 => new CheckResult("Bellek bütünlüğü (Çekirdek yalıtımı)", CheckState.Info, "Kapalı"),
            _ => new CheckResult("Bellek bütünlüğü (Çekirdek yalıtımı)", CheckState.Info, "Yapılandırılmamış (kayıt yok)")
        };
    }
}

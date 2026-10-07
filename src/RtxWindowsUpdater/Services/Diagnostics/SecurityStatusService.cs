using Microsoft.Win32;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Windows Güvenlik Merkezi'ne kayıtlı ürün (virüsten koruma / güvenlik duvarı).</summary>
public sealed record SecurityProduct(string Kind, string Name, bool? Enabled, bool? UpToDate)
{
    public string StateText => Enabled switch { true => L.T("Açık", "On"), false => L.T("Kapalı", "Off"), _ => L.T("Bilinmiyor", "Unknown") } +
                               (UpToDate is false ? L.T(" · tanımlar güncel değil", " · definitions out of date") : "");
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
        var av = products.Where(p => p.Kind == L.T("Virüsten koruma", "Antivirus")).ToList();
        if (products.Count == 0 && productError is not null)
            checks.Add(new CheckResult(L.T("Virüsten koruma", "Antivirus"), CheckState.Unknown, L.T("Windows Güvenlik Merkezi okunamadı.", "Could not read Windows Security Center."), productError));
        else if (av.Any(p => p.Enabled == true))
        {
            var active = av.Where(p => p.Enabled == true).ToList();
            checks.Add(new CheckResult(L.T("Virüsten koruma", "Antivirus"), active.Any(p => p.UpToDate == false) ? CheckState.Warning : CheckState.Healthy,
                L.T("Etkin: ", "Active: ") + string.Join(", ", active.Select(p => p.Name)),
                active.Any(p => p.UpToDate == false) ? L.T("Güvenlik Merkezi tanımların güncel olmadığını bildiriyor.", "Security Center reports that the definitions are out of date.") : null));
        }
        else
            checks.Add(new CheckResult(L.T("Virüsten koruma", "Antivirus"), CheckState.Error, av.Count == 0 ? L.T("Kayıtlı virüsten koruma ürünü yok", "No registered antivirus product") : L.T("Kayıtlı ürünlerin hiçbiri açık değil", "None of the registered products is on"),
                string.Join("\n", av.Select(p => $"{p.Name}: {p.StateText}"))));

        // Microsoft Defender ayrıntıları
        if (!ps.Ok)
            checks.Add(new CheckResult("Microsoft Defender", CheckState.Unknown, L.T("Defender durumu okunamadı.", "Could not read Defender status."), ps.DescribeFailure("Get-MpComputerStatus")));
        else
        {
            var data = ps.Data!.Value;
            var mpErr = data.Str("mpError");
            if (mpErr is not null || !data.TryGetProperty("mp", out var mp) || mp.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                var thirdParty = av.Any(p => p.Enabled == true && !p.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase));
                checks.Add(new CheckResult("Microsoft Defender", thirdParty ? CheckState.Info : CheckState.Unknown,
                    thirdParty ? L.T("Başka bir virüsten koruma etkin; Defender durumu okunamadı", "Another antivirus is active; Defender status could not be read") : L.T("Defender durumu okunamadı.", "Could not read Defender status."), mpErr));
            }
            else
            {
                var mode = mp.Str("mode") ?? "—";
                var passive = mode.Contains("Passive", StringComparison.OrdinalIgnoreCase) || mode.Contains("EDR", StringComparison.OrdinalIgnoreCase);
                var rt = mp.Bool("rt") == true;
                var avOn = mp.Bool("av") == true;
                checks.Add(new CheckResult("Microsoft Defender",
                    passive ? CheckState.Info : avOn && rt ? CheckState.Healthy : CheckState.Warning,
                    passive ? L.T($"Pasif mod ({mode}) – başka ürün koruyor", $"Passive mode ({mode}) – another product is protecting") : !avOn ? L.T("Virüsten koruma kapalı", "Antivirus off") : rt ? L.T("Açık · gerçek zamanlı koruma açık", "On · real-time protection on") : L.T("Gerçek zamanlı koruma KAPALI", "Real-time protection OFF"),
                    L.T($"Çalışma modu: {mode} · davranış izleme: {OnOff(mp.Bool("behavior"))} · kurcalama koruması: {OnOff(mp.Bool("tamper"))}", $"Running mode: {mode} · behavior monitoring: {OnOff(mp.Bool("behavior"))} · tamper protection: {OnOff(mp.Bool("tamper"))}")));
                var age = mp.Long("sigAge");
                if (!passive && age is not null)
                    checks.Add(new CheckResult(L.T("Defender tanımları", "Defender definitions"), age > 7 ? CheckState.Warning : CheckState.Healthy,
                        age == 0 ? L.T("Bugün güncellendi", "Updated today") : L.T($"{age} gün önce güncellendi", $"Updated {age} day(s) ago"), null, Nav.Health, Nav.Cards));
                var quick = mp.Str("quick");
                var full = mp.Str("full");
                checks.Add(new CheckResult(L.T("Son tarama", "Last scan"), CheckState.Info,
                    quick is null ? L.T("Hızlı tarama kaydı yok", "No quick scan record") : L.T("Hızlı tarama: ", "Quick scan: ") + quick,
                    full is null ? L.T("Tam tarama kaydı yok", "No full scan record") : L.T("Tam tarama: ", "Full scan: ") + full));
            }

            // Güvenlik duvarı
            var fwErr = data.Str("firewallError");
            var profiles = data.Arr("firewall").Select(p => (Name: p.Str("name") ?? "?", Enabled: p.Str("enabled"))).ToList();
            var thirdFw = products.Where(p => p.Kind == L.T("Güvenlik duvarı", "Firewall") && p.Enabled == true).ToList();
            if (fwErr is not null || profiles.Count == 0)
                checks.Add(new CheckResult(L.T("Güvenlik duvarı", "Firewall"), CheckState.Unknown, L.T("Güvenlik duvarı profilleri okunamadı.", "Could not read the firewall profiles."), fwErr));
            else
            {
                var off = profiles.Where(p => !string.Equals(p.Enabled, "True", StringComparison.OrdinalIgnoreCase)).Select(p => ProfileName(p.Name)).ToList();
                var summary = string.Join(" · ", profiles.Select(p => L.T($"{ProfileName(p.Name)}: {(string.Equals(p.Enabled, "True", StringComparison.OrdinalIgnoreCase) ? "açık" : "kapalı")}", $"{ProfileName(p.Name)}: {(string.Equals(p.Enabled, "True", StringComparison.OrdinalIgnoreCase) ? "on" : "off")}")));
                checks.Add(off.Count == 0
                    ? new CheckResult(L.T("Güvenlik duvarı", "Firewall"), CheckState.Healthy, L.T("Tüm profillerde açık", "On in all profiles"), summary)
                    : thirdFw.Count > 0
                        ? new CheckResult(L.T("Güvenlik duvarı", "Firewall"), CheckState.Info, L.T("Windows Güvenlik Duvarı bazı profillerde kapalı; başka ürün etkin: ", "Windows Firewall is off in some profiles; another product is active: ") + string.Join(", ", thirdFw.Select(p => p.Name)), summary)
                        : new CheckResult(L.T("Güvenlik duvarı", "Firewall"), CheckState.Warning, L.T("Kapalı profil: ", "Profile off: ") + string.Join(", ", off), summary));
            }
        }

        checks.Add(ReadSecureBoot());
        checks.Add(ReadUac());
        checks.Add(ReadMemoryIntegrity());
        logger.Info(L.T("Güvenlik durumu: ", "Security status: ") + string.Join(" | ", checks.Select(c => $"{c.Title}: {CheckStates.Text(c.State)} – {c.Summary}")));
        return new SecurityReport(checks, products);

        static string OnOff(bool? v) => v switch { true => L.T("açık", "on"), false => L.T("kapalı", "off"), _ => "—" };
        static string ProfileName(string n) => n switch { "Domain" => L.T("Etki alanı", "Domain"), "Private" => L.T("Özel", "Private"), "Public" => L.T("Ortak", "Public"), _ => n };
    }

    /// <summary>root\SecurityCenter2 ürünleri. productState: 12-15. bitler 1 = açık, 4-7. bitler 0 = tanımlar güncel.</summary>
    private static (List<SecurityProduct> Products, string? Error) ReadProducts()
    {
        var list = new List<SecurityProduct>();
        string? error = null;
        foreach (var (cls, kind) in new[] { ("AntiVirusProduct", L.T("Virüsten koruma", "Antivirus")), ("FirewallProduct", L.T("Güvenlik duvarı", "Firewall")) })
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
                bool? upToDate = state is null || kind != L.T("Virüsten koruma", "Antivirus") ? null : ((state.Value >> 4) & 0xF) == 0;
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
            1 => new CheckResult(L.T("Güvenli Önyükleme", "Secure Boot"), CheckState.Healthy, L.T("Açık", "On")),
            0 => new CheckResult(L.T("Güvenli Önyükleme", "Secure Boot"), CheckState.Warning, L.T("Kapalı (UEFI ayarlarından açılabilir)", "Off (can be turned on in the UEFI settings)")),
            _ => new CheckResult(L.T("Güvenli Önyükleme", "Secure Boot"), CheckState.Info, L.T("Windows bildirmedi (eski BIOS modu veya desteklenmiyor)", "Not reported by Windows (legacy BIOS mode or not supported)"))
        };
    }

    private static CheckResult ReadUac()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        return k?.GetValue("EnableLUA") switch
        {
            0 => new CheckResult(L.T("Kullanıcı Hesabı Denetimi (UAC)", "User Account Control (UAC)"), CheckState.Warning, L.T("Kapalı", "Off")),
            1 => new CheckResult(L.T("Kullanıcı Hesabı Denetimi (UAC)", "User Account Control (UAC)"), CheckState.Healthy, L.T("Açık", "On")),
            _ => new CheckResult(L.T("Kullanıcı Hesabı Denetimi (UAC)", "User Account Control (UAC)"), CheckState.Unknown, L.T("Okunamadı", "Unreadable"))
        };
    }

    private static CheckResult ReadMemoryIntegrity()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return k?.GetValue("Enabled") switch
        {
            1 => new CheckResult(L.T("Bellek bütünlüğü (Çekirdek yalıtımı)", "Memory integrity (Core isolation)"), CheckState.Info, L.T("Açık", "On")),
            0 => new CheckResult(L.T("Bellek bütünlüğü (Çekirdek yalıtımı)", "Memory integrity (Core isolation)"), CheckState.Info, L.T("Kapalı", "Off")),
            _ => new CheckResult(L.T("Bellek bütünlüğü (Çekirdek yalıtımı)", "Memory integrity (Core isolation)"), CheckState.Info, L.T("Yapılandırılmamış (kayıt yok)", "Not configured (no entry)"))
        };
    }
}

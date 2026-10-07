using System.Runtime.InteropServices;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services.Diagnostics;

/// <summary>Tek bataryanın Windows'un bildirdiği değerleri (Win32_Battery + root\wmi Battery* sınıfları). Bildirilmeyen alan null.</summary>
public sealed record BatteryInfo(
    string Name,
    string? Manufacturer,
    string? Chemistry,
    int? ChargePercent,
    int? StatusCode,
    long? RemainingMWh,
    long? FullChargeMWh,
    long? DesignMWh,
    int? CycleCount,
    long? RateMW,
    bool? Charging,
    bool? Discharging)
{
    /// <summary>Sağlık = tam şarj kapasitesi / tasarım kapasitesi (ikisi de bildirildiyse).</summary>
    public double? HealthPercent => FullChargeMWh is > 0 && DesignMWh is > 0 ? FullChargeMWh.Value * 100.0 / DesignMWh.Value : null;
    public string ChargeText => ChargePercent is { } c ? $"%{c}" : L.T("Bildirilmedi", "Not reported");
    public string StatusText => StatusCode switch
    {
        1 => L.T("Pilden çalışıyor (boşalıyor)", "On battery (discharging)"),
        2 => L.T("Prize takılı", "Plugged in"),
        3 => L.T("Tam dolu", "Fully charged"),
        4 => L.T("Düşük", "Low"),
        5 => L.T("Kritik", "Critical"),
        6 or 7 or 8 or 9 => L.T("Şarj oluyor", "Charging"),
        11 => L.T("Kısmen dolu", "Partially charged"),
        null => Charging == true ? L.T("Şarj oluyor", "Charging") : Discharging == true ? L.T("Pilden çalışıyor (boşalıyor)", "On battery (discharging)") : L.T("Bildirilmedi", "Not reported"),
        _ => L.T($"Bilinmiyor ({StatusCode})", $"Unknown ({StatusCode})")
    };
    public string RemainingText => RemainingMWh is > 0 ? Formats.Number(RemainingMWh.Value / 1000.0) + " Wh" : L.T("Bildirilmedi", "Not reported");
    public string FullText => FullChargeMWh is > 0 ? Formats.Number(FullChargeMWh.Value / 1000.0) + " Wh" : L.T("Bildirilmedi", "Not reported");
    public string DesignText => DesignMWh is > 0 ? Formats.Number(DesignMWh.Value / 1000.0) + " Wh" : L.T("Bildirilmedi", "Not reported");
    public string HealthText => HealthPercent is { } h
        ? L.T($"%{Formats.Number(Math.Min(h, 100), "0")} (tasarım kapasitesinin; aşınma %{Formats.Number(Math.Max(0, 100 - h), "0")})", $"{Formats.Number(Math.Min(h, 100), "0")}% (of design capacity; wear {Formats.Number(Math.Max(0, 100 - h), "0")}%)") + (h > 100 ? L.T(" – sürücü tasarımdan yüksek kapasite bildiriyor", " – the driver reports a capacity higher than the design capacity") : "")
        : L.T("Hesaplanamadı (kapasite bildirilmedi)", "Could not be calculated (capacity not reported)");
    public string CycleText => CycleCount is > 0 ? CycleCount.Value.ToString("N0") : L.T("Bildirilmedi", "Not reported");
    public string RateText => RateMW is { } r && r != 0 ? Formats.Number(Math.Abs(r) / 1000.0) + " W" + (r > 0 ? L.T(" (şarj)", " (charging)") : L.T(" (deşarj)", " (discharging)")) : "—";
    public CheckState State => HealthPercent is { } h ? h < 60 ? CheckState.Warning : CheckState.Healthy : CheckState.Unknown;
}

public sealed record BatteryReport(bool HasBattery, IReadOnlyList<BatteryInfo> Batteries, string? AcText, string? RemainingTimeText, string? Note, string? Error);

/// <summary>
/// Batarya: GetSystemPowerStatus (AC / pil, batarya var mı), Win32_Battery (şarj yüzdesi, durum), root\wmi BatteryStaticData
/// (tasarım kapasitesi), BatteryFullChargedCapacity, BatteryStatus (kalan kapasite / şarj hızı) ve BatteryCycleCount. Masaüstünde
/// "Bu sistemde batarya bulunamadı." döner. Sürücünün bildirmediği değer (ör. döngü sayısı 0) "Bildirilmedi" yazar – tahmin yok.
/// </summary>
public sealed class BatteryService(Logger logger)
{
    public static string NoBatteryText => L.T("Bu sistemde batarya bulunamadı.", "No battery was found on this system.");

    public Task<BatteryReport> ReadAsync(CancellationToken ct = default) => Task.Run(Read, ct);

    private BatteryReport Read()
    {
        string? ac = null, remaining = null;
        var systemSaysNoBattery = false;
        if (GetSystemPowerStatus(out var ps))
        {
            ac = ps.ACLineStatus switch { 0 => L.T("Pilden çalışıyor", "On battery"), 1 => L.T("Prize takılı (AC)", "Plugged in (AC)"), _ => L.T("Bilinmiyor", "Unknown") };
            systemSaysNoBattery = (ps.BatteryFlag & 128) != 0;
            if (ps.BatteryLifeTime is > 0 and not uint.MaxValue)
                remaining = L.T($"{ps.BatteryLifeTime / 3600} sa {ps.BatteryLifeTime % 3600 / 60} dk (Windows tahmini)", $"{ps.BatteryLifeTime / 3600} h {ps.BatteryLifeTime % 3600 / 60} min (Windows estimate)");
        }

        var win = Wmi.Query(@"\\.\root\cimv2", "SELECT Name, DeviceID, EstimatedChargeRemaining, BatteryStatus, Chemistry FROM Win32_Battery", Wmi.DefaultTimeout);
        if (systemSaysNoBattery && win.Rows.Count == 0)
        {
            logger.Info(L.T("Batarya: ", "Battery: ") + NoBatteryText);
            return new BatteryReport(false, [], ac, null, null, null);
        }
        if (!win.Ok && win.Rows.Count == 0)
            return new BatteryReport(false, [], ac, null, null, L.T("Batarya bilgisi okunamadı: ", "Could not read battery information: ") + win.Error);
        if (win.Rows.Count == 0)
            return new BatteryReport(false, [], ac, null, null, null);

        // root\wmi sınıfları aynı sırayla (InstanceName) döner; sayı tutmazsa kapasite eşleştirilmez (yanlış bataryaya yazılmaz).
        var stat = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, DesignedCapacity, ManufactureName, DeviceName FROM BatteryStaticData", Wmi.DefaultTimeout);
        var full = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", Wmi.DefaultTimeout);
        var status = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, RemainingCapacity, ChargeRate, DischargeRate, Charging, Discharging FROM BatteryStatus", Wmi.DefaultTimeout);
        var cycles = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, CycleCount FROM BatteryCycleCount", Wmi.DefaultTimeout);
        var notes = new List<string>();
        if (!stat.Ok) notes.Add(L.T("Tasarım kapasitesi okunamadı: ", "Could not read the design capacity: ") + stat.Error);
        if (!full.Ok) notes.Add(L.T("Tam şarj kapasitesi okunamadı: ", "Could not read the full charge capacity: ") + full.Error);

        var list = new List<BatteryInfo>();
        for (var i = 0; i < win.Rows.Count; i++)
        {
            var w = win.Rows[i];
            var s = Pick(stat, i, win.Rows.Count);
            var f = Pick(full, i, win.Rows.Count);
            var st = Pick(status, i, win.Rows.Count);
            var cy = Pick(cycles, i, win.Rows.Count);
            long? rate = st is null ? null
                : st.Long("ChargeRate") is > 0 and var cr ? cr
                : st.Long("DischargeRate") is > 0 and var dr ? -dr : null;
            list.Add(new BatteryInfo(
                s?.Str("DeviceName") ?? w.Str("Name") ?? w.Str("DeviceID") ?? L.T("Batarya", "Battery"),
                s?.Str("ManufactureName"),
                ChemistryText(w.Long("Chemistry")),
                (int?)w.Long("EstimatedChargeRemaining") is { } pct and >= 0 and <= 100 ? pct : null,
                (int?)w.Long("BatteryStatus"),
                st?.Long("RemainingCapacity") is > 0 and var rem ? rem : null,
                f?.Long("FullChargedCapacity") is > 0 and var fc ? fc : null,
                s?.Long("DesignedCapacity") is > 0 and var dc ? dc : null,
                (int?)cy?.Long("CycleCount") is > 0 and var c ? c : null,
                rate,
                st?.Bool("Charging"),
                st?.Bool("Discharging")));
        }
        logger.Info(L.T("Batarya: ", "Battery: ") + string.Join(" | ", list.Select(b => L.T($"{b.Name}: {b.ChargeText}, {b.StatusText}, tam {b.FullText}, tasarım {b.DesignText}, sağlık {b.HealthText}, döngü {b.CycleText}", $"{b.Name}: {b.ChargeText}, {b.StatusText}, full {b.FullText}, design {b.DesignText}, health {b.HealthText}, cycles {b.CycleText}"))));
        return new BatteryReport(true, list, ac, remaining, notes.Count == 0 ? null : string.Join(" ", notes), null);

        static System.Management.ManagementBaseObject? Pick(WmiResult r, int index, int expected) =>
            r.Ok && r.Rows.Count == expected ? r.Rows[index] : null;
    }

    private static string? ChemistryText(long? c) => c switch
    {
        3 => L.T("Kurşun asit", "Lead acid"), 4 => L.T("Nikel kadmiyum", "Nickel cadmium"), 5 => L.T("Nikel metal hidrit", "Nickel metal hydride"), 6 => L.T("Lityum iyon", "Lithium-ion"), 7 => L.T("Çinko hava", "Zinc air"), 8 => L.T("Lityum polimer", "Lithium polymer"),
        _ => null
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

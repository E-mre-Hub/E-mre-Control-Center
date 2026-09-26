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
    public string ChargeText => ChargePercent is { } c ? $"%{c}" : "Bildirilmedi";
    public string StatusText => StatusCode switch
    {
        1 => "Pilden çalışıyor (boşalıyor)",
        2 => "Prize takılı",
        3 => "Tam dolu",
        4 => "Düşük",
        5 => "Kritik",
        6 or 7 or 8 or 9 => "Şarj oluyor",
        11 => "Kısmen dolu",
        null => Charging == true ? "Şarj oluyor" : Discharging == true ? "Pilden çalışıyor (boşalıyor)" : "Bildirilmedi",
        _ => $"Bilinmiyor ({StatusCode})"
    };
    public string RemainingText => RemainingMWh is > 0 ? Formats.Number(RemainingMWh.Value / 1000.0) + " Wh" : "Bildirilmedi";
    public string FullText => FullChargeMWh is > 0 ? Formats.Number(FullChargeMWh.Value / 1000.0) + " Wh" : "Bildirilmedi";
    public string DesignText => DesignMWh is > 0 ? Formats.Number(DesignMWh.Value / 1000.0) + " Wh" : "Bildirilmedi";
    public string HealthText => HealthPercent is { } h
        ? $"%{Formats.Number(Math.Min(h, 100), "0")} (tasarım kapasitesinin; aşınma %{Formats.Number(Math.Max(0, 100 - h), "0")})" + (h > 100 ? " – sürücü tasarımdan yüksek kapasite bildiriyor" : "")
        : "Hesaplanamadı (kapasite bildirilmedi)";
    public string CycleText => CycleCount is > 0 ? CycleCount.Value.ToString("N0") : "Bildirilmedi";
    public string RateText => RateMW is { } r && r != 0 ? Formats.Number(Math.Abs(r) / 1000.0) + " W" + (r > 0 ? " (şarj)" : " (deşarj)") : "—";
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
    public const string NoBatteryText = "Bu sistemde batarya bulunamadı.";

    public Task<BatteryReport> ReadAsync(CancellationToken ct = default) => Task.Run(Read, ct);

    private BatteryReport Read()
    {
        string? ac = null, remaining = null;
        var systemSaysNoBattery = false;
        if (GetSystemPowerStatus(out var ps))
        {
            ac = ps.ACLineStatus switch { 0 => "Pilden çalışıyor", 1 => "Prize takılı (AC)", _ => "Bilinmiyor" };
            systemSaysNoBattery = (ps.BatteryFlag & 128) != 0;
            if (ps.BatteryLifeTime is > 0 and not uint.MaxValue)
                remaining = $"{ps.BatteryLifeTime / 3600} sa {ps.BatteryLifeTime % 3600 / 60} dk (Windows tahmini)";
        }

        var win = Wmi.Query(@"\\.\root\cimv2", "SELECT Name, DeviceID, EstimatedChargeRemaining, BatteryStatus, Chemistry FROM Win32_Battery", Wmi.DefaultTimeout);
        if (systemSaysNoBattery && win.Rows.Count == 0)
        {
            logger.Info("Batarya: " + NoBatteryText);
            return new BatteryReport(false, [], ac, null, null, null);
        }
        if (!win.Ok && win.Rows.Count == 0)
            return new BatteryReport(false, [], ac, null, null, "Batarya bilgisi okunamadı: " + win.Error);
        if (win.Rows.Count == 0)
            return new BatteryReport(false, [], ac, null, null, null);

        // root\wmi sınıfları aynı sırayla (InstanceName) döner; sayı tutmazsa kapasite eşleştirilmez (yanlış bataryaya yazılmaz).
        var stat = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, DesignedCapacity, ManufactureName, DeviceName FROM BatteryStaticData", Wmi.DefaultTimeout);
        var full = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", Wmi.DefaultTimeout);
        var status = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, RemainingCapacity, ChargeRate, DischargeRate, Charging, Discharging FROM BatteryStatus", Wmi.DefaultTimeout);
        var cycles = Wmi.Query(@"\\.\root\wmi", "SELECT InstanceName, CycleCount FROM BatteryCycleCount", Wmi.DefaultTimeout);
        var notes = new List<string>();
        if (!stat.Ok) notes.Add("Tasarım kapasitesi okunamadı: " + stat.Error);
        if (!full.Ok) notes.Add("Tam şarj kapasitesi okunamadı: " + full.Error);

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
                s?.Str("DeviceName") ?? w.Str("Name") ?? w.Str("DeviceID") ?? "Batarya",
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
        logger.Info("Batarya: " + string.Join(" | ", list.Select(b => $"{b.Name}: {b.ChargeText}, {b.StatusText}, tam {b.FullText}, tasarım {b.DesignText}, sağlık {b.HealthText}, döngü {b.CycleText}")));
        return new BatteryReport(true, list, ac, remaining, notes.Count == 0 ? null : string.Join(" ", notes), null);

        static System.Management.ManagementBaseObject? Pick(WmiResult r, int index, int expected) =>
            r.Ok && r.Rows.Count == expected ? r.Rows[index] : null;
    }

    private static string? ChemistryText(long? c) => c switch
    {
        3 => "Kurşun asit", 4 => "Nikel kadmiyum", 5 => "Nikel metal hidrit", 6 => "Lityum iyon", 7 => "Çinko hava", 8 => "Lityum polimer",
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

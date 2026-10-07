using System.Collections.ObjectModel;
using System.Windows.Threading;
using RtxWindowsUpdater.Services;

namespace RtxWindowsUpdater.ViewModels;

/// <summary>
/// Cihaz Bilgileri kategorisi (Monster Kontrol Merkezi düzeni): Cihaz Bilgileri (donanım kartları), Cihaz Durumu (canlı ölçüm),
/// Hakkında bölmeleri. Canlı ölçüm YALNIZCA Cihaz Durumu bölmesi açıkken 2 saniyede bir yapılır; bölme kapanınca durur.
/// </summary>
public sealed partial class MainViewModel
{
    private DeviceMonitorService? _monitorInstance;
    private DispatcherTimer? _monitorTimer;
    private bool _sampling;
    private string _deviceStatusTime = L.T("Ölçülüyor...", "Measuring...");

    private DeviceMonitorService Monitor => _monitorInstance ??= new DeviceMonitorService(_logger);

    /// <summary>Cihaz Bilgileri kartları (İşlemci, Ekran Kartı, Bellek, Depolama, İşletim Sistemi) – mevcut sistem bilgisi alanlarından.</summary>
    public ObservableCollection<DeviceInfoSectionViewModel> DeviceSections { get; } = [];

    /// <summary>Cihaz Durumu satırları (gerçek ölçümler).</summary>
    public ObservableCollection<DeviceStatusRowViewModel> DeviceStatusRows { get; } = [];

    public string DeviceStatusTime { get => _deviceStatusTime; private set => Set(ref _deviceStatusTime, value); }

    /// <summary>Sistem bilgisi alanlarını gruplarına göre kartlara yerleştirir (yeni veri üretmez).</summary>
    private void RebuildDeviceSections()
    {
        (string Group, string Title, string Glyph)[] layout =
        [
            (SystemInfoGroups.Cpu, L.T("İşlemci Bilgileri", "Processor Information"), ""),
            (SystemInfoGroups.Gpu, L.T("Ekran Kartı Bilgileri", "Graphics Card Information"), ""),
            (SystemInfoGroups.Ram, L.T("Bellek Bilgileri", "Memory Information"), ""),
            (SystemInfoGroups.Storage, L.T("Depolama Aygıtı Bilgileri", "Storage Device Information"), ""),
            (SystemInfoGroups.Os, L.T("İşletim Sistemi", "Operating System"), "")
        ];
        DeviceSections.Clear();
        foreach (var (group, title, glyph) in layout)
        {
            var fields = SystemInfoFields.Where(f => f.Group == group).ToList();
            if (fields.Count > 0) DeviceSections.Add(new DeviceInfoSectionViewModel(title, glyph, fields));
        }
        var other = SystemInfoFields.Where(f => layout.All(l => l.Group != f.Group)).ToList();
        if (other.Count > 0) DeviceSections.Add(new DeviceInfoSectionViewModel(L.T("Diğer", "Other"), "", other));
    }

    // ------------------------------------------------------------------ canlı ölçüm (yalnızca Cihaz Durumu bölmesi açıkken)

    private void UpdateDeviceMonitoring()
    {
        if (IsDashboard && CurrentSectionKey == SectionKeys.DeviceStatus && _windowVisible) StartDeviceMonitoring();
        else StopDeviceMonitoring();
    }

    private void StartDeviceMonitoring()
    {
        if (_monitorTimer is not null) return;
        _logger.Info(L.T("Cihaz Durumu: canlı ölçüm başladı (2 saniyede bir).", "Device Status: live measurement started (every 2 seconds)."));
        DeviceStatusTime = L.T("Ölçülüyor...", "Measuring...");
        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += OnMonitorTick;
        _monitorTimer.Start();
        OnMonitorTick(null, EventArgs.Empty);
    }

    private void StopDeviceMonitoring()
    {
        if (_monitorTimer is null) return;
        _monitorTimer.Stop();
        _monitorTimer.Tick -= OnMonitorTick;
        _monitorTimer = null;
        // NVML serbest bırakılır (ekran kartı uyku durumuna geçebilir). Süren bir ölçüm varsa arayüz beklemesin.
        var monitor = _monitorInstance;
        if (monitor is not null) _ = Task.Run(monitor.Stop);
        _logger.Info(L.T("Cihaz Durumu: canlı ölçüm durdu.", "Device Status: live measurement stopped."));
    }

    private async void OnMonitorTick(object? sender, EventArgs e)
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var snapshot = await Task.Run(Monitor.Sample);
            if (_monitorTimer is null) return; // ekran bu sırada kapandı
            ApplyDeviceStatus(snapshot);
        }
        catch (Exception ex)
        {
            DeviceStatusTime = L.T("Ölçüm başarısız: ", "Measurement failed: ") + ex.Message;
            _logger.Warning(L.T("Cihaz Durumu ölçülemedi: ", "Device Status could not be measured: ") + ex.Message);
        }
        finally
        {
            _sampling = false;
        }
    }

    private readonly record struct GaugeSpec(string Caption, string Unit, double Maximum, bool IsTemperature, DeviceReading Reading, string Format = "0");

    private sealed record RowSpec(string Key, string Title, string Subtitle, string? Note, GaugeSpec[] Gauges);

    /// <summary>Ölçümü satırlara uygular. Yapı değişmedikçe satırlar yeniden oluşturulmaz; yalnızca değerler güncellenir.</summary>
    private void ApplyDeviceStatus(DeviceStatusSnapshot s)
    {
        var rows = new List<RowSpec>();
        var cpuName = SystemInfoFields.FirstOrDefault(f => f.Label == "CPU" && f.Available)?.Value ?? string.Empty;
        rows.Add(new RowSpec("cpu", L.T("İşlemci", "Processor"), cpuName, null,
        [
            new GaugeSpec(L.T("Kullanım", "Usage"), "%", 100, false, s.CpuUsage),
            new GaugeSpec(L.T("Termal bölge", "Thermal zone"), "°C", 100, true, s.ThermalZone)
        ]));

        if (s.Gpus.Count == 0)
            rows.Add(new RowSpec("gpu", L.T("Ekran Kartı", "Graphics Card"), string.Empty, s.GpuNote, []));
        foreach (var g in s.Gpus)
        {
            var vram = g.MemoryTotalBytes is > 0 && g.MemoryUsedBytes is { } used
                ? $" · VRAM {used / 1073741824.0:0.0} GB / {g.MemoryTotalBytes.Value / 1073741824.0:0.0} GB"
                : string.Empty;
            rows.Add(new RowSpec("gpu:" + g.Name, L.T("Ekran Kartı", "Graphics Card"), g.Name + vram, null,
            [
                new GaugeSpec(L.T("Kullanım", "Usage"), "%", 100, false, g.Usage),
                new GaugeSpec(L.T("Sıcaklık", "Temperature"), "°C", 100, true, g.Temperature),
                new GaugeSpec(L.T("Bellek", "Memory"), "%", 100, false, g.MemoryUsage)
            ]));
        }

        var memoryText = s.MemoryTotalBytes > 0
            ? L.T($"{s.MemoryUsedBytes / 1073741824.0:0.0} GB / {s.MemoryTotalBytes / 1073741824.0:0.0} GB kullanılıyor", $"{s.MemoryUsedBytes / 1073741824.0:0.0} GB / {s.MemoryTotalBytes / 1073741824.0:0.0} GB in use")
            : string.Empty;
        rows.Add(new RowSpec("ram", L.T("Bellek", "Memory"), memoryText, null, [new GaugeSpec(L.T("Kullanım", "Usage"), "%", 100, false, s.MemoryUsage)]));

        // Disk: etkin süre + okuma / yazma (PhysicalDisk sayaçları) ve sıcaklık (güvenilirlik sayacı; yönetici gerekir).
        if (s.DiskActivity.Count == 0 && s.Disks.Count == 0)
            rows.Add(new RowSpec("disk", L.T("Depolama", "Storage"), string.Empty, s.DiskActivityNote ?? s.DiskNote ?? L.T("Disk bilgisi henüz okunmadı", "Disk information not read yet"), []));
        foreach (var a in s.DiskActivity)
        {
            var disk = s.Disks.FirstOrDefault(d => d.Id == a.Id);
            var io = L.T($"Okuma {Services.Diagnostics.Formats.Rate(a.ReadBytesPerSec.Value ?? 0)} · Yazma {Services.Diagnostics.Formats.Rate(a.WriteBytesPerSec.Value ?? 0)}", $"Read {Services.Diagnostics.Formats.Rate(a.ReadBytesPerSec.Value ?? 0)} · Write {Services.Diagnostics.Formats.Rate(a.WriteBytesPerSec.Value ?? 0)}");
            rows.Add(new RowSpec("disk:" + a.Id, L.T("Depolama", "Storage"), $"{disk?.Name ?? a.Instance} · {io}", null,
            [
                new GaugeSpec(L.T("Etkin süre", "Active time"), "%", 100, false, a.ActiveTime),
                new GaugeSpec(L.T("Sıcaklık", "Temperature"), "°C", 100, true, disk?.Temperature ?? DeviceReading.Missing(s.DiskNote ?? L.T("Disk sıcaklığı okunmadı", "Disk temperature not read")))
            ]));
        }
        if (s.DiskActivity.Count == 0)
            foreach (var d in s.Disks)
                rows.Add(new RowSpec("disk:" + d.Id, L.T("Depolama", "Storage"), d.Name, s.DiskActivityNote, [new GaugeSpec(L.T("Sıcaklık", "Temperature"), "°C", 100, true, d.Temperature)]));

        // Ağ: bağlı bağdaştırıcıların toplam anlık aktarımı (Mbps).
        if (s.Network is { } net)
        {
            static DeviceReading Mbps(DeviceReading r) => r.Value is { } v ? new DeviceReading(v * 8 / 1_000_000) : r;
            rows.Add(new RowSpec("net", L.T("Ağ", "Network"), net.Adapters, null,
            [
                new GaugeSpec(L.T("İndirme", "Download"), "Mbps", 0, false, Mbps(net.ReceiveBytesPerSec), "0.0"),
                new GaugeSpec(L.T("Yükleme", "Upload"), "Mbps", 0, false, Mbps(net.SendBytesPerSec), "0.0")
            ]));
        }

        var fans = new List<GaugeSpec>();
        foreach (var g in s.Gpus.Where(g => g.Fan.Value is not null))
            fans.Add(new GaugeSpec(L.T("Ekran kartı", "Graphics card"), "%", 100, false, g.Fan));
        foreach (var f in s.Fans)
            fans.Add(new GaugeSpec(f.Name, "RPM", 0, false, f.Rpm)); // RPM için bilinen üst sınır yok: halka boş kalır, değer yazılır
        rows.Add(new RowSpec("fan", L.T("Fan Hızı", "Fan Speed"), string.Empty,
            fans.Count == 0
                ? L.T("Bu cihaz fan hızını Windows'un standart arayüzleriyle bildirmiyor (ekran kartı dahil). Fan bilgisi yalnızca üretici yazılımıyla okunabilir.", "This device does not report fan speed through the standard Windows interfaces (including the graphics card). Fan information can only be read with the manufacturer's software.")
                : null,
            fans.ToArray()));

        var sameShape = DeviceStatusRows.Count == rows.Count && rows.Select((r, i) =>
            DeviceStatusRows[i].Key == r.Key && DeviceStatusRows[i].Gauges.Count == r.Gauges.Length &&
            r.Gauges.Select((g, j) => DeviceStatusRows[i].Gauges[j].Caption == g.Caption).All(x => x)).All(x => x);
        if (!sameShape)
        {
            DeviceStatusRows.Clear();
            foreach (var r in rows)
            {
                var row = new DeviceStatusRowViewModel(r.Key, r.Title);
                foreach (var g in r.Gauges) row.Gauges.Add(new DeviceGaugeViewModel(g.Caption, g.Unit, g.Maximum, g.IsTemperature, g.Format));
                DeviceStatusRows.Add(row);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = DeviceStatusRows[i];
            row.Subtitle = rows[i].Subtitle;
            row.Note = rows[i].Note;
            for (var j = 0; j < rows[i].Gauges.Length; j++)
                row.Gauges[j].Update(rows[i].Gauges[j].Reading);
        }
        DeviceStatusTime = L.T($"Canlı · 2 saniyede bir güncellenir · Son ölçüm {s.Time:HH:mm:ss}", $"Live · updated every 2 seconds · Last measurement {s.Time:HH:mm:ss}");
    }
}

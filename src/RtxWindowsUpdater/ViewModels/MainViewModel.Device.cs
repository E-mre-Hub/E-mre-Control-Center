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
    private string _deviceStatusTime = "Ölçülüyor...";

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
            (SystemInfoGroups.Cpu, "İşlemci Bilgileri", ""),
            (SystemInfoGroups.Gpu, "Ekran Kartı Bilgileri", ""),
            (SystemInfoGroups.Ram, "Bellek Bilgileri", ""),
            (SystemInfoGroups.Storage, "Depolama Aygıtı Bilgileri", ""),
            (SystemInfoGroups.Os, "İşletim Sistemi", "")
        ];
        DeviceSections.Clear();
        foreach (var (group, title, glyph) in layout)
        {
            var fields = SystemInfoFields.Where(f => f.Group == group).ToList();
            if (fields.Count > 0) DeviceSections.Add(new DeviceInfoSectionViewModel(title, glyph, fields));
        }
        var other = SystemInfoFields.Where(f => layout.All(l => l.Group != f.Group)).ToList();
        if (other.Count > 0) DeviceSections.Add(new DeviceInfoSectionViewModel("Diğer", "", other));
    }

    // ------------------------------------------------------------------ canlı ölçüm (yalnızca Cihaz Durumu bölmesi açıkken)

    private void UpdateDeviceMonitoring()
    {
        if (IsDashboard && CurrentSectionKey == SectionKeys.DeviceStatus) StartDeviceMonitoring();
        else StopDeviceMonitoring();
    }

    private void StartDeviceMonitoring()
    {
        if (_monitorTimer is not null) return;
        _logger.Info("Cihaz Durumu: canlı ölçüm başladı (2 saniyede bir).");
        DeviceStatusTime = "Ölçülüyor...";
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
        _logger.Info("Cihaz Durumu: canlı ölçüm durdu.");
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
            DeviceStatusTime = "Ölçüm başarısız: " + ex.Message;
            _logger.Warning("Cihaz Durumu ölçülemedi: " + ex.Message);
        }
        finally
        {
            _sampling = false;
        }
    }

    private readonly record struct GaugeSpec(string Caption, string Unit, double Maximum, bool IsTemperature, DeviceReading Reading);

    private sealed record RowSpec(string Key, string Title, string Subtitle, string? Note, GaugeSpec[] Gauges);

    /// <summary>Ölçümü satırlara uygular. Yapı değişmedikçe satırlar yeniden oluşturulmaz; yalnızca değerler güncellenir.</summary>
    private void ApplyDeviceStatus(DeviceStatusSnapshot s)
    {
        var rows = new List<RowSpec>();
        var cpuName = SystemInfoFields.FirstOrDefault(f => f.Label == "CPU" && f.Available)?.Value ?? string.Empty;
        rows.Add(new RowSpec("cpu", "İşlemci", cpuName, null,
        [
            new GaugeSpec("Kullanım", "%", 100, false, s.CpuUsage),
            new GaugeSpec("Termal bölge", "°C", 100, true, s.ThermalZone)
        ]));

        if (s.Gpus.Count == 0)
            rows.Add(new RowSpec("gpu", "Ekran Kartı", string.Empty, s.GpuNote, []));
        foreach (var g in s.Gpus)
        {
            rows.Add(new RowSpec("gpu:" + g.Name, "Ekran Kartı", g.Name, null,
            [
                new GaugeSpec("Kullanım", "%", 100, false, g.Usage),
                new GaugeSpec("Sıcaklık", "°C", 100, true, g.Temperature),
                new GaugeSpec("Bellek", "%", 100, false, g.MemoryUsage)
            ]));
        }

        var memoryText = s.MemoryTotalBytes > 0
            ? $"{s.MemoryUsedBytes / 1073741824.0:0.0} GB / {s.MemoryTotalBytes / 1073741824.0:0.0} GB kullanılıyor"
            : string.Empty;
        rows.Add(new RowSpec("ram", "Bellek", memoryText, null, [new GaugeSpec("Kullanım", "%", 100, false, s.MemoryUsage)]));

        if (s.Disks.Count == 0)
            rows.Add(new RowSpec("disk", "Depolama", string.Empty, s.DiskNote ?? "Disk sıcaklığı henüz okunmadı", []));
        foreach (var d in s.Disks)
            rows.Add(new RowSpec("disk:" + d.Name, "Depolama", d.Name, null, [new GaugeSpec("Sıcaklık", "°C", 100, true, d.Temperature)]));

        var fans = new List<GaugeSpec>();
        foreach (var g in s.Gpus.Where(g => g.Fan.Value is not null))
            fans.Add(new GaugeSpec("Ekran kartı", "%", 100, false, g.Fan));
        foreach (var f in s.Fans)
            fans.Add(new GaugeSpec(f.Name, "RPM", 0, false, f.Rpm)); // RPM için bilinen üst sınır yok: halka boş kalır, değer yazılır
        rows.Add(new RowSpec("fan", "Fan Hızı", string.Empty,
            fans.Count == 0
                ? "Bu cihaz fan hızını Windows'un standart arayüzleriyle bildirmiyor (ekran kartı dahil). Fan bilgisi yalnızca üretici yazılımıyla okunabilir."
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
                foreach (var g in r.Gauges) row.Gauges.Add(new DeviceGaugeViewModel(g.Caption, g.Unit, g.Maximum, g.IsTemperature));
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
        DeviceStatusTime = $"Canlı · 2 saniyede bir güncellenir · Son ölçüm {s.Time:HH:mm:ss}";
    }
}

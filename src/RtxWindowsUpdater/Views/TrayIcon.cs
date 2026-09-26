using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Bildirim alanı (görev çubuğunun sağı / "^" gizli simgeler) simgesi: Windows'un Shell_NotifyIcon API'si, WinForms gerekmez.
/// Simge gizli bir pencereye bağlıdır (başlığı <see cref="AppSignals.WindowTitle"/>): çift tıklama / Enter → açma, sağ tık → menü.
/// Gezgin yeniden başlarsa (TaskbarCreated) simge yeniden eklenir. Aynı pencere ikinci örneğin "göster" ve kurulumun "kapan"
/// iletilerini de alır.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = 0x8000 + 0x21; // WM_APP + 0x21
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _window;
    private IntPtr _icon;
    private bool _ownsIcon;
    private string _tooltip;
    private bool _added;

    /// <summary>Çift tıklama / klavyeyle seçme: pencereyi aç.</summary>
    public event Action? OpenRequested;

    /// <summary>Sağ tık / menü tuşu: fiziksel piksel cinsinden ekran konumu.</summary>
    public event Action<Point>? MenuRequested;

    /// <summary>İkinci örnek başlatıldı (<see cref="AppSignals.ShowMessage"/>).</summary>
    public event Action? ShowSignal;

    /// <summary>Kurulum / kaldırma kapanma istedi (<see cref="AppSignals.ExitMessage"/>).</summary>
    public event Action? ExitSignal;

    public TrayIcon(string tooltip)
    {
        _tooltip = Trim(tooltip, 127);
        // Görünmeyen üst düzey pencere (ileti penceresi değil: TaskbarCreated yayını yalnızca üst düzey pencerelere gelir).
        _window = new HwndSource(new HwndSourceParameters(AppSignals.WindowTitle)
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW: Alt+Tab'da görünmez
            Width = 0,
            Height = 0
        });
        _window.AddHook(WndProc);
        // Yönetici olarak çalışırken normal yetkili Gezgin / ikinci örnek / kurulum bu iletileri gönderebilsin (yalnızca bunlar).
        foreach (var message in new[] { TaskbarCreated, AppSignals.ShowMessage, AppSignals.ExitMessage })
            ChangeWindowMessageFilterEx(_window.Handle, message, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        (_icon, _ownsIcon) = LoadAppIcon();
    }

    public IntPtr WindowHandle => _window.Handle;

    /// <summary>Fiziksel piksel → WPF birimi (menü konumu için; bildirim alanının bulunduğu ekranın ölçeği).</summary>
    public Point ToDip(Point device) => _window.CompositionTarget?.TransformFromDevice.Transform(device) ?? device;

    /// <summary>Simgeyi bildirim alanına ekler; başarıyı Windows'un yanıtıyla döndürür.</summary>
    public bool Show()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = ShellNotifyIcon(NimAdd, ref data);
        if (_added)
        {
            data.uVersionOrTimeout = 4; // NOTIFYICON_VERSION_4: klavye seçimi ve menü konumu doğru bildirilir
            ShellNotifyIcon(NimSetVersion, ref data);
        }
        return _added;
    }

    public void SetTooltip(string text)
    {
        text = Trim(text, 127);
        if (text == _tooltip) return;
        _tooltip = text;
        if (!_added) return;
        var data = NewData(NifTip | NifShowTip);
        ShellNotifyIcon(NimModify, ref data);
    }

    /// <summary>Windows bildirimi (simgeden). Windows sessiz saatlere ve kullanıcının bildirim ayarlarına uyar.</summary>
    public bool ShowBalloon(string title, string text)
    {
        if (!_added) return false;
        var data = NewData(NifInfo);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(text, 255);
        data.dwInfoFlags = 0x1 /* NIIF_INFO */ | 0x80 /* NIIF_RESPECT_QUIET_TIME */;
        return ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // Sürüm 4: lParam alt sözcüğü = olay, wParam = simge konumu (x, y; fiziksel piksel).
            var ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case 0x0203: // WM_LBUTTONDBLCLK
                case 0x0401: // NIN_KEYSELECT (Enter / Boşluk)
                    OpenRequested?.Invoke();
                    break;
                case 0x007B: // WM_CONTEXTMENU (sağ tık / menü tuşu)
                    var x = (short)(wParam.ToInt64() & 0xFFFF);
                    var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    MenuRequested?.Invoke(new Point(x, y));
                    break;
            }
            handled = true;
        }
        else if ((uint)msg == TaskbarCreated && TaskbarCreated != 0)
        {
            _added = false;
            Show(); // Gezgin yeniden başladı: simge kayboldu, yeniden eklenir
        }
        else if ((uint)msg == AppSignals.ShowMessage)
        {
            ShowSignal?.Invoke();
            handled = true;
        }
        else if ((uint)msg == AppSignals.ExitMessage)
        {
            ExitSignal?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Uygulamanın kendi ICO dosyasından bildirim alanı boyutuna en uygun görüntü (Windows ölçeğine göre 16 / 20 / 24 / 32 px).</summary>
    private static (IntPtr Handle, bool Owned) LoadAppIcon()
    {
        var size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/E-mre Control Center;component/Assets/E-mreLogo.ico"));
            if (info is null) return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false);
            byte[] ico;
            using (var ms = new MemoryStream())
            {
                info.Stream.CopyTo(ms);
                info.Stream.Dispose();
                ico = ms.ToArray();
            }
            // ICONDIR (6 bayt) + ICONDIRENTRY (16 bayt): genişlik (0 = 256), ..., bayt sayısı (8), ofset (12).
            var count = BitConverter.ToUInt16(ico, 4);
            int best = -1, bestWidth = int.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var e = 6 + i * 16;
                var w = ico[e] == 0 ? 256 : ico[e];
                if (w >= size && w < bestWidth) { best = i; bestWidth = w; }
            }
            if (best < 0) best = 0;
            var entry = 6 + best * 16;
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            var bits = new byte[length];
            Buffer.BlockCopy(ico, offset, bits, 0, length);
            var handle = CreateIconFromResourceEx(bits, (uint)length, true, 0x00030000, size, size, 0);
            return handle != IntPtr.Zero ? (handle, true) : (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or UriFormatException)
        {
            return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false); // IDI_APPLICATION (paylaşımlı; yok edilmez)
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero && _ownsIcon) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    // ------------------------------------------------------------------ Win32

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

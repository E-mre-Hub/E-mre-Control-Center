using System.Runtime.InteropServices;
using System.Text;

namespace RtxWindowsUpdater.Core;

/// <summary>Restart Manager'ın bildirdiği, kayıtlı dosyaları kullanan tek bir işlem.</summary>
public sealed record RmProcess(int ProcessId, long StartTime, string AppName, string ServiceName, RestartManager.AppType Type, uint SessionId);

/// <summary>
/// Windows Restart Manager API'si (rstrtmgr.dll). Kurulum programlarının "şu uygulamalar dosyaları kullanıyor"
/// tespiti için kullandığı resmi mekanizmadır: verilen dosyaları açık tutan (veya DLL olarak yüklemiş) işlemleri döndürür.
/// Bu sınıf yalnızca TESPİT yapar; hiçbir işlemi kapatmaz (RmShutdown kullanılmaz – Windows hizmetlerini de durdurabilir).
/// </summary>
public static class RestartManager
{
    public enum AppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000
    }

    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string strServiceShortName;
        public AppType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    /// <summary>
    /// Verilen dosyaları kullanan işlemleri döndürür. Hata durumunda Win32 hata kodu ile <see cref="System.ComponentModel.Win32Exception"/> fırlatır.
    /// </summary>
    public static IReadOnlyList<RmProcess> GetProcessesUsing(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return [];

        var key = new StringBuilder(CchRmSessionKey + 1);
        var rc = RmStartSession(out var session, 0, key);
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc, "RmStartSession başarısız");
        try
        {
            // Çok sayıda dosya parça parça kaydedilir (tek çağrıda aşırı büyük dizi göndermemek için).
            foreach (var chunk in files.Chunk(500))
            {
                rc = RmRegisterResources(session, (uint)chunk.Length, chunk, 0, null, 0, null);
                if (rc != 0) throw new System.ComponentModel.Win32Exception(rc, "RmRegisterResources başarısız");
            }

            uint needed, count = 0, reasons = 0;
            RM_PROCESS_INFO[]? infos = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                if (rc == 0) break;
                if (rc != ErrorMoreData) throw new System.ComponentModel.Win32Exception(rc, "RmGetList başarısız");
                count = needed;
                infos = new RM_PROCESS_INFO[needed];
            }
            if (rc != 0) throw new System.ComponentModel.Win32Exception(rc, "RmGetList başarısız");
            if (infos is null || count == 0) return [];

            var list = new List<RmProcess>((int)count);
            for (var i = 0; i < count; i++)
            {
                var p = infos[i];
                var ft = ((long)(uint)p.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)p.Process.ProcessStartTime.dwLowDateTime;
                list.Add(new RmProcess(p.Process.dwProcessId, ft, p.strAppName ?? string.Empty,
                    p.strServiceShortName ?? string.Empty, p.ApplicationType, p.TSSessionId));
            }
            return list;
        }
        finally
        {
            RmEndSession(session);
        }
    }
}

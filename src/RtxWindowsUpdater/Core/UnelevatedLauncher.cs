using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Yönetici olarak çalışan bu uygulamadan, kullanıcının YAZABİLDİĞİ konumdaki bir programı (ör. kullanıcı profiline kurulan
/// Speedtest by Ookla aracı) YÖNETİCİ YETKİSİ OLMADAN başlatır. Neden: normal kullanıcı yetkisiyle çalışan zararlı bir program o
/// dosyayı değiştirirse, yönetici yetkisiyle çalıştırılması onu yükseltmiş olurdu.
/// Belirteç: Windows SAFER "normal kullanıcı" düzeyi (SaferComputeTokenFromLevel – Yöneticiler grubu yalnızca reddetme, yönetici
/// ayrıcalıkları kaldırılmış) + ORTA bütünlük düzeyi. Süreç bu belirteçle CreateProcessAsUser ile başlatılır (belirteç çağıranın
/// kendi belirtecinin kısıtlanmış hâli olduğu için ek ayrıcalık gerekmez). stdin / stdout / stderr borulara yönlendirilir; alt sürece
/// devredilen tanıtıcılar PROC_THREAD_ATTRIBUTE_HANDLE_LIST ile YALNIZCA bu borularla sınırlanır.
/// </summary>
internal sealed class UnelevatedLauncher : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly ProcessWaitHandle _exitEvent;

    public int ProcessId { get; }
    public FileStream StdOut { get; }
    public FileStream StdErr { get; }

    private UnelevatedLauncher(SafeProcessHandle process, int pid, FileStream stdout, FileStream stderr)
    {
        _process = process;
        ProcessId = pid;
        StdOut = stdout;
        StdErr = stderr;
        _exitEvent = new ProcessWaitHandle(process);
    }

    public bool HasExited => _exitEvent.WaitOne(0);

    public int ExitCode => GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

    /// <summary>Süreç bitince tamamlanır.</summary>
    public Task WaitForExitAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) =>
        {
            tcs.TrySetResult();
            registration?.Unregister(null);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return tcs.Task;
    }

    public void Kill()
    {
        if (!HasExited) TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        StdOut.Dispose();
        StdErr.Dispose();
        _exitEvent.Dispose();
        _process.Dispose();
    }

    public static UnelevatedLauncher Start(string fileName, string arguments, string workingDirectory)
    {
        IntPtr level = IntPtr.Zero, token = IntPtr.Zero, sid = IntPtr.Zero, label = IntPtr.Zero, attributes = IntPtr.Zero, handleList = IntPtr.Zero;
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, inRead = null, inWrite = null;
        var pi = default(ProcessInformation);
        try
        {
            // 1) Normal kullanıcı belirteci (yönetici ayrıcalıkları yok) + Orta bütünlük düzeyi
            if (!SaferCreateLevel(SaferScopeIdUser, SaferLevelIdNormalUser, SaferLevelOpen, out level, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("SaferCreateLevel başarısız", "SaferCreateLevel failed"));
            if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out token, 0, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("SaferComputeTokenFromLevel başarısız", "SaferComputeTokenFromLevel failed"));
            if (!ConvertStringSidToSidW("S-1-16-8192", out sid)) // Orta bütünlük düzeyi
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("ConvertStringSidToSid başarısız", "ConvertStringSidToSid failed"));
            var labelSize = Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(sid);
            label = Marshal.AllocHGlobal(labelSize);
            Marshal.StructureToPtr(new TokenMandatoryLabel { Sid = sid, Attributes = SeGroupIntegrity }, label, false);
            if (!SetTokenInformation(token, TokenIntegrityLevel, label, (uint)labelSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("SetTokenInformation (bütünlük düzeyi) başarısız", "SetTokenInformation (integrity level) failed"));

            // 2) Borular: alt sürecin uçları devralınabilir, bu sürecin uçları devralınamaz
            CreateInheritablePipe(out outRead, out outWrite, childEndIsWrite: true);
            CreateInheritablePipe(out errRead, out errWrite, childEndIsWrite: true);
            CreateInheritablePipe(out inRead, out inWrite, childEndIsWrite: false);
            inWrite.Dispose(); // girdi beklenmez: alt süreç stdin'de dosya sonunu görür

            // 3) Devralınacak tanıtıcılar YALNIZCA bu üç boru ucu
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("InitializeProcThreadAttributeList başarısız", "InitializeProcThreadAttributeList failed"));
            var handles = new[] { inRead.DangerousGetHandle(), outWrite.DangerousGetHandle(), errWrite.DangerousGetHandle() };
            handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, handleList, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributeHandleList, handleList,
                    (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("UpdateProcThreadAttribute başarısız", "UpdateProcThreadAttribute failed"));

            var si = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = handles[0],
                    hStdOutput = handles[1],
                    hStdError = handles[2]
                },
                lpAttributeList = attributes
            };
            var commandLine = new StringBuilder($"\"{fileName}\" {arguments}".TrimEnd());
            if (!CreateProcessAsUserW(token, fileName, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    CreateNoWindow | ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(pi.hThread);
            pi.hThread = IntPtr.Zero;
            var launcher = new UnelevatedLauncher(new SafeProcessHandle(pi.hProcess, ownsHandle: true), pi.dwProcessId,
                new FileStream(outRead, FileAccess.Read, 4096, isAsync: false), new FileStream(errRead, FileAccess.Read, 4096, isAsync: false));
            pi.hProcess = IntPtr.Zero;
            outRead = errRead = null; // FileStream'ler sahiplendi
            return launcher;
        }
        finally
        {
            outWrite?.Dispose();
            errWrite?.Dispose();
            inRead?.Dispose();
            outRead?.Dispose();
            errRead?.Dispose();
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (label != IntPtr.Zero) Marshal.FreeHGlobal(label);
            if (sid != IntPtr.Zero) LocalFree(sid);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (level != IntPtr.Zero) SaferCloseLevel(level);
        }
    }

    /// <summary>Süreç tanıtıcısını bekleme nesnesi olarak sarar (kendi olay tanıtıcısını oluşturmaz; süreç tanıtıcısının sahibi değildir).</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private static void CreateInheritablePipe(out SafeFileHandle read, out SafeFileHandle write, bool childEndIsWrite)
    {
        var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        if (!CreatePipe(out read, out write, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("CreatePipe başarısız", "CreatePipe failed"));
        // Bu sürecin ucu alt sürece devredilmez
        var parentEnd = childEndIsWrite ? read : write;
        if (!SetHandleInformation(parentEnd, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("SetHandleInformation başarısız", "SetHandleInformation failed"));
    }

    // ------------------------------------------------------------------ Win32

    private const uint SaferScopeIdUser = 2;
    private const uint SaferLevelIdNormalUser = 0x20000;
    private const uint SaferLevelOpen = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;
    private const uint HandleFlagInherit = 1;
    private const int ProcThreadAttributeHandleList = 0x20002;
    private const int StartfUseStdHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCreateLevel(uint scopeId, uint levelId, uint openFlags, out IntPtr levelHandle, IntPtr reserved);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferComputeTokenFromLevel(IntPtr levelHandle, IntPtr inAccessToken, out IntPtr outAccessToken, uint flags,
        IntPtr reserved);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCloseLevel(IntPtr levelHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

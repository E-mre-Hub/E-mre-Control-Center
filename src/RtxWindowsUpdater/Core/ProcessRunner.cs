using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RtxWindowsUpdater.Core;

public sealed class ProcessResult
{
    public int ExitCode { get; init; } = -1;
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>İşlem hiç başlatılamadıysa (ör. dosya bulunamadı) hata mesajı.</summary>
    public string? StartError { get; init; }

    /// <summary>Win32 başlatma hata kodu (2 = dosya bulunamadı).</summary>
    public int StartErrorCode { get; init; }

    public bool Started => StartError is null;
    public bool Succeeded => Started && !TimedOut && !Cancelled && ExitCode == 0;
    public string ExitCodeHex => $"0x{unchecked((uint)ExitCode):X8}";
}

/// <summary>
/// Harici komutları pencere açmadan çalıştırır; stdout/stderr'i satır satır yakalar,
/// zaman aşımı ve iptalde tüm süreç ağacını sonlandırır. Hiçbir durumda istisna fırlatmaz,
/// tüm sonuçları <see cref="ProcessResult"/> olarak döndürür.
/// </summary>
public static class ProcessRunner
{
    /// <param name="displayCommand">
    /// İşlem kaydında (Detaylı Sonuç paneli) gösterilecek komut. Boşsa "dosya argümanlar" kullanılır.
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        Encoding? outputEncoding = null,
        string? displayCommand = null)
    {
        var startedAt = DateTime.Now;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var command = displayCommand ?? $"{Path.GetFileName(fileName)} {arguments}".Trim();
        var result = await RunCoreAsync(fileName, arguments, timeout, cancellationToken, onStdOut, onStdErr, outputEncoding)
            .ConfigureAwait(false);
        ExecutionTrace.Record(command, startedAt, watch.Elapsed, result);
        return result;
    }

    /// <summary>
    /// Bir sistem aracını cmd.exe üzerinden çalıştırır (bkz. <see cref="CmdCommand"/>). stdout/stderr yakalama,
    /// çıkış kodu, zaman aşımı ve süreç ağacını sonlandırma davranışı <see cref="RunAsync"/> ile aynıdır;
    /// cmd.exe /c, çalıştırdığı programın çıkış kodunu aynen döndürür.
    /// </summary>
    public static Task<ProcessResult> RunCmdAsync(
        string executable,
        IEnumerable<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        Encoding? outputEncoding = null,
        bool waitForGuiApp = false)
    {
        string cmdArgs;
        try
        {
            cmdArgs = CmdCommand.BuildArguments(executable, args, waitForGuiApp);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new ProcessResult { StartError = ex.Message });
        }
        return RunAsync(CmdCommand.CmdPath, cmdArgs, timeout, cancellationToken, onStdOut, onStdErr, outputEncoding,
            displayCommand: CmdCommand.Display(cmdArgs));
    }

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        Encoding? outputEncoding)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var outClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outClosed.TrySetResult(); return; }
            lock (stdout) stdout.AppendLine(e.Data);
            SafeInvoke(onStdOut, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errClosed.TrySetResult(); return; }
            lock (stderr) stderr.AppendLine(e.Data);
            SafeInvoke(onStdErr, e.Data);
        };

        try
        {
            if (!process.Start())
                return new ProcessResult { StartError = $"'{fileName}' başlatılamadı." };
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult
            {
                StartError = ex.NativeErrorCode == 2
                    ? $"'{fileName}' sistemde bulunamadı."
                    : $"'{fileName}' başlatılamadı: {ex.Message}",
                StartErrorCode = ex.NativeErrorCode
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult { StartError = $"'{fileName}' başlatılamadı: {ex.Message}" };
        }

        try { process.StandardInput.Close(); } catch { /* etkileşimli girdi beklenmiyor */ }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            KillTree(process);
        }

        // Çıktı akışlarının boşalmasını kısa bir süre bekle.
        await Task.WhenAny(Task.WhenAll(outClosed.Task, errClosed.Task), Task.Delay(3000)).ConfigureAwait(false);

        int exitCode;
        try { exitCode = process.HasExited ? process.ExitCode : -1; }
        catch { exitCode = -1; }

        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();

        return new ProcessResult
        {
            ExitCode = exitCode,
            StdOut = o,
            StdErr = e2,
            TimedOut = timedOut,
            Cancelled = cancelled
        };
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Süreç zaten kapanmış olabilir.
        }
    }

    private static void SafeInvoke(Action<string>? callback, string line)
    {
        if (callback is null) return;
        try { callback(line); } catch { /* callback hatası yakalamayı bozmamalı */ }
    }

    /// <summary>ProcessResult için kullanıcıya gösterilecek kısa hata açıklaması.</summary>
    public static string Describe(ProcessResult r, string toolName)
    {
        if (!r.Started) return r.StartError!;
        if (r.TimedOut) return $"{toolName} zaman aşımına uğradı ve sonlandırıldı.";
        if (r.Cancelled) return $"{toolName} işlemi iptal edildi.";
        var lastLine = LastMeaningfulLine(r.StdErr) ?? LastMeaningfulLine(r.StdOut);
        return lastLine is null
            ? $"{toolName} hata kodu ile sonlandı: {r.ExitCodeHex}"
            : $"{toolName} hata kodu {r.ExitCodeHex}: {lastLine}";
    }

    public static string? LastMeaningfulLine(string text)
    {
        return text
            .Split('\n')
            .Select(l => l.Trim().Trim('\r'))
            .LastOrDefault(l => l.Length > 2 && l.Any(char.IsLetter));
    }
}

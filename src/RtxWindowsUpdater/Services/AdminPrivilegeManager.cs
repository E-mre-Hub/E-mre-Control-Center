using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace RtxWindowsUpdater.Services;

public enum ElevationOutcome
{
    Started,      // Yönetici örneği başlatıldı – bu örnek kapanmalı
    Declined,     // Kullanıcı UAC penceresinde "Hayır" dedi
    Failed        // Başka bir nedenle başlatılamadı
}

/// <summary>
/// Yönetici yetkisini denetler ve gerekirse uygulamayı Windows UAC ("runas") üzerinden
/// yeniden başlatır. UAC hiçbir şekilde atlatılmaz; onay tamamen kullanıcıya aittir.
/// </summary>
public static class AdminPrivilegeManager
{
    public const string ArgAccepted = "--accepted";
    public const string ArgStartCheck = "--start-check";
    public const string ArgElevated = "--elevated";

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: UAC reddedildi

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static (ElevationOutcome Outcome, string? Error) RelaunchElevated(params string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return (ElevationOutcome.Failed, "Uygulamanın dosya yolu belirlenemedi.");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', args.Append(ArgElevated)),
            UseShellExecute = true, // "runas" fiili yalnızca ShellExecute ile çalışır
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        try
        {
            Process.Start(psi);
            return (ElevationOutcome.Started, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (ElevationOutcome.Declined, "Yönetici izni kullanıcı tarafından reddedildi.");
        }
        catch (Exception ex)
        {
            return (ElevationOutcome.Failed, $"Yönetici olarak başlatılamadı: {ex.Message}");
        }
    }
}

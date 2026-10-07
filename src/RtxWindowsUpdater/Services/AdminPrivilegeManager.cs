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

    /// <summary>
    /// İşlemin yönetici belirteciyle çalışıp çalışmadığı. Bir işlemin yükseltme durumu çalışırken değişemeyeceği için
    /// belirteç bir kez okunur; sonraki kontroller aynı gerçek değeri kullanır (her modülde yeniden sorgu yapılmaz).
    /// </summary>
    public static bool IsElevated => Elevated.Value;

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    });

    public static (ElevationOutcome Outcome, string? Error) RelaunchElevated(params string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return (ElevationOutcome.Failed, L.T("Uygulamanın dosya yolu belirlenemedi.", "The app's file path could not be determined."));

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', args.Concat(Core.LaunchModes.PreferenceArgs()).Append(ArgElevated)),
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
            return (ElevationOutcome.Declined, L.T("Yönetici izni kullanıcı tarafından reddedildi.", "Administrator permission was denied by the user."));
        }
        catch (Exception ex)
        {
            return (ElevationOutcome.Failed, L.T($"Yönetici olarak başlatılamadı: {ex.Message}", $"Could not start as administrator: {ex.Message}"));
        }
    }
}

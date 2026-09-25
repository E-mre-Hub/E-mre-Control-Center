using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RtxWindowsUpdater.Core;

/// <summary>
/// Yalnızca Yöneticiler (S-1-5-32-544) ve SYSTEM (S-1-5-18) erişimli klasör (miras kapalı). Yönetici işleminin çalıştıracağı dosyalar
/// (kaldırıcının geçici kopyası, indirilen güncelleme) burada tutulur: standart kullanıcı yetkisiyle çalışan bir işlem dosyayı
/// değiştiremez, klasörü silemez veya yeniden adlandıramaz.
/// </summary>
internal static class ProtectedDirectory
{
    public static void Create(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
}

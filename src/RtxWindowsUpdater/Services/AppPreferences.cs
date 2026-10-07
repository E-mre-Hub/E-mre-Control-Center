using System.IO;
using System.Text.Json;
using RtxWindowsUpdater.Core;

namespace RtxWindowsUpdater.Services;

/// <summary>
/// Kurulum / kaldırma ekranı için kullanıcının uygulamada seçtiği dil ve tema (state.json'dan SALT OKUNUR; dosya / klasör
/// oluşturulmaz, eski veri kopyalanmaz – kurulum uygulamanın veri klasörüne yazmaz). Okunamazsa (null, null).
/// </summary>
public static class AppPreferences
{
    public static (string? Language, string? Theme) TryRead()
    {
        try
        {
            var path = Path.Combine(AppInfo.DataDirectory, "state.json");
            if (!File.Exists(path)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string? Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("Language"), Get("Theme"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }
}

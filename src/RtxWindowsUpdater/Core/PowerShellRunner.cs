using System.IO;
using System.Text;
using System.Text.Json;

namespace RtxWindowsUpdater.Core;

public sealed class PowerShellResult
{
    public required ProcessResult Process { get; init; }

    /// <summary>Betiğin "##RESULT|" satırıyla döndürdüğü JSON; yoksa null.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>Betiğin kendi yakaladığı hata mesajı (JSON içindeki "error").</summary>
    public string? ScriptError { get; init; }

    public bool Ok => Data is not null && ScriptError is null;

    public string DescribeFailure(string what)
    {
        if (ScriptError is not null) return ScriptError;
        if (!Process.Succeeded || Data is null)
            return ProcessRunner.Describe(Process, what);
        return $"{what} bilinmeyen bir hata döndürdü.";
    }
}

/// <summary>
/// Windows PowerShell 5.1 betiklerini (-EncodedCommand ile) ayrı süreçte çalıştırır.
/// Betikler iki tür satır yazar:
///   ##LOG|mesaj      → arayüzdeki işlem günlüğüne aktarılır
///   ##RESULT|{json}  → tek yapılandırılmış sonuç
/// Ayrı süreç kullanmak; COM çağrıları takılsa bile zaman aşımında güvenle sonlandırmayı sağlar.
/// </summary>
public static class PowerShellRunner
{
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        function Write-Log([string]$m) { [Console]::Out.WriteLine('##LOG|' + $m); [Console]::Out.Flush() }
        function Write-Result($obj) { [Console]::Out.WriteLine('##RESULT|' + ($obj | ConvertTo-Json -Depth 6 -Compress)); [Console]::Out.Flush() }
        function Write-Failure($err) {
            $ex = $err.Exception
            $h = if ($ex) { '0x{0:X8}' -f $ex.HResult } else { '' }
            $msg = if ($ex) { $ex.Message } else { [string]$err }
            Write-Result @{ error = $msg; hresult = $h }
        }

        """;

    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>JSON argümanını betiğe güvenle gömmek için tek tırnaklı PowerShell dizesine çevirir.</summary>
    public static string ToPsLiteral(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return "'" + json.Replace("'", "''") + "'";
    }

    public static async Task<PowerShellResult> RunAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onLog = null,
        string? traceName = null)
    {
        var fullScript = Prelude + "try {\n" + script + "\n} catch { Write-Failure $_ }\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(fullScript));
        var args = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";

        string? resultLine = null;
        var result = await ProcessRunner.RunAsync(
            PowerShellPath, args, timeout, cancellationToken,
            onStdOut: line =>
            {
                if (line.StartsWith("##LOG|", StringComparison.Ordinal))
                    onLog?.Invoke(line[6..]);
                else if (line.StartsWith("##RESULT|", StringComparison.Ordinal))
                    resultLine = line[9..];
            },
            displayCommand: "powershell.exe: " + (traceName ?? "PowerShell betiği")).ConfigureAwait(false);

        JsonElement? data = null;
        string? scriptError = null;
        if (resultLine is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(resultLine);
                var root = doc.RootElement.Clone();
                data = root;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.String)
                {
                    var hr = root.TryGetProperty("hresult", out var h) ? h.GetString() : null;
                    scriptError = string.IsNullOrEmpty(hr) ? err.GetString() : $"{err.GetString()} ({hr})";
                }
            }
            catch (JsonException ex)
            {
                scriptError = $"PowerShell sonucu okunamadı: {ex.Message}";
            }
        }
        else if (result.Started && !result.TimedOut && !result.Cancelled)
        {
            var errLine = ProcessRunner.LastMeaningfulLine(result.StdErr);
            scriptError = errLine is null
                ? $"PowerShell betiği sonuç döndürmedi (çıkış kodu {result.ExitCodeHex})."
                : $"PowerShell hatası: {errLine}";
        }

        return new PowerShellResult { Process = result, Data = data, ScriptError = scriptError };
    }
}

public static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    public static long? Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var l)
            ? l
            : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().ToList(),
            JsonValueKind.Object => [v], // PowerShell 5.1 tek elemanlı dizileri nesneye çevirebilir
            _ => []
        };
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;

namespace WinNetControl.Core;

/// <summary>
/// Runs shell commands (netsh / powershell) with the correct elevation strategy.
///
/// Rule: If the current process is ALREADY an Administrator we do NOT use
/// UseShellExecute=true + Verb="runas" — that spawns a second UAC prompt which
/// fails or shows a confusing double-prompt.  Instead we launch the child
/// directly under the same elevated token via UseShellExecute=false.
///
/// PowerShell scripts are always written to a temp .ps1 file and executed with
/// powershell -File, which completely eliminates command-line quoting / brace-
/// escaping problems (the root cause of the "MissingEndCurlyBrace" error).
/// </summary>
public static class ElevatedRunner
{
    // Cached once at startup — elevation level doesn't change within a session.
    private static readonly bool _isAdmin = CheckIsAdmin();

    private static bool CheckIsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>True when the current process is already running as Administrator.</summary>
    public static bool IsAdmin => _isAdmin;

    // ── netsh ─────────────────────────────────────────────────────────────────

    /// <summary>Runs <c>netsh &lt;args&gt;</c> inheriting the current token.</summary>
    public static (bool ok, string output) RunNetsh(string args)
        => RunDirect("netsh", args);

    // ── PowerShell ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a PowerShell script string.  The script is written verbatim to a
    /// .ps1 temp file so no command-line quoting or brace escaping is needed.
    /// </summary>
    public static Task<(bool ok, string output)> RunPowerShellAsync(string psScript)
        => Task.Run(() => RunPsFile(psScript));

    public static (bool ok, string output) RunPowerShell(string psScript)
        => RunPsFile(psScript);

    /// <summary>
    /// Runs a PowerShell script, capturing full error details on failure.
    /// Uses a temp .ps1 file — completely immune to brace/quote escaping bugs.
    /// </summary>
    public static Task<(bool ok, string error)> RunPowerShellWithErrorAsync(string psScript)
        => Task.Run(() => RunPsWithError(psScript));

    public static (bool ok, string error) RunPowerShellWithError(string psScript)
        => RunPsWithError(psScript);

    // ── Private implementation ────────────────────────────────────────────────

    private static (bool ok, string error) RunPsWithError(string psScript)
    {
        string errFile = TempFile("pserr", ".txt");

        // Build wrapper script as a plain multiline string — written directly to
        // a .ps1 file so there is NO shell-level quoting or brace escaping at all.
        string wrapped =
            "$ErrorActionPreference = 'Stop'\r\n" +
            "try {\r\n" +
            psScript + "\r\n" +
            "} catch {\r\n" +
            "    $_ | Format-List * | Out-String |" +
            "    Out-File -LiteralPath '" + errFile.Replace("'", "''") + "' -Encoding utf8\r\n" +
            "    exit 1\r\n" +
            "}\r\n";

        try
        {
            var (ok, stdout) = RunPsFile(wrapped);
            if (ok) return (true, "");

            string details = "";
            if (File.Exists(errFile))
            {
                details = File.ReadAllText(errFile).Trim();
                if (details.Length > 800) details = details[..800] + "…";
            }
            return (false, string.IsNullOrWhiteSpace(details)
                ? (string.IsNullOrWhiteSpace(stdout) ? "Command failed." : stdout.Trim())
                : details);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { TryDelete(errFile); }
    }

    private static (bool ok, string output) RunPsFile(string psScript)
    {
        string scriptPath = TempFile("ps", ".ps1");
        try
        {
            File.WriteAllText(scriptPath, psScript, System.Text.Encoding.UTF8);

            string psArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";

            if (_isAdmin)
            {
                // Already elevated — run directly, inherit token, capture I/O
                return RunDirect("powershell", psArgs);
            }
            else
            {
                // Not elevated — need UAC prompt.  Capture output via a second temp file.
                string outPath = TempFile("psout", ".txt");
                string wrapScript =
                    "& powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                    $"-File '{scriptPath.Replace("'", "''")}' " +
                    $"*>&1 | Out-File -LiteralPath '{outPath.Replace("'", "''")}' -Encoding utf8";
                string wrapPath = TempFile("pswrap", ".ps1");
                try
                {
                    File.WriteAllText(wrapPath, wrapScript, System.Text.Encoding.UTF8);
                    var psi = new ProcessStartInfo("powershell",
                        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{wrapPath}\"")
                    {
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return (false, "Could not start PowerShell.");
                    p.WaitForExit(30_000);
                    string result = File.Exists(outPath) ? File.ReadAllText(outPath).Trim() : "";
                    return (p.ExitCode == 0, result);
                }
                finally { TryDelete(wrapPath); TryDelete(outPath); }
            }
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { TryDelete(scriptPath); }
    }

    /// <summary>Runs an executable directly, inheriting the current elevated token.</summary>
    private static (bool ok, string output) RunDirect(string exe, string args, int timeoutMs = 30_000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "Could not start process.");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            bool ok = p.ExitCode == 0;
            string combined = ok
                ? stdout.Trim()
                : (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim());
            return (ok, combined);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string TempFile(string prefix, string ext)
        => Path.Combine(Path.GetTempPath(), $"wnc_{prefix}_{Guid.NewGuid():N}{ext}");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

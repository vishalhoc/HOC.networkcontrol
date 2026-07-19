using System;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Threading.Tasks;

namespace WinNetControl.Core;

/// <summary>
/// Wraps netsh advfirewall and Windows shell to manage firewall rules,
/// network tools, and startup tasks.
/// </summary>
public static class FirewallService
{
    // ─────────────────────────────────────────────────────────────────────────
    // Admin / firewall state
    // ─────────────────────────────────────────────────────────────────────────

    public static bool IsAdministrator()
    {
        using var identity  = WindowsIdentity.GetCurrent();
        var       principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void EnsureFirewallEnabled()
    {
        try { RunNetsh("advfirewall set allprofiles state on"); } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // App-level block / unblock
    // ─────────────────────────────────────────────────────────────────────────

    public static void BlockApp(string appName, string appPath,
                                bool blockInbound = true, bool blockOutbound = true)
    {
        if (string.IsNullOrWhiteSpace(appPath)) return;
        if (blockInbound)  BlockAppInbound(appName, appPath);
        if (blockOutbound) BlockAppOutbound(appName, appPath);
    }

    public static void UnblockApp(string appName)
    {
        string ruleIn = $"WinNetControl_Block_{appName}_In";
        string ruleOut = $"WinNetControl_Block_{appName}_Out";

        var psiIn = new System.Diagnostics.ProcessStartInfo("netsh", $"advfirewall firewall delete rule name=\"{ruleIn}\"");
        psiIn.CreateNoWindow = true;
        psiIn.UseShellExecute = true;
        psiIn.Verb = "runas";
        System.Diagnostics.Process.Start(psiIn)?.WaitForExit();

        var psiOut = new System.Diagnostics.ProcessStartInfo("netsh", $"advfirewall firewall delete rule name=\"{ruleOut}\"");
        psiOut.CreateNoWindow = true;
        psiOut.UseShellExecute = true;
        psiOut.Verb = "runas";
        System.Diagnostics.Process.Start(psiOut)?.WaitForExit();

        HistoryLogService.AddLog("Firewall Rule Removed", appName, "Removed both Inbound and Outbound block rules");
    }

    public static void BlockAppInbound(string appName, string appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath)) return;
        string rule = InboundRuleName(appName);
        DeleteRule(rule);
        var psi = new System.Diagnostics.ProcessStartInfo("netsh", $"advfirewall firewall add rule name=\"{rule}\" dir=in action=block program=\"{appPath}\" enable=yes");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = true;
        psi.Verb = "runas";
        System.Diagnostics.Process.Start(psi)?.WaitForExit();
        HistoryLogService.AddLog("Firewall Rule Added", appName, $"Direction: Inbound, Path: {appPath}");
    }

    public static void BlockAppOutbound(string appName, string appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath)) return;
        string rule = OutboundRuleName(appName);
        DeleteRule(rule);
        RunNetsh($"advfirewall firewall add rule name=\"{rule}\" " +
                 $"dir=out action=block program=\"{appPath}\" enable=yes");
        HistoryLogService.AddLog("Firewall Rule Added", appName, $"Direction: Outbound, Path: {appPath}");
    }

    public static void UnblockAppOutbound(string appName)
    {
        DeleteRule(OutboundRuleName(appName));
        HistoryLogService.AddLog("Firewall Rule Removed", appName, "Removed Outbound block rule");
    }

    public static void UnblockAppInbound(string appName)
    {
        DeleteRule(InboundRuleName(appName));
        HistoryLogService.AddLog("Firewall Rule Removed", appName, "Removed Inbound block rule");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-connection block / unblock  (remote IP + port rules)
    // ─────────────────────────────────────────────────────────────────────────

    public static void BlockConnection(string appPath, string remoteAddress,
                                       int remotePort, int localPort,
                                       bool blockInbound = true, bool blockOutbound = true)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress) || remoteAddress == "*") return;

        string tag = $"{remoteAddress}_{remotePort}_{localPort}";
        if (blockInbound)
        {
            string rule = $"WinNetControl_ConnIn_{tag}";
            DeleteRule(rule);
            RunNetsh($"advfirewall firewall add rule name=\"{rule}\" dir=in action=block " +
                     $"remoteip=\"{remoteAddress}\" remoteport={remotePort} " +
                     (string.IsNullOrWhiteSpace(appPath) ? "" : $"program=\"{appPath}\" ") +
                     "protocol=TCP enable=yes");
        }
        if (blockOutbound)
        {
            string rule = $"WinNetControl_ConnOut_{tag}";
            DeleteRule(rule);
            RunNetsh($"advfirewall firewall add rule name=\"{rule}\" dir=out action=block " +
                     $"remoteip=\"{remoteAddress}\" remoteport={remotePort} " +
                     (string.IsNullOrWhiteSpace(appPath) ? "" : $"program=\"{appPath}\" ") +
                     "protocol=TCP enable=yes");
        }
    }

    public static void UnblockConnection(string appPath, string remoteAddress,
                                         int remotePort, int localPort)
    {
        string tag = $"{remoteAddress}_{remotePort}_{localPort}";
        DeleteRule($"WinNetControl_ConnIn_{tag}");
        DeleteRule($"WinNetControl_ConnOut_{tag}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generic named outbound block (HTTP Inspector / domain blocking)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Add a named outbound block rule for a remote IP and optional port.</summary>
    public static (bool ok, string error) AddOutboundBlockRule(
        string ruleName, string remoteIp = "", int port = 0)
    {
        string ipPart   = string.IsNullOrWhiteSpace(remoteIp) ? "" : $"remoteip=\"{remoteIp}\" ";
        string portPart = port > 0 ? $"remoteport={port} protocol=TCP " : "";
        string cmd = $"advfirewall firewall add rule name=\"{ruleName}\" " +
                     $"dir=out action=block {ipPart}{portPart}enable=yes";
        return RunNetshResult(cmd);
    }

    public static (bool ok, string error) RemoveRule(string ruleName)
        => RunNetshResult($"advfirewall firewall delete rule name=\"{ruleName}\"");

    public static bool RuleExists(string ruleName)
    {
        var psi = new ProcessStartInfo("netsh",
            $"advfirewall firewall show rule name=\"{ruleName}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi);
        string output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        return output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DNS / network tools
    // ─────────────────────────────────────────────────────────────────────────

    public static (bool ok, string output) FlushDns()
        => RunCmd("ipconfig", "/flushdns");

    public static (bool ok, string output) ResetWinsock()
        => RunNetshResult("winsock reset");

    public static (bool ok, string output) ResetTcpIp()
        => RunNetshResult("int ip reset");

    public static (bool ok, string output) ReleaseIp()
        => RunCmd("ipconfig", "/release");

    public static (bool ok, string output) RenewIp()
        => RunCmd("ipconfig", "/renew");

    public static (bool ok, string output) FlushArpCache()
        => RunCmd("arp", "-d *");

    public static (bool ok, string output) ResetFirewallDefaults()
        => RunNetshResult("advfirewall reset");

    public static void DeleteAllWinNetControlRules()
        => RunNetsh("advfirewall firewall delete rule name=all");

    // ─────────────────────────────────────────────────────────────────────────
    // Open system dialogs
    // ─────────────────────────────────────────────────────────────────────────

    public static void OpenWindowsFirewall()
        => ShellStart("wf.msc");

    public static void OpenNetworkConnections()
        => ShellStart("ncpa.cpl");

    public static void OpenNetworkSettings()
        => ShellStart("ms-settings:network");

    public static void OpenNetworkTroubleshooter()
        => ShellStart("msdt.exe", "/id NetworkDiagnosticsNetworkAdapter");

    // ─────────────────────────────────────────────────────────────────────────
    // Startup task (Registry Run key)
    // ─────────────────────────────────────────────────────────────────────────

    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "WinNetControl";

    public static string GetCurrentExePath()
        => Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public static void CreateStartupTask(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(StartupKeyPath, writable: true);
            key?.SetValue(StartupValueName, $"\"{exePath}\"");
        }
        catch { }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(StartupKeyPath, writable: false);
            return key?.GetValue(StartupValueName) != null;
        }
        catch { return false; }
    }

    public static void RemoveStartupTask()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(StartupKeyPath, writable: true);
            key?.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DNS resolve (async, used by HTTP Inspector firewall block)
    // ─────────────────────────────────────────────────────────────────────────

    public static async Task<string> ResolveHostAsync(string hostname)
    {
        try
        {
            if (IPAddress.TryParse(hostname, out _)) return hostname;
            var addresses = await Dns.GetHostAddressesAsync(hostname);
            return addresses.Length > 0 ? addresses[0].ToString() : string.Empty;
        }
        catch { return string.Empty; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule name helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string InboundRuleName(string appName)  => $"WinNetControl_Block_{appName}_In";
    private static string OutboundRuleName(string appName) => $"WinNetControl_Block_{appName}_Out";

    private static void DeleteRule(string ruleName)
        => RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");

    // ─────────────────────────────────────────────────────────────────────────
    // Internal runners
    // ─────────────────────────────────────────────────────────────────────────

    private static void RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8_000);
        }
        catch { }
    }

    private static (bool ok, string error) RunNetshResult(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            bool ok = p.ExitCode == 0;
            return (ok, ok ? stdout.Trim() : stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool ok, string output) RunCmd(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            string out_ = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode == 0, out_.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static void ShellStart(string path, string args = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(path, args)
                { UseShellExecute = true });
        }
        catch { }
    }

    // ── Rule Export / Import (#4 / #37) ──────────────────────────────────────

    /// <summary>
    /// Exports current netsh advfirewall rules whose name starts with the
    /// WinNetControl prefix to a JSON file on the Desktop.
    /// Returns the path written, or null on error.
    /// </summary>
    public static string? ExportRules(System.Collections.Generic.IEnumerable<WinNetControl.Models.ProcessNetworkInfo> configs)
    {
        try
        {
            // Collect all blocked entries
            var entries = configs
                .Where(c => c.IsBlocked && !string.IsNullOrEmpty(c.ProcessPath))
                .Select(c => new RuleExportEntry
                {
                    Name         = c.ProcessName,
                    Path         = c.ProcessPath,
                    BlockInbound = c.BlockInbound,
                    BlockOutbound= c.BlockOutbound,
                    DataLimitMb  = c.DataLimitMb,
                    Notes        = c.Notes
                }).ToList();

            string json   = System.Text.Json.JsonSerializer.Serialize(entries,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path    = System.IO.Path.Combine(desktop,
                $"WinNetControl_Rules_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            System.IO.File.WriteAllText(path, json);
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// Imports rules from a JSON file. Returns count of rules applied, or -1 on error.
    /// </summary>
    public static int ImportRules(string filePath,
        System.Action<WinNetControl.Models.ProcessNetworkInfo> onEachRule)
    {
        try
        {
            string json    = System.IO.File.ReadAllText(filePath);
            var    entries = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<RuleExportEntry>>(json);
            if (entries == null) return 0;

            int count = 0;
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.Path)) continue;
                BlockApp(e.Name, e.Path, e.BlockInbound, e.BlockOutbound);
                onEachRule(new WinNetControl.Models.ProcessNetworkInfo
                {
                    ProcessName   = e.Name,
                    ProcessPath   = e.Path,
                    IsBlocked     = true,
                    BlockInbound  = e.BlockInbound,
                    BlockOutbound = e.BlockOutbound,
                    DataLimitMb   = e.DataLimitMb,
                    Notes         = e.Notes
                });
                count++;
            }
            return count;
        }
        catch { return -1; }
    }

    private sealed class RuleExportEntry
    {
        public string Name          { get; set; } = "";
        public string Path          { get; set; } = "";
        public bool   BlockInbound  { get; set; } = true;
        public bool   BlockOutbound { get; set; } = true;
        public double DataLimitMb   { get; set; }
        public string Notes         { get; set; } = "";
    }
}

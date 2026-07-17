using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WinNetControl.Core;

/// <summary>Windows TCP/IP stack and internet optimization commands.</summary>
public static class NetworkOptimizeService
{
    // ── TCP Optimizations ─────────────────────────────────────────────────────
    public static (bool ok, string output) SetTcpAutoTuning(string level = "normal")
        => RunNetsh($"int tcp set global autotuninglevel={level}");
    // levels: disabled, highlyrestricted, restricted, normal, experimental

    public static (bool ok, string output) SetCongestionProvider(string provider = "ctcp")
        => RunNetsh($"int tcp set supplemental template=internet congestionprovider={provider}");
    // ctcp (Compound TCP), cubic, DCTCP

    public static (bool ok, string output) EnableRss()
        => RunNetsh("int tcp set global rss=enabled");

    public static (bool ok, string output) DisableRss()
        => RunNetsh("int tcp set global rss=disabled");

    public static (bool ok, string output) EnableChimneyOffload()
        => RunNetsh("int tcp set global chimney=enabled");

    public static (bool ok, string output) DisableChimneyOffload()
        => RunNetsh("int tcp set global chimney=disabled");

    public static (bool ok, string output) SetInitialRto(int ms = 2000)
        => RunNetsh($"int tcp set global initialrto={ms}");

    public static (bool ok, string output) SetMaxSynRetransmissions(int count = 2)
        => RunNetsh($"int tcp set global maxsynretransmissions={count}");

    public static (bool ok, string output) EnableTimestamps()
        => RunNetsh("int tcp set global timestamps=enabled");

    public static (bool ok, string output) DisableTimestamps()
        => RunNetsh("int tcp set global timestamps=disabled");

    public static (bool ok, string output) EnableEcn()
        => RunNetsh("int tcp set global ecncapability=enabled");

    public static (bool ok, string output) DisableEcn()
        => RunNetsh("int tcp set global ecncapability=disabled");

    // ── Query Current TCP state ────────────────────────────────────────────────
    public static (bool ok, string output) QueryTcpGlobal()
        => RunNetsh("int tcp show global");

    public static (bool ok, string output) QueryUdpGlobal()
        => RunNetsh("int udp show global");

    public static (bool ok, string output) QueryIpGlobal()
        => RunNetsh("int ip show global");

    // ── DNS ───────────────────────────────────────────────────────────────────
    public static (bool ok, string output) FlushDns()
        => RunCmd("ipconfig /flushdns");

    public static (bool ok, string output) SetDnsServers(string adapter, string primary, string secondary)
    {
        var (ok1, o1) = RunNetsh($"int ip set dns name=\"{adapter}\" static {primary}");
        var (ok2, o2) = RunNetsh($"int ip add dns name=\"{adapter}\" {secondary} index=2");
        return (ok1 && ok2, o1 + "\n" + o2);
    }

    public static (bool ok, string output) SetDhcpDns(string adapter)
        => RunNetsh($"int ip set dns name=\"{adapter}\" dhcp");

    // ── Network Reset (full stack) ────────────────────────────────────────────
    public static (bool ok, string output) ResetAll()
    {
        var sb = new StringBuilder();
        bool ok = true;
        foreach (var cmd in new[]
        {
            ("netsh winsock reset",     true),
            ("netsh int ip reset",      true),
            ("netsh int tcp reset",     true),
            ("ipconfig /release",       false),
            ("ipconfig /flushdns",      false),
            ("ipconfig /renew",         false),
        })
        {
            var (r, o) = RunCmd(cmd.Item1);
            if (cmd.Item2) ok &= r;
            sb.AppendLine($"[{cmd.Item1}]: {(r ? "OK" : "FAIL")}");
            if (!string.IsNullOrWhiteSpace(o)) sb.AppendLine(o.Trim());
        }
        return (ok, sb.ToString());
    }

    // ── MTU ───────────────────────────────────────────────────────────────────
    public static (bool ok, string output) ShowMtu()
        => RunNetsh("int ipv4 show subinterfaces");

    public static (bool ok, string output) SetMtu(string ifAlias, int mtu)
        => RunNetsh($"int ipv4 set subinterface \"{ifAlias}\" mtu={mtu} store=persistent");

    // ── Firewall extras ───────────────────────────────────────────────────────
    public static (bool ok, string output) EnableKillSwitch()
    {
        // Block ALL outbound on all profiles — emergency internet off
        var (ok1, o1) = RunNetsh("advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound");
        return (ok1, "⚠ Kill switch ON — all internet blocked!\n" + o1);
    }

    public static (bool ok, string output) DisableKillSwitch()
    {
        var (ok1, o1) = RunNetsh("advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound");
        return (ok1, "Kill switch OFF — internet restored.\n" + o1);
    }

    public static (bool ok, string output) BlockPort(int port, string protocol = "any", string direction = "out")
        => RunNetsh($"advfirewall firewall add rule name=\"WinNetControl_Port_{port}_{direction}\" " +
                    $"dir={direction} action=block protocol={protocol} localport={port}");

    public static (bool ok, string output) UnblockPort(int port, string direction = "out")
        => RunNetsh($"advfirewall firewall delete rule name=\"WinNetControl_Port_{port}_{direction}\"");

    public static (bool ok, string output) ListWinNetControlRules()
        => RunCmd("netsh advfirewall firewall show rule name=all | findstr /C:\"WinNetControl\"");

    public static List<string> GetWinNetControlRuleNames()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"Get-NetFirewallRule | " +
                "Where-Object { $_.DisplayName -like 'WinNetControl*' } | " +
                "Select-Object DisplayName,Direction,Action,Enabled | ConvertTo-Json -Compress\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi)!;
            string json = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            // Simple parse — extract DisplayName values
            var names = new List<string>();
            foreach (var segment in json.Split("\"DisplayName\":", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                int s = segment.IndexOf('"');
                int e = segment.IndexOf('"', s + 1);
                if (s >= 0 && e > s) names.Add(segment[(s + 1)..e]);
            }
            return names;
        }
        catch { return new List<string>(); }
    }

    public static (bool ok, string output) DeleteAllWinNetControlRules()
    {
        var psi = new ProcessStartInfo("powershell",
            "-NoProfile -Command \"Get-NetFirewallRule | " +
            "Where-Object { $_.DisplayName -like 'WinNetControl*' } | Remove-NetFirewallRule\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        };
        try
        {
            using var proc = Process.Start(psi)!;
            string out_ = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode == 0, "All WinNetControl rules removed.\n" + out_);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static (bool ok, string output) RunNetsh(string args)
        => RunCmd($"netsh {args}");

    private static (bool ok, string output) RunCmd(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode == 0, (stdout + stderr).Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

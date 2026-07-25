using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinNetControl.Core;

/// <summary>Represents a single line in the Windows Hosts file.</summary>
public class HostsEntry
{
    public string   Ip        { get; set; } = string.Empty;
    public string   Hostname  { get; set; } = string.Empty;
    public string   Comment   { get; set; } = string.Empty;  // inline comment after #
    public bool     IsEnabled { get; set; } = true;          // false = line is commented out
    public bool     IsComment { get; set; } = false;         // pure comment-only line
    public int      LineIndex { get; set; }                   // original line number
    /// <summary>App that triggered this block, shown as '| via AppName' in the hosts comment.</summary>
    public string   SourceApp { get; set; } = string.Empty;

    public string DisplayIp       => IsEnabled ? Ip       : "(disabled)";
    public string DisplayHostname => IsEnabled ? Hostname : Hostname;
    /// <summary>Shows SourceApp if available, otherwise plain Comment.</summary>
    public string DisplaySource   => !string.IsNullOrEmpty(SourceApp) ? SourceApp : Comment;

    public string FullLine
    {
        get
        {
            if (IsComment) return string.IsNullOrWhiteSpace(Ip) ? $"# {Hostname}" : $"# {Ip} {Hostname}";
            // Build comment: embed SourceApp if present so we can recover it on re-read
            string cmt = string.IsNullOrWhiteSpace(SourceApp)
                ? Comment
                : (string.IsNullOrWhiteSpace(Comment)
                    ? $"WinNetControl | via {SourceApp}"
                    : $"{Comment} | via {SourceApp}");
            string line = $"{Ip}\t{Hostname}";
            if (!string.IsNullOrWhiteSpace(cmt)) line += $" # {cmt}";
            return IsEnabled ? line : $"# {line}";
        }
    }
}

public static class HostsFileService
{
    public static readonly string HostsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                     @"drivers\etc\hosts");

    private const string DefaultHostsContent =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n" +
        "#\r\n" +
        "# This file contains the mappings of IP addresses to host names. Each\r\n" +
        "# entry should be kept on an individual line. The IP address should\r\n" +
        "# be placed in the first column followed by the corresponding host name.\r\n" +
        "# The IP address and the host name should be separated by at least one\r\n" +
        "# space.\r\n" +
        "#\r\n" +
        "# Additionally, comments (such as these) may be inserted on individual\r\n" +
        "# lines or following the machine name denoted by a '#' symbol.\r\n" +
        "#\r\n" +
        "# For example:\r\n" +
        "#\r\n" +
        "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
        "#       38.25.63.10     x.acme.com              # x client host\r\n" +
        "\r\n" +
        "# localhost name resolution is handled within DNS itself.\r\n" +
        "#\t127.0.0.1       localhost\r\n" +
        "#\t::1             localhost\r\n";

    // ── Read ──────────────────────────────────────────────────────────────────
    public static List<HostsEntry> ReadEntries()
    {
        var entries = new List<HostsEntry>();
        if (!File.Exists(HostsPath)) return entries;

        string[] lines;
        try { lines = File.ReadAllLines(HostsPath, Encoding.UTF8); }
        catch { lines = Array.Empty<string>(); }

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            entries.Add(ParseLine(raw, i));
        }
        return entries;
    }

    private static HostsEntry ParseLine(string raw, int index)
    {
        var entry = new HostsEntry { LineIndex = index };
        string line = raw.TrimStart();

        if (string.IsNullOrWhiteSpace(raw))
        {
            entry.IsComment = true;
            return entry;
        }

        // Detect disabled entry (commented-out ip hostname)
        if (line.StartsWith("#"))
        {
            string rest = line.TrimStart('#').Trim();
            // Try to parse as a disabled ip-hostname pair
            var parts = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IsValidIp(parts[0]))
            {
                entry.IsEnabled  = false;
                entry.IsComment  = false;
                entry.Ip         = parts[0];
                entry.Hostname   = parts[1];
                string rawCmt    = parts.Length > 2 ? string.Join(" ", parts.Skip(2)).TrimStart('#').Trim() : string.Empty;
                ExtractSourceApp(entry, rawCmt);
            }
            else
            {
                // Pure comment
                entry.IsComment  = true;
                entry.Hostname   = rest;
            }
            return entry;
        }

        // Active entry — strip inline comment
        string inlineComment = string.Empty;
        int hashIdx = line.IndexOf('#');
        if (hashIdx >= 0)
        {
            inlineComment = line[(hashIdx + 1)..].Trim();
            line = line[..hashIdx].Trim();
        }

        var cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (cols.Length >= 2)
        {
            entry.Ip        = cols[0];
            entry.Hostname  = cols[1];
            entry.IsEnabled = true;
            ExtractSourceApp(entry, inlineComment);
        }
        else
        {
            entry.IsComment = true;
            entry.Hostname  = raw;
        }
        return entry;
    }

    private static bool IsValidIp(string s)
        => System.Net.IPAddress.TryParse(s, out _);

    /// <summary>Splits 'WinNetControl | via chrome.exe' into Comment + SourceApp.</summary>
    private static void ExtractSourceApp(HostsEntry entry, string rawComment)
    {
        if (string.IsNullOrWhiteSpace(rawComment)) { entry.Comment = string.Empty; return; }
        const string tag = "| via ";
        int idx = rawComment.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            entry.SourceApp = rawComment[(idx + tag.Length)..].Trim();
            entry.Comment   = rawComment[..idx].Trim().TrimEnd('|').Trim();
        }
        else
        {
            entry.Comment   = rawComment;
            entry.SourceApp = string.Empty;
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────
    public static (bool ok, string error) WriteEntries(List<HostsEntry> entries)
    {
        try
        {
            var lines = entries.Select(e => e.FullLine);
            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            FlushDnsCache();
            return (true, string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied. Run the app as Administrator to edit the Hosts file.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Convenience operations ────────────────────────────────────────────────
    public static (bool ok, string error) AddEntry(string ip, string hostname, string comment = "")
    {
        var entries = ReadEntries();
        // Check for duplicate
        if (entries.Any(e => !e.IsComment && e.IsEnabled &&
                        string.Equals(e.Hostname, hostname, StringComparison.OrdinalIgnoreCase) &&
                        e.Ip == ip))
            return (false, "Entry already exists.");

        entries.Add(new HostsEntry
        {
            Ip        = ip,
            Hostname  = hostname,
            Comment   = comment,
            IsEnabled = true,
            LineIndex = entries.Count
        });
        return WriteEntries(entries);
    }

    /// <summary>Quick-block a hostname by mapping it to 0.0.0.0, optionally tagging the source app.</summary>
    public static (bool ok, string error) BlockDomain(string hostname, string appName = "")
    {
        var entries = ReadEntries();
        hostname = hostname.Trim().ToLowerInvariant();
        if (entries.Any(e => !e.IsComment && e.IsEnabled
                          && string.Equals(e.Hostname, hostname, StringComparison.OrdinalIgnoreCase)
                          && e.Ip == "0.0.0.0"))
            return (true, "already blocked");
        entries.Add(new HostsEntry
        {
            Ip        = "0.0.0.0",
            Hostname  = hostname,
            Comment   = "WinNetControl",
            SourceApp = appName,
            IsEnabled = true,
            LineIndex = entries.Count
        });
        return WriteEntries(entries);
    }

    public static (bool ok, string error) RemoveEntry(int lineIndex)
    {
        var entries = ReadEntries();
        var target  = entries.FirstOrDefault(e => e.LineIndex == lineIndex);
        if (target == null) return (false, "Entry not found.");
        entries.Remove(target);
        for (int i = 0; i < entries.Count; i++) entries[i].LineIndex = i;
        return WriteEntries(entries);
    }

    public static (bool ok, string error) RemoveEntries(IEnumerable<int> lineIndexes)
    {
        var entries = ReadEntries();
        var toRemove = new HashSet<int>(lineIndexes);
        entries.RemoveAll(e => toRemove.Contains(e.LineIndex));
        for (int i = 0; i < entries.Count; i++) entries[i].LineIndex = i;
        return WriteEntries(entries);
    }

    public static (bool ok, string error) ToggleEntry(int lineIndex, bool enable)
    {
        var entries = ReadEntries();
        var target  = entries.FirstOrDefault(e => e.LineIndex == lineIndex);
        if (target == null) return (false, "Entry not found.");
        target.IsEnabled = enable;
        return WriteEntries(entries);
    }

    public static (bool ok, string error) ResetToDefault()
    {
        try
        {
            File.WriteAllText(HostsPath, DefaultHostsContent, new UTF8Encoding(false));
            FlushDnsCache();
            return (true, string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied. Run the app as Administrator.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void OpenInNotepad()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "notepad.exe",
                Arguments       = $"\"{HostsPath}\"",
                UseShellExecute = true
            };
            // Only add runas if not already elevated — avoids a second UAC prompt
            if (!ElevatedRunner.IsAdmin)
                psi.Verb = "runas";
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private static void FlushDnsCache()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
            { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
        }
        catch { }
    }

}

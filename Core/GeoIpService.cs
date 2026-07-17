using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinNetControl.Core;

/// <summary>
/// Lightweight async geo-IP resolver using the free ip-api.com API (no key required).
/// Results are cached per-IP for the lifetime of the app session.
/// Private / reserved IPs are resolved immediately without a network call.
/// </summary>
public static class GeoIpService
{
    // ── Cache ─────────────────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // ── Private/Reserved IP prefixes — no lookup needed ───────────────────────
    private static readonly string[] _privateRanges =
    {
        "0.", "10.", "100.64.", "127.", "169.254.", "172.16.", "172.17.",
        "172.18.", "172.19.", "172.2", "172.30.", "172.31.", "192.0.",
        "192.168.", "198.51.", "203.0.", "224.", "240.", "255.", "::"
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns "🇺🇸 US" style string for the given IP, or empty string for local/private IPs.
    /// Result is cached; safe to call frequently — will NOT re-query if already resolved.
    /// </summary>
    public static string GetCountryLabel(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0" || ip == "::")
            return "";

        if (_cache.TryGetValue(ip, out string? cached))
            return cached;

        // Check private/reserved ranges — return immediately
        if (IsPrivate(ip))
        {
            _cache[ip] = "";
            return "";
        }

        // Return empty for now; kick off background fetch
        _cache[ip] = "";   // placeholder prevents duplicate requests
        _ = FetchAsync(ip);
        return "";
    }

    /// <summary>
    /// Async version — awaitable; returns populated label after network call.
    /// </summary>
    public static async Task<string> GetCountryLabelAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0" || ip == "::")
            return "";
        if (IsPrivate(ip)) return "";

        if (_cache.TryGetValue(ip, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        return await FetchAsync(ip);
    }

    // ── Internals ─────────────────────────────────────────────────────────────
    private static bool IsPrivate(string ip)
    {
        foreach (var prefix in _privateRanges)
            if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<string> FetchAsync(string ip)
    {
        try
        {
            // ip-api.com free tier: 45 req/min, no HTTPS on free tier
            string url = $"http://ip-api.com/json/{ip}?fields=status,countryCode";
            string json = await _http.GetStringAsync(url).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("status", out var status)
                && status.GetString() == "success"
                && doc.RootElement.TryGetProperty("countryCode", out var cc))
            {
                string code  = cc.GetString() ?? "";
                string flag  = CountryCodeToFlag(code);
                string label = $"{flag} {code}";
                _cache[ip] = label;
                return label;
            }
        }
        catch { /* swallow — geo-IP is best-effort */ }

        _cache[ip] = "";
        return "";
    }

    /// <summary>Converts a 2-letter ISO country code to a flag emoji.</summary>
    private static string CountryCodeToFlag(string code)
    {
        if (code.Length != 2) return "";
        // Regional indicator letters: 🇦 = U+1F1E6, offset from 'A'
        int a = code[0] - 'A' + 0x1F1E6;
        int b = code[1] - 'A' + 0x1F1E6;
        return char.ConvertFromUtf32(a) + char.ConvertFromUtf32(b);
    }

    /// <summary>Clear cache (e.g. after network change).</summary>
    public static void ClearCache() => _cache.Clear();
}

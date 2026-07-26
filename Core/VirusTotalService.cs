using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetControl.Core;

// ── VirusTotal scan status ────────────────────────────────────────────────────
public enum VtStatus { Unknown, Checking, Clean, Suspicious, Malicious, NotFound, Error }

public class VtResult
{
    public VtStatus Status    { get; init; } = VtStatus.Unknown;
    public int  Malicious     { get; init; }
    public int  Suspicious    { get; init; }
    public int  Total         { get; init; }
    public string Message     { get; init; } = string.Empty;  // e.g. "0/72"
}

// ── Serialisable cache entry (includes timestamp so entries can be expired) ───
internal class VtCacheEntry
{
    [JsonPropertyName("s")]  public int     StatusInt  { get; set; }   // (int)VtStatus
    [JsonPropertyName("m")]  public int     Malicious  { get; set; }
    [JsonPropertyName("su")] public int     Suspicious { get; set; }
    [JsonPropertyName("t")]  public int     Total      { get; set; }
    [JsonPropertyName("msg")] public string Message   { get; set; } = string.Empty;
    [JsonPropertyName("at")] public long    ScannedAtUtc { get; set; } // DateTime.UtcNow.Ticks

    public VtStatus  Status => (VtStatus)StatusInt;
    public DateTime  ScannedAt => new DateTime(ScannedAtUtc, DateTimeKind.Utc);

    public VtResult ToResult() => new()
    {
        Status     = Status,
        Malicious  = Malicious,
        Suspicious = Suspicious,
        Total      = Total,
        Message    = Message
    };

    public static VtCacheEntry FromResult(VtResult r) => new()
    {
        StatusInt    = (int)r.Status,
        Malicious    = r.Malicious,
        Suspicious   = r.Suspicious,
        Total        = r.Total,
        Message      = r.Message,
        ScannedAtUtc = DateTime.UtcNow.Ticks
    };
}

// ── Service ───────────────────────────────────────────────────────────────────
public sealed class VirusTotalService : IDisposable
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Cache expires after 30 days (free tier quota matters)
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    // ── Disk cache paths ──────────────────────────────────────────────────────
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinNetControl");
    private static readonly string CacheFile = Path.Combine(CacheDir, "vt_cache.json");

    // hash → entry   (SHA-256 of file content)
    private Dictionary<string, VtCacheEntry> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    // filePath → hash  (so we can look up without re-hashing)
    private Dictionary<string, string>       _pathToHash = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    // Rate limiter: free VT API = 4 lookups / minute
    private readonly SemaphoreSlim _gate          = new(1, 1);
    private DateTime               _windowStart   = DateTime.UtcNow;
    private int                    _reqThisWindow;
    private const int              MaxPerMinute   = 4;

    public VirusTotalService() => LoadCache();

    // ── Public: instant cached result by file path (no API call) ─────────────
    public VtResult? TryGetCachedByPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        lock (_lock)
        {
            if (!_pathToHash.TryGetValue(filePath, out string? hash)) return null;
            if (!_hashCache.TryGetValue(hash, out var entry)) return null;
            if (DateTime.UtcNow - entry.ScannedAt > CacheMaxAge) return null; // expired
            return entry.ToResult();
        }
    }

    // ── Public: full scan (cache-first, then API) ─────────────────────────────
    public async Task<VtResult> CheckFileAsync(string filePath, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new VtResult { Status = VtStatus.Error, Message = "No API key" };

        if (!File.Exists(filePath))
            return new VtResult { Status = VtStatus.NotFound, Message = "File not found" };

        // 1. Try in-memory + disk cache
        string hash;
        try   { hash = ComputeSha256(filePath); }
        catch { return new VtResult { Status = VtStatus.Error, Message = "Cannot read file" }; }

        lock (_lock)
        {
            // Record path→hash mapping even before scan
            _pathToHash[filePath] = hash;

            if (_hashCache.TryGetValue(hash, out var cached) &&
                DateTime.UtcNow - cached.ScannedAt <= CacheMaxAge)
                return cached.ToResult();
        }

        // 2. API call with rate limiting
        await _gate.WaitAsync();
        try
        {
            // Double-check after acquiring gate
            lock (_lock)
            {
                if (_hashCache.TryGetValue(hash, out var cached) &&
                    DateTime.UtcNow - cached.ScannedAt <= CacheMaxAge)
                    return cached.ToResult();
            }

            // Enforce 4 req/min window
            var now = DateTime.UtcNow;
            if ((now - _windowStart).TotalSeconds >= 60) { _windowStart = now; _reqThisWindow = 0; }
            if (_reqThisWindow >= MaxPerMinute)
            {
                double wait = 60 - (now - _windowStart).TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait + 1));
                _windowStart    = DateTime.UtcNow;
                _reqThisWindow  = 0;
            }
            _reqThisWindow++;

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.virustotal.com/api/v3/files/{hash}");
            req.Headers.Add("x-apikey", apiKey);
            req.Headers.Add("Accept", "application/json");

            HttpResponseMessage resp;
            try   { resp = await _http.SendAsync(req); }
            catch (Exception ex)
            { return new VtResult { Status = VtStatus.Error, Message = $"Network: {ex.Message}" }; }

            string json = await resp.Content.ReadAsStringAsync();
            var result  = ParseResponse(resp.StatusCode, json);

            // Cache result (even errors like NotFound — to avoid re-hitting quota)
            if (result.Status != VtStatus.Error)
            {
                lock (_lock)
                {
                    _hashCache[hash]       = VtCacheEntry.FromResult(result);
                    _pathToHash[filePath]  = hash;
                }
                SaveCache(); // fire-and-forget write
            }

            return result;
        }
        finally { _gate.Release(); }
    }

    // ── Parsing ───────────────────────────────────────────────────────────────
    private static VtResult ParseResponse(System.Net.HttpStatusCode code, string json)
    {
        if (code == System.Net.HttpStatusCode.NotFound)
            return new VtResult { Status = VtStatus.NotFound, Message = "Not in VT database" };
        if (code == System.Net.HttpStatusCode.Forbidden)
            return new VtResult { Status = VtStatus.Error,    Message = "Invalid API key" };
        if ((int)code == 429)
            return new VtResult { Status = VtStatus.Error,    Message = "Rate limit exceeded" };
        if (code != System.Net.HttpStatusCode.OK)
            return new VtResult { Status = VtStatus.Error,    Message = $"HTTP {(int)code}" };

        try
        {
            using var doc  = JsonDocument.Parse(json);
            var attrs      = doc.RootElement.GetProperty("data").GetProperty("attributes");
            var stats      = attrs.GetProperty("last_analysis_stats");

            int malicious  = GetInt(stats, "malicious");
            int suspicious = GetInt(stats, "suspicious");
            int total      = 0;
            foreach (var p in stats.EnumerateObject()) total += p.Value.GetInt32();

            var status = malicious > 5  ? VtStatus.Malicious  :
                         malicious > 0 || suspicious > 2 ? VtStatus.Suspicious :
                         VtStatus.Clean;

            return new VtResult
            {
                Status     = status,
                Malicious  = malicious,
                Suspicious = suspicious,
                Total      = total,
                Message    = $"{malicious}/{total}"
            };
        }
        catch (Exception ex)
        {
            return new VtResult { Status = VtStatus.Error, Message = $"Parse error: {ex.Message}" };
        }
    }

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetInt32() : 0;

    // ── SHA-256 ───────────────────────────────────────────────────────────────
    private static string ComputeSha256(string filePath)
    {
        using var sha    = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    // ── Disk persistence ──────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            string json = File.ReadAllText(CacheFile);
            var data = JsonSerializer.Deserialize<VtDiskCache>(json, _jsonOpts);
            if (data == null) return;
            lock (_lock)
            {
                _hashCache  = data.HashCache  ?? new(StringComparer.OrdinalIgnoreCase);
                _pathToHash = data.PathToHash ?? new(StringComparer.OrdinalIgnoreCase);

                // Purge expired entries on load
                var expiredHashes = new List<string>();
                foreach (var kvp in _hashCache)
                    if (DateTime.UtcNow - kvp.Value.ScannedAt > CacheMaxAge)
                        expiredHashes.Add(kvp.Key);
                foreach (var h in expiredHashes) _hashCache.Remove(h);
            }
        }
        catch { /* corrupt cache — ignore */ }
    }

    private void SaveCache()
    {
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                VtDiskCache snapshot;
                lock (_lock)
                    snapshot = new VtDiskCache
                    {
                        HashCache  = new Dictionary<string, VtCacheEntry>(_hashCache,  StringComparer.OrdinalIgnoreCase),
                        PathToHash = new Dictionary<string, string>(_pathToHash, StringComparer.OrdinalIgnoreCase)
                    };
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(snapshot, _jsonOpts));
            }
            catch { }
        });
    }

    public void Dispose() => _gate.Dispose();
}

// ── JSON root wrapper ─────────────────────────────────────────────────────────
internal class VtDiskCache
{
    [JsonPropertyName("h")] public Dictionary<string, VtCacheEntry>? HashCache  { get; set; }
    [JsonPropertyName("p")] public Dictionary<string, string>?        PathToHash { get; set; }
}

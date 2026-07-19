using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WinNetControl.Core;

/// <summary>
/// Cross-module sync bus. Fires events when a connection is blocked or unblocked
/// so that Socket Manager, Firewall, and Connection Manager stay in sync.
/// </summary>
public static class BlockedConnectionStore
{
    // ── Event ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Fired when a connection block state changes.
    /// Args: (processName, remoteIp, remotePort, localPort, isBlocked)
    /// </summary>
    public static event Action<string, string, int, int, bool>? ConnectionBlockChanged;

    // ── In-memory store ───────────────────────────────────────────────────────
    // Key: "processName|remoteIp|remotePort|localPort"
    private static readonly ConcurrentDictionary<string, bool> _store = new();

    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this after applying a firewall block/unblock to notify all subscribers.
    /// </summary>
    public static void NotifyBlockChange(
        string processName, string remoteIp, int remotePort, int localPort, bool isBlocked)
    {
        string key = MakeKey(processName, remoteIp, remotePort, localPort);
        if (isBlocked)
            _store[key] = true;
        else
            _store.TryRemove(key, out _);

        // Fire on whatever thread the caller is on — subscribers should marshal to UI if needed
        ConnectionBlockChanged?.Invoke(processName, remoteIp, remotePort, localPort, isBlocked);
    }

    /// <summary>Returns true if the given connection is currently tracked as blocked.</summary>
    public static bool IsBlocked(string processName, string remoteIp, int remotePort, int localPort)
        => _store.ContainsKey(MakeKey(processName, remoteIp, remotePort, localPort));

    /// <summary>Returns a snapshot of all currently blocked connections.</summary>
    public static IReadOnlyDictionary<string, bool> Snapshot() => _store;

    private static string MakeKey(string processName, string remoteIp, int remotePort, int localPort)
        => $"{processName}|{remoteIp}|{remotePort}|{localPort}";
}

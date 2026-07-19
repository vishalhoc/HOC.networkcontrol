using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinNetControl.Models;

namespace WinNetControl.Core;

public class NetworkMonitorService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, uint processInformationLength, out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private static readonly Dictionary<string, int> _launchCounts = new();

    private string GetParentProcessName(int pid)
    {
        if (pid <= 4) return "System";
        IntPtr hProcess = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (hProcess == IntPtr.Zero) return "Unknown";
        
        PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
        uint returnLength;
        int status = NtQueryInformationProcess(hProcess, 0, ref pbi, (uint)Marshal.SizeOf(pbi), out returnLength);
        CloseHandle(hProcess);
        
        if (status != 0) return "Unknown";
        
        int parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
        if (parentPid <= 0) return "Unknown";
        
        try
        {
            return Process.GetProcessById(parentPid).ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    private string GetProcessPath(int pid)
    {
        if (pid <= 4) return "System";
        IntPtr hProcess = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (hProcess == IntPtr.Zero) return string.Empty;
        
        uint capacity = 1024;
        System.Text.StringBuilder sb = new System.Text.StringBuilder((int)capacity);
        if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
        {
            CloseHandle(hProcess);
            return sb.ToString();
        }
        CloseHandle(hProcess);
        return string.Empty;
    }

    // Mapping PID to ProcessNetworkInfo
    private readonly Dictionary<int, ProcessNetworkInfo> _processMap = new();

    // Grace window: track how many consecutive polls a PID has been missing
    private readonly Dictionary<int, int> _missCounts = new();
    private const int GracePollCount = 3; // remove after 3 consecutive misses

    private bool _isRunning;

    // Set of process names that should be kept as phantom entries (blocked apps)
    public readonly HashSet<string> KnownBlockedNames = new(StringComparer.OrdinalIgnoreCase);
    
    public event Action<IEnumerable<ProcessNetworkInfo>>? OnConnectionsUpdated;

    public void StartMonitoring()
    {
        if (_isRunning) return;
        _isRunning = true;
        
        Task.Run(async () =>
        {
            while (_isRunning)
            {
                UpdateConnections();
                await Task.Delay(2000);
            }
        });
    }

    public void StopMonitoring()
    {
        _isRunning = false;
    }

    private void UpdateConnections()
    {
        var connections = new List<ProcessConnection>();
        List<ProcessNetworkInfo> snapshot;
        try
        {
            var tcp = GetTcpConnections();
            var udp = GetUdpConnections();
            connections.AddRange(tcp);
            connections.AddRange(udp);
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText("network_error.log", $"[TCP/UDP] {DateTime.Now}: {ex.Message}\n{ex.StackTrace}\n"); } catch {}
            return;
        }
        
        // Group by PID
        var grouped = connections.GroupBy(c => c.ProcessId);
        var activePids = new HashSet<int>(grouped.Select(g => g.Key));
        
        lock (_processMap)
        {
            // Update existing or add new
            foreach (var group in grouped)
            {
                int pid = group.Key;
                if (pid <= 0) continue;

                // Reset miss count since this PID is active
                _missCounts.Remove(pid);
                
                if (!_processMap.TryGetValue(pid, out var info))
                {
                    string processName = GetProcessName(pid);
                    string processPath = GetProcessPath(pid);
                    
                    _launchCounts.TryGetValue(processPath ?? processName, out int count);
                    count++;
                    _launchCounts[processPath ?? processName] = count;

                    info = new ProcessNetworkInfo
                    {
                        ProcessId = pid,
                        ProcessName = processName,
                        ProcessPath = processPath ?? string.Empty,
                        AppType = ClassifyApp(pid),
                        ParentProcessName = GetParentProcessName(pid),
                        LaunchCount = count
                    };
                    _processMap[pid] = info;
                    
                    if (count == 1)
                    {
                        HistoryLogService.AddLog("Process Start", processName, $"PID: {pid}, Path: {processPath}");
                    }
                }
                
                info.CurrentConnections.Clear();
                foreach (var conn in group)
                {
                    info.CurrentConnections.Add(conn);
                }

                // Resolve adapter from first local IP
                if (string.IsNullOrEmpty(info.AdapterName) && info.CurrentConnections.Count > 0)
                {
                    string localIp = info.CurrentConnections[0].LocalAddress;
                    info.AdapterName = GetAdapterNameForLocalIp(localIp);
                }
            }
            
            // Grace-window removal: only remove a PID after GracePollCount consecutive misses
            var pidsToRemove = new List<int>();
            foreach (var kvp in _processMap)
            {
                int pid = kvp.Key;
                if (!activePids.Contains(pid))
                {
                    // Don't remove if it's a known blocked app
                    if (KnownBlockedNames.Contains(kvp.Value.ProcessName))
                        continue;

                    _missCounts.TryGetValue(pid, out int misses);
                    misses++;
                    _missCounts[pid] = misses;

                    if (misses >= GracePollCount)
                        pidsToRemove.Add(pid);
                }
            }
            foreach (var pid in pidsToRemove)
            {
                _processMap.Remove(pid);
                _missCounts.Remove(pid);
            }

            // Mark processes with no active connections as phantom
            foreach (var kvp in _processMap)
            {
                kvp.Value.IsPhantom = !activePids.Contains(kvp.Key);
            }
            
            snapshot = _processMap.Values.ToList();
        }
        
        OnConnectionsUpdated?.Invoke(snapshot);
    }

    private string GetProcessName(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>Returns the NIC friendly name whose unicast address matches the given local IP string.</summary>
    private static string GetAdapterNameForLocalIp(string localIp)
    {
        if (string.IsNullOrWhiteSpace(localIp) || localIp == "0.0.0.0" || localIp == "::") return string.Empty;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.ToString() == localIp)
                        return nic.Name;
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private string ClassifyApp(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            string name = proc.ProcessName.ToLower();
            
            if (name == "svchost" || name == "system" || name == "idle") 
                return "Windows Service";
                
            try
            {
                string path = proc.MainModule?.FileName?.ToLower() ?? "";
                if (path.Contains(@"\windowsapps\"))
                    return "Windows App";
                if (path.Contains(@"\windows\system32\") || path.Contains(@"\windows\syswow64\"))
                    return "Windows System";
            }
            catch
            {
                // Access denied or module not available
            }

            if (name.Contains("windows")) return "Windows Component";
            return "Third-Party App";
        }
        catch
        {
            return "Unknown";
        }
    }

    private IEnumerable<ProcessConnection> GetTcpConnections()
    {
        var connections = new List<ProcessConnection>();
        connections.AddRange(GetTcpConnectionsInternal(2,  (int)NativeMethods.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL));
        connections.AddRange(GetTcpConnectionsInternal(23, (int)NativeMethods.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL));
        return connections;
    }

    private IEnumerable<ProcessConnection> GetTcpConnectionsInternal(int ipVersion, int tblClass)
    {
        int bufferSize = 0;
        uint ret = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, ipVersion, tblClass, 0);

        int retries = 0;
        while ((ret == 122 || ret == 234) && retries < 5)
        {
            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = NativeMethods.GetExtendedTcpTable(tcpTablePtr, ref bufferSize, true, ipVersion, tblClass, 0);
                if (ret == 0)
                {
                    int rowCount = Marshal.ReadInt32(tcpTablePtr);
                    var connections = new List<ProcessConnection>();
                    IntPtr rowPtr = tcpTablePtr + 4; 

                    for (int i = 0; i < rowCount; i++)
                    {
                        if (ipVersion == 2) // IPv4
                        {
                            var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                            connections.Add(new ProcessConnection
                            {
                                Protocol = "TCP",
                                LocalAddress = new System.Net.IPAddress((long)row.localAddr).ToString(),
                                LocalPort = BitConverter.ToUInt16(new byte[] { row.localPort[1], row.localPort[0] }, 0),
                                RemoteAddress = new System.Net.IPAddress((long)row.remoteAddr).ToString(),
                                RemotePort = BitConverter.ToUInt16(new byte[] { row.remotePort[1], row.remotePort[0] }, 0),
                                State = row.state.ToString(),
                                ProcessId = (int)row.owningPid
                            });
                            rowPtr += Marshal.SizeOf(typeof(NativeMethods.MIB_TCPROW_OWNER_PID));
                        }
                        else // IPv6
                        {
                            var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(rowPtr);
                            connections.Add(new ProcessConnection
                            {
                                Protocol = "TCPv6",
                                LocalAddress = new System.Net.IPAddress(row.localAddr).ToString(),
                                LocalPort = BitConverter.ToUInt16(new byte[] { row.localPort[1], row.localPort[0] }, 0),
                                RemoteAddress = new System.Net.IPAddress(row.remoteAddr).ToString(),
                                RemotePort = BitConverter.ToUInt16(new byte[] { row.remotePort[1], row.remotePort[0] }, 0),
                                State = row.state.ToString(),
                                ProcessId = (int)row.owningPid
                            });
                            rowPtr += Marshal.SizeOf(typeof(NativeMethods.MIB_TCP6ROW_OWNER_PID));
                        }
                    }
                    return connections;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
            retries++;
        }
        return Enumerable.Empty<ProcessConnection>();
    }
    
    private IEnumerable<ProcessConnection> GetUdpConnections()
    {
        var connections = new List<ProcessConnection>();
        connections.AddRange(GetUdpConnectionsInternal(2,  (int)NativeMethods.UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID));
        connections.AddRange(GetUdpConnectionsInternal(23, (int)NativeMethods.UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID));
        return connections;
    }

    private IEnumerable<ProcessConnection> GetUdpConnectionsInternal(int ipVersion, int tblClass)
    {
        int bufferSize = 0;
        uint ret = NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, true, ipVersion, tblClass, 0);

        int retries = 0;
        while ((ret == 122 || ret == 234) && retries < 5)
        {
            IntPtr udpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = NativeMethods.GetExtendedUdpTable(udpTablePtr, ref bufferSize, true, ipVersion, tblClass, 0);
                if (ret == 0)
                {
                    int rowCount = Marshal.ReadInt32(udpTablePtr);
                    var connections = new List<ProcessConnection>();
                    IntPtr rowPtr = udpTablePtr + 4; 

                    for (int i = 0; i < rowCount; i++)
                    {
                        if (ipVersion == 2) // IPv4
                        {
                            var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(rowPtr);
                            connections.Add(new ProcessConnection
                            {
                                Protocol = "UDP",
                                LocalAddress = new System.Net.IPAddress((long)row.localAddr).ToString(),
                                LocalPort = BitConverter.ToUInt16(new byte[] { row.localPort[1], row.localPort[0] }, 0),
                                RemoteAddress = "*",
                                RemotePort = 0,
                                State = "N/A",
                                ProcessId = (int)row.owningPid
                            });
                            rowPtr += Marshal.SizeOf(typeof(NativeMethods.MIB_UDPROW_OWNER_PID));
                        }
                        else // IPv6
                        {
                            var row = Marshal.PtrToStructure<NativeMethods.MIB_UDP6ROW_OWNER_PID>(rowPtr);
                            connections.Add(new ProcessConnection
                            {
                                Protocol = "UDPv6",
                                LocalAddress = new System.Net.IPAddress(row.localAddr).ToString(),
                                LocalPort = BitConverter.ToUInt16(new byte[] { row.localPort[1], row.localPort[0] }, 0),
                                RemoteAddress = "*",
                                RemotePort = 0,
                                State = "N/A",
                                ProcessId = (int)row.owningPid
                            });
                            rowPtr += Marshal.SizeOf(typeof(NativeMethods.MIB_UDP6ROW_OWNER_PID));
                        }
                    }
                    return connections;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(udpTablePtr);
            }
            retries++;
        }
        return Enumerable.Empty<ProcessConnection>();
    }
}

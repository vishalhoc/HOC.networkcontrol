using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinNetControl.Core;

/// <summary>Creates scoped Windows QoS policies for a process or an individual destination.</summary>
public static class QosPolicyService
{
    public const int LowPriorityDscp = 8;
    public const int StandardPriorityDscp = 16;
    public const int HighPriorityDscp = 46;

    public static async Task<(bool Success, string Message)> SetPriorityAsync(
        string processPath,
        string processName,
        int dscp,
        string? destinationIp = null,
        int destinationPort = 0,
        int throttleKbps = 0,
        string? policyName = null,
        bool allowAllTraffic = false,
        string? protocol = null)
    {
        if (dscp is < 0 or > 63)
            return (false, "DSCP must be between 0 and 63.");

        bool hasApp  = !string.IsNullOrWhiteSpace(processPath);
        bool hasDest = !string.IsNullOrWhiteSpace(destinationIp) || destinationPort > 0;

        // Require at least one targeting condition
        if (!hasApp && !hasDest && !allowAllTraffic)
            return (false, "Provide an application path, a destination IP/port, or enable 'Apply to all traffic'.");

        // Windows QoS cannot combine app path + destination in one rule
        if (hasApp && hasDest)
            return (false, "Windows QoS cannot combine an application path with a destination IP or port. Create either an app policy or a destination-only policy.");

        string name = string.IsNullOrWhiteSpace(policyName)
            ? BuildPolicyName(processName, destinationIp, destinationPort)
            : policyName.Trim();

        var command = new System.Text.StringBuilder();
        command.Append($"$n={Ps(name)}; ");
        command.Append("Get-NetQosPolicy -Name $n -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Remove-NetQosPolicy -Confirm:$false; ");
        command.Append($"New-NetQosPolicy -Name $n -DSCPAction {dscp}");

        if (hasApp)
            command.Append($" -AppPathNameMatchCondition {Ps(processPath)}");
        if (!string.IsNullOrWhiteSpace(destinationIp))
            command.Append($" -IPDstPrefixMatchCondition {Ps(destinationIp)}");
        if (destinationPort is > 0 and <= 65535)
            command.Append($" -IPDstPortMatchCondition {destinationPort}");
        if (protocol is "TCP" or "UDP")
            command.Append($" -IPProtocolMatchCondition {protocol}");
        if (throttleKbps > 0)
            command.Append($" -ThrottleRateActionBitsPerSecond {throttleKbps * 1000L}");

        command.Append(" -NetworkProfile All -PolicyStore ActiveStore -ErrorAction Stop");
        var (success, detail) = await RunElevatedAsync(command.ToString());
        if (!success) return (false, detail);

        string scope = hasDest
            ? $"{destinationIp}{(destinationPort > 0 ? $":{destinationPort}" : "")}{(protocol is "TCP" or "UDP" ? $"/{protocol}" : "")}"
            : processName;
        return (true, $"{PriorityLabel(dscp)} priority applied to {scope}.");
    }

    public static async Task<(bool Success, string Message)> RemovePolicyAsync(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName)) return (false, "Choose a policy name to remove.");
        string command = $"$n={Ps(policyName.Trim())}; $removed=$false; " +
                         "foreach($store in @('localhost','ActiveStore')) { " +
                         "$policy=Get-NetQosPolicy -Name $n -PolicyStore $store -ErrorAction SilentlyContinue; " +
                         "if($policy) { $policy | Remove-NetQosPolicy -PolicyStore $store -Confirm:$false -ErrorAction Stop; $removed=$true } }; " +
                         "if(-not $removed) { throw 'Policy was not found in the local or active store.' }";
        return await RunElevatedAsync(command);
    }

    public static string BuildPolicyName(string processName, string? destinationIp = null, int destinationPort = 0)
    {
        string app = Regex.Replace(Path.GetFileNameWithoutExtension(processName ?? "app"), "[^a-zA-Z0-9_]", "_");
        string target = string.IsNullOrWhiteSpace(destinationIp) ? "app" : Regex.Replace(destinationIp, "[^a-zA-Z0-9]", "_");
        return $"WNC_QoS_{app}_{target}{(destinationPort > 0 ? "_" + destinationPort : "")}";
    }

    public static string PriorityLabel(int dscp) => dscp switch
    {
        HighPriorityDscp => "High",
        LowPriorityDscp => "Low",
        StandardPriorityDscp => "Standard",
        _ => $"DSCP {dscp}"
    };

    private static string Ps(string value) => $"'{value.Replace("'", "''")}'";

    private static async Task<(bool Success, string Message)> RunElevatedAsync(string command)
    {
        var (ok, error) = await ElevatedRunner.RunPowerShellWithErrorAsync(command);
        return (ok, error);
    }
}

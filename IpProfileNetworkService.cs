using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetSwitcher
{
    public static partial class NetworkService
    {
        public static async Task<SwitchResult> ApplyIpProfileWithRollbackAsync(
            IpProfile profile,
            AdapterInfo adapter,
            IpProfile previousProfile,
            IProgress<string> progress = null)
        {
            var result = new SwitchResult();
            var validationError = "";
            if (profile == null || !profile.TryValidate(out validationError))
            {
                result.Message = validationError.Length == 0 ? "IP 配置无效。" : validationError;
                result.Steps.Add(new OperationStep { Name = "检查 IP 配置", Success = false, Detail = result.Message });
                return result;
            }
            if (adapter == null)
            {
                result.Message = "找不到配置绑定的网卡，请编辑配置后重新选择网卡。";
                result.Steps.Add(new OperationStep { Name = "检查目标网卡", Success = false, Detail = result.Message });
                return result;
            }

            progress?.Report("正在保存「" + adapter.Name + "」的当前 IP 配置…");
            var captured = await Task.Run(() => TryCaptureIpConfiguration(adapter, out var snapshot, out var error)
                ? (success: true, snapshot, error: "")
                : (success: false, snapshot: (IpNetworkSnapshot)null, error));
            result.Steps.Add(new OperationStep
            {
                Name = "保存原 IP 配置",
                Success = captured.success,
                Detail = captured.success ? "已保存 IPv4、网关和 DNS 设置" : captured.error
            });
            if (!captured.success)
            {
                result.Message = "无法保存应用前的 IP 配置，已取消操作以避免无法回滚。";
                AppLogger.Error(result.Message + " " + captured.error);
                return result;
            }

            progress?.Report("正在应用固定 IP 配置「" + profile.Name + "」…");
            var applied = await Task.Run(() => ApplyStaticIpConfiguration(
                profile, adapter, previousProfile, out var message)
                    ? (success: true, message)
                    : (success: false, message));
            result.Steps.Add(new OperationStep
            {
                Name = "应用固定 IP 配置",
                Success = applied.success,
                Detail = applied.message
            });

            if (!applied.success)
            {
                progress?.Report("应用未完成，正在恢复原 IP 配置…");
                var rollback = await Task.Run(() => RestoreIpConfiguration(
                    captured.snapshot, adapter, profile, previousProfile, out var rollbackMessage)
                        ? (success: true, rollbackMessage)
                        : (success: false, rollbackMessage));
                result.RolledBack = rollback.success;
                result.Steps.Add(new OperationStep
                {
                    Name = "恢复原 IP 配置",
                    Success = rollback.success,
                    Detail = rollback.rollbackMessage
                });
                result.Message = rollback.success
                    ? "固定 IP 配置未能应用，已恢复原设置。"
                    : "固定 IP 配置应用失败，自动恢复也未完全成功，请查看操作详情。";
                AppLogger.Error(result.ToDetailText());
                return result;
            }

            progress?.Report("正在确认本机网络参数…");
            var current = await Task.Run(Enumerate);
            var verified = FindByIdentity(current, adapter.Id, adapter.Name);
            var matches = IpProfileMatchesAdapter(profile, verified);
            result.Steps.Add(new OperationStep
            {
                Name = "确认本机网络参数",
                Success = matches,
                Detail = matches
                    ? "IP " + profile.Address + "，子网掩码 " + profile.SubnetMask
                    : "系统回读结果与目标配置不一致"
            });
            if (!matches)
            {
                progress?.Report("配置确认未通过，正在恢复原 IP 配置…");
                var rollback = await Task.Run(() => RestoreIpConfiguration(
                    captured.snapshot, adapter, profile, previousProfile, out var rollbackMessage)
                        ? (success: true, rollbackMessage)
                        : (success: false, rollbackMessage));
                result.RolledBack = rollback.success;
                result.Steps.Add(new OperationStep
                {
                    Name = "恢复原 IP 配置",
                    Success = rollback.success,
                    Detail = rollback.rollbackMessage
                });
                result.Message = rollback.success
                    ? "系统未确认新的 IP 配置，已恢复原设置。"
                    : "系统未确认新的 IP 配置，自动恢复也未完全成功，请查看操作详情。";
                AppLogger.Error(result.ToDetailText());
                return result;
            }

            result.Success = true;
            result.Message = "已应用 IP 配置「" + profile.Name + "」到「" + adapter.Name + "」。";
            AppLogger.Info(result.ToDetailText());
            return result;
        }

        public static async Task<SwitchResult> RestoreDhcpWithRollbackAsync(
            AdapterInfo adapter,
            IpProfile previousProfile,
            IProgress<string> progress = null)
        {
            var result = new SwitchResult();
            if (adapter == null)
            {
                result.Message = "没有找到要恢复为自动获取的网卡。";
                result.Steps.Add(new OperationStep { Name = "检查目标网卡", Success = false, Detail = result.Message });
                return result;
            }

            progress?.Report("正在保存「" + adapter.Name + "」的当前 IP 配置…");
            var captured = await Task.Run(() => TryCaptureIpConfiguration(adapter, out var snapshot, out var error)
                ? (success: true, snapshot, error: "")
                : (success: false, snapshot: (IpNetworkSnapshot)null, error));
            result.Steps.Add(new OperationStep
            {
                Name = "保存原 IP 配置",
                Success = captured.success,
                Detail = captured.success ? "已保存 IPv4、网关和 DNS 设置" : captured.error
            });
            if (!captured.success)
            {
                result.Message = "无法保存当前 IP 配置，已取消操作以避免无法回滚。";
                AppLogger.Error(result.Message + " " + captured.error);
                return result;
            }

            progress?.Report("正在恢复自动获取 IP 和 DNS…");
            var applied = await Task.Run(() => ApplyDhcpConfiguration(adapter, previousProfile, out var message)
                ? (success: true, message)
                : (success: false, message));
            result.Steps.Add(new OperationStep { Name = "恢复 DHCP", Success = applied.success, Detail = applied.message });
            if (!applied.success)
            {
                progress?.Report("恢复 DHCP 未完成，正在还原原 IP 配置…");
                var rollback = await Task.Run(() => RestoreIpConfiguration(
                    captured.snapshot, adapter, null, previousProfile, out var rollbackMessage)
                        ? (success: true, rollbackMessage)
                        : (success: false, rollbackMessage));
                result.RolledBack = rollback.success;
                result.Steps.Add(new OperationStep
                {
                    Name = "恢复原 IP 配置",
                    Success = rollback.success,
                    Detail = rollback.rollbackMessage
                });
                result.Message = rollback.success
                    ? "恢复自动获取未完成，已恢复原设置。"
                    : "恢复自动获取失败，自动回滚也未完全成功，请查看操作详情。";
                AppLogger.Error(result.ToDetailText());
                return result;
            }

            result.Success = true;
            result.Message = "已将「" + adapter.Name + "」恢复为自动获取 IP 和 DNS。";
            AppLogger.Info(result.ToDetailText());
            return result;
        }

        public static bool IpProfileMatchesAdapter(IpProfile profile, AdapterInfo adapter)
        {
            if (profile == null || adapter == null) return false;
            return string.Equals(profile.Address, adapter.Ip, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(profile.SubnetMask, adapter.Mask, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(profile.Gateway ?? "", adapter.Gateway ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCaptureIpConfiguration(
            AdapterInfo adapter,
            out IpNetworkSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = "";
            var script =
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" +
                "$ErrorActionPreference='Stop'\n" +
                "$adapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(adapter.Name) + "' } | Select-Object -First 1\n" +
                "if($null -eq $adapter){ throw '找不到目标网卡' }\n" +
                "$idx=[int]$adapter.ifIndex\n" +
                "$iface=Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 | Select-Object -First 1\n" +
                "$ips=@(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.PrefixOrigin -ne 'WellKnown' } | " +
                "ForEach-Object { [pscustomobject]@{ address=$_.IPAddress; prefix=[int]$_.PrefixLength } })\n" +
                "$dns=@((Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)\n" +
                "$default=Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | " +
                "Sort-Object RouteMetric | Select-Object -First 1\n" +
                "$key='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{'+$adapter.InterfaceGuid.ToString()+'}'\n" +
                "$nameServer=(Get-ItemProperty -Path $key -Name NameServer -ErrorAction SilentlyContinue).NameServer\n" +
                "[pscustomobject]@{ name=$adapter.Name; dhcp=[string]$iface.Dhcp; dnsAutomatic=[string]::IsNullOrWhiteSpace([string]$nameServer); " +
                "ips=$ips; dns=$dns; gateway=$(if($default){[string]$default.NextHop}else{''}); " +
                "routeMetric=$(if($default){[int]$default.RouteMetric}else{$null}) } | ConvertTo-Json -Compress -Depth 4";

            var execution = RunPowerShell(script);
            if (!execution.Success || string.IsNullOrWhiteSpace(execution.StandardOutput))
            {
                error = execution.TimedOut
                    ? "保存 IP 配置超时。"
                    : "保存 IP 配置失败：" + ShortError(execution.StandardError);
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(execution.StandardOutput.Trim());
                var root = doc.RootElement;
                snapshot = new IpNetworkSnapshot
                {
                    AdapterName = root.TryGetProperty("name", out var name) ? name.GetString() ?? adapter.Name : adapter.Name,
                    DhcpEnabled = root.TryGetProperty("dhcp", out var dhcp) &&
                                  string.Equals(dhcp.GetString(), "Enabled", StringComparison.OrdinalIgnoreCase),
                    DnsAutomatic = root.TryGetProperty("dnsAutomatic", out var auto) && auto.ValueKind == JsonValueKind.True,
                    Gateway = root.TryGetProperty("gateway", out var gateway) ? gateway.GetString() ?? "" : "",
                    RouteMetric = root.TryGetProperty("routeMetric", out var metric) && metric.ValueKind == JsonValueKind.Number
                        ? metric.GetInt32()
                        : (int?)null
                };
                if (root.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ips.EnumerateArray())
                    {
                        var address = item.TryGetProperty("address", out var ip) ? ip.GetString() ?? "" : "";
                        var prefix = item.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : -1;
                        if (!string.IsNullOrWhiteSpace(address) && prefix >= 0)
                            snapshot.Addresses.Add(new IpAddressSnapshot { Address = address, PrefixLength = prefix });
                    }
                }
                if (root.TryGetProperty("dns", out var dns))
                {
                    if (dns.ValueKind == JsonValueKind.Array)
                        snapshot.DnsServers.AddRange(dns.EnumerateArray().Select(value => value.GetString() ?? "").Where(value => value.Length > 0));
                    else if (dns.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dns.GetString()))
                        snapshot.DnsServers.Add(dns.GetString());
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "解析 IP 配置快照失败：" + ex.Message;
                AppLogger.Error(error, ex);
                snapshot = null;
                return false;
            }
        }

        private static bool ApplyStaticIpConfiguration(
            IpProfile profile,
            AdapterInfo adapter,
            IpProfile previousProfile,
            out string message)
        {
            IpProfile.TryMaskToPrefixLength(profile.SubnetMask, out var prefixLength);
            var script = BeginAdapterScript(adapter.Name);
            AppendRemoveProfileRoutes(script, previousProfile);
            AppendClearIpv4Configuration(script);
            script.AppendLine("Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled");
            var gateway = string.IsNullOrWhiteSpace(profile.Gateway)
                ? ""
                : " -DefaultGateway '" + Esc(profile.Gateway) + "'";
            script.AppendLine("New-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress '" +
                              Esc(profile.Address) + "' -PrefixLength " + prefixLength + gateway + " | Out-Null");
            AppendDnsConfiguration(script, profile.DnsServers);
            AppendAddProfileRoutes(script, profile);
            script.AppendLine("Start-Sleep -Milliseconds 300");
            script.AppendLine("$verify=Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress '" + Esc(profile.Address) + "' -ErrorAction SilentlyContinue");
            script.AppendLine("if($null -eq $verify){ throw '系统未能回读新的 IPv4 地址' }");

            var execution = RunPowerShell(script.ToString());
            if (!execution.Success)
            {
                message = execution.TimedOut
                    ? "应用固定 IP 配置超时。"
                    : "应用固定 IP 配置失败：" + ShortError(execution.StandardError);
                AppLogger.Error(message);
                return false;
            }
            message = "已写入 IPv4、网关、DNS 和静态路由；接口跃点数保持不变。";
            return true;
        }

        private static bool ApplyDhcpConfiguration(AdapterInfo adapter, IpProfile previousProfile, out string message)
        {
            var script = BeginAdapterScript(adapter.Name);
            AppendRemoveProfileRoutes(script, previousProfile);
            AppendClearIpv4Configuration(script);
            script.AppendLine("Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled");
            script.AppendLine("Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses");
            script.AppendLine("$verify=Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 | Select-Object -First 1");
            script.AppendLine("if([string]$verify.Dhcp -ne 'Enabled'){ throw '系统未能启用 DHCP' }");
            var execution = RunPowerShell(script.ToString());
            if (!execution.Success)
            {
                message = execution.TimedOut
                    ? "恢复自动获取超时。"
                    : "恢复自动获取失败：" + ShortError(execution.StandardError);
                AppLogger.Error(message);
                return false;
            }
            message = "已启用 DHCP，并将 DNS 恢复为自动获取；接口跃点数保持不变。";
            return true;
        }

        private static bool RestoreIpConfiguration(
            IpNetworkSnapshot snapshot,
            AdapterInfo adapter,
            IpProfile failedProfile,
            IpProfile previousProfile,
            out string message)
        {
            if (snapshot == null)
            {
                message = "缺少可用于恢复的 IP 配置快照。";
                return false;
            }

            var script = BeginAdapterScript(adapter.Name);
            AppendRemoveProfileRoutes(script, failedProfile);
            AppendClearIpv4Configuration(script);
            if (snapshot.DhcpEnabled)
            {
                script.AppendLine("Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled");
            }
            else
            {
                script.AppendLine("Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled");
                for (var index = 0; index < snapshot.Addresses.Count; index++)
                {
                    var address = snapshot.Addresses[index];
                    var gateway = index == 0 && !string.IsNullOrWhiteSpace(snapshot.Gateway)
                        ? " -DefaultGateway '" + Esc(snapshot.Gateway) + "'"
                        : "";
                    script.AppendLine("New-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress '" +
                                      Esc(address.Address) + "' -PrefixLength " + address.PrefixLength + gateway + " | Out-Null");
                }
            }

            if (snapshot.DnsAutomatic)
                script.AppendLine("Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses");
            else
                AppendDnsConfiguration(script, snapshot.DnsServers);
            AppendAddProfileRoutes(script, previousProfile);

            var execution = RunPowerShell(script.ToString());
            if (!execution.Success)
            {
                message = execution.TimedOut
                    ? "恢复原 IP 配置超时。"
                    : "恢复原 IP 配置失败：" + ShortError(execution.StandardError);
                AppLogger.Error(message);
                return false;
            }
            message = "已恢复操作前的 IPv4、网关和 DNS 设置。";
            return true;
        }

        private static StringBuilder BeginAdapterScript(string adapterName)
        {
            var script = new StringBuilder();
            script.AppendLine("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8");
            script.AppendLine("$ErrorActionPreference='Stop'");
            script.AppendLine("$adapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(adapterName) + "' } | Select-Object -First 1");
            script.AppendLine("if($null -eq $adapter){ throw '找不到目标网卡' }");
            script.AppendLine("if($adapter.Status -eq 'Disabled'){ $adapter | Enable-NetAdapter -Confirm:$false; Start-Sleep -Milliseconds 500; " +
                              "$adapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(adapterName) + "' } | Select-Object -First 1 }");
            script.AppendLine("$idx=[int]$adapter.ifIndex");
            return script;
        }

        private static void AppendClearIpv4Configuration(StringBuilder script)
        {
            script.AppendLine("Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
            script.AppendLine("Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
                              "Where-Object { $_.PrefixOrigin -ne 'WellKnown' } | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue");
        }

        private static void AppendDnsConfiguration(StringBuilder script, IEnumerable<string> dnsServers)
        {
            var servers = (dnsServers ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray();
            if (servers.Length == 0)
            {
                script.AppendLine("Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses");
                return;
            }
            var list = string.Join(",", servers.Select(value => "'" + Esc(value) + "'"));
            script.AppendLine("Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses @(" + list + ")");
        }

        private static void AppendRemoveProfileRoutes(StringBuilder script, IpProfile profile)
        {
            if (profile?.Routes == null) return;
            foreach (var route in profile.Routes)
            {
                if (string.IsNullOrWhiteSpace(route.DestinationPrefix) || string.IsNullOrWhiteSpace(route.Gateway)) continue;
                var prefix = Esc(route.DestinationPrefix);
                var gateway = Esc(route.Gateway);
                script.AppendLine("Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '" + prefix +
                                  "' -NextHop '" + gateway + "' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
                                  "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
                script.AppendLine("Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '" + prefix +
                                  "' -NextHop '" + gateway + "' -PolicyStore PersistentStore -ErrorAction SilentlyContinue | " +
                                  "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
            }
        }

        private static void AppendAddProfileRoutes(StringBuilder script, IpProfile profile)
        {
            if (profile?.Routes == null) return;
            foreach (var route in profile.Routes)
            {
                if (string.IsNullOrWhiteSpace(route.DestinationPrefix) || string.IsNullOrWhiteSpace(route.Gateway)) continue;
                script.AppendLine("New-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '" +
                                  Esc(route.DestinationPrefix) + "' -NextHop '" + Esc(route.Gateway) +
                                  "' -RouteMetric " + route.Metric + " -PolicyStore PersistentStore | Out-Null");
            }
        }

        private sealed class IpNetworkSnapshot
        {
            public string AdapterName { get; set; } = "";
            public bool DhcpEnabled { get; set; }
            public bool DnsAutomatic { get; set; }
            public List<IpAddressSnapshot> Addresses { get; } = new List<IpAddressSnapshot>();
            public List<string> DnsServers { get; } = new List<string>();
            public string Gateway { get; set; } = "";
            public int? RouteMetric { get; set; }
        }

        private sealed class IpAddressSnapshot
        {
            public string Address { get; set; } = "";
            public int PrefixLength { get; set; }
        }
    }
}

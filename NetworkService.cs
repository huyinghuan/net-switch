using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetSwitcher
{
    /// <summary>
    /// 一张网卡在某一时刻的快照信息。
    /// </summary>
    public sealed class AdapterInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public NetworkInterfaceType Type { get; set; }
        public OperationalStatus Status { get; set; }
        public bool LinkUp { get; set; }
        public long SpeedBps { get; set; }
        public int IfIndex { get; set; } = -1;

        public string Ip { get; set; } = "";
        public string Mask { get; set; } = "";
        public string Gateway { get; set; } = "";
        public string Mac { get; set; } = "";

        public int Metric { get; set; } = -1;      // 接口跃点数（PowerShell 读取，可能为 -1 表示尚未刷新）
        public int RouteMetric { get; set; } = -1; // 默认网关的路由跃点数

        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }

        public bool IsWireless => Type == NetworkInterfaceType.Wireless80211;
        public bool IsEthernet => Type == NetworkInterfaceType.Ethernet;
        public bool HasIp => !string.IsNullOrEmpty(Ip);
        public bool HasGateway => !string.IsNullOrEmpty(Gateway);
        public bool IsUp => Status == OperationalStatus.Up;

        /// <summary>
        /// 能否真正承担上网出口：必须在线 + 有 IP + 有默认网关。
        /// （虚拟交换机之类的网卡往往有 IP 但没有网关，不能当出口，必须排除。）
        /// </summary>
        public bool CanRoute => IsUp && HasIp && HasGateway;

        /// <summary>
        /// Windows 真正的选路依据 ≈ 接口跃点数 + 路由跃点数，越小越优先。
        /// </summary>
        public int EffectiveMetric
        {
            get
            {
                var m = Metric < 0 ? 10000 : Metric;
                var r = RouteMetric < 0 ? 0 : RouteMetric;
                return m + r;
            }
        }

        public string KindText => IsWireless ? "Wi-Fi" : (IsEthernet ? "有线" : "其他");

        public override string ToString()
        {
            // 卡片标题已经说明了类型，下拉框里只留网卡名；虚拟网卡额外标注避免误选
            return NetworkService.LooksVirtual(this) ? Name + "（虚拟）" : Name;
        }
    }

    /// <summary>
    /// 网卡枚举 + 一键切换（改跃点数 / 启用禁用网卡）。
    /// 读信息走 .NET 原生 API（零开销），改配置走 PowerShell 的 NetAdapter 模块（官方支持的方式）。
    /// </summary>
    public static class NetworkService
    {
        // 自动选择网卡时优先排除的虚拟 / 隧道设备
        private static readonly string[] VirtualKeywords =
        {
            "virtualbox", "vmware", "hyper-v", "hyperv", "virtual ", "virtualswitch", "tap-windows",
            "tap-win32", "wintun", "npcap", "npf_", "loopback", "isatap", "teredo", "6to4",
            "bluetooth", "wi-fi direct", "pseudo", "npcap loopback", "ras async", "kernel debug",
            "tailscale", "zerotier", "easytier", "radmin", "sangfor", "miniport", "packet scheduler",
            "qos packet", "wan miniport", "pangolin", "dslite", "ipsecdrv",
            "openvpn", "wireguard", "vpn", "docker", "wsl", "bridge", "vethernet", "vpn client",
            // 中文系统下的常见虚拟 / 无关设备
            "蓝牙", "虚拟", "隧道", "环回", "虚拟主机", "小型端口", "深信服", "花生壳"
        };

        private const int PreferredMetric = 10;   // 选为出口的网卡：跃点数最低
        private const int SecondaryMetric = 300;  // 另一张网卡：跃点数抬高，保留故障转移能力

        #region 枚举

        public static List<AdapterInfo> Enumerate()
        {
            var result = new List<AdapterInfo>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Unknown)
                    continue;

                IPv4InterfaceProperties v4Props = null;
                IPInterfaceProperties ipProps = null;
                int idx = -1;
                try
                {
                    ipProps = ni.GetIPProperties();
                    v4Props = ipProps.GetIPv4Properties();
                    idx = v4Props.Index;
                }
                catch { /* 未启用 IPv4 的网卡 */ }

                string ip = "", mask = "", gw = "";
                if (ipProps != null)
                {
                    var addr = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (addr != null)
                    {
                        ip = addr.Address.ToString();
                        mask = addr.IPv4Mask == null ? "" : addr.IPv4Mask.ToString();
                    }
                    var gateway = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address?.AddressFamily == AddressFamily.InterNetwork);
                    if (gateway != null) gw = gateway.Address.ToString();
                }

                long sent = 0, recv = 0;
                try
                {
                    var stat = ni.GetIPStatistics();
                    sent = stat.BytesSent;
                    recv = stat.BytesReceived;
                }
                catch { }

                string mac = "";
                try
                {
                    var pa = ni.GetPhysicalAddress();
                    if (pa != null && pa.GetAddressBytes().Length > 0)
                        mac = BitConverter.ToString(pa.GetAddressBytes()).Replace("-", ":");
                }
                catch { }

                result.Add(new AdapterInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description ?? ni.Name,
                    Type = ni.NetworkInterfaceType,
                    Status = ni.OperationalStatus,
                    LinkUp = ni.OperationalStatus == OperationalStatus.Up,
                    SpeedBps = ni.Speed > 0 ? ni.Speed : -1,
                    IfIndex = idx,
                    Ip = ip,
                    Mask = mask,
                    Gateway = gw,
                    Mac = mac,
                    BytesSent = sent,
                    BytesReceived = recv
                });
            }
            return result;
        }

        /// <summary>排除明显的虚拟网卡，用于下拉框展示与自动推荐。</summary>
        public static bool LooksVirtual(AdapterInfo a)
        {
            var tag = (a.Name + " " + (a.Description ?? "")).ToLowerInvariant();
            return VirtualKeywords.Any(k => tag.Contains(k));
        }

        /// <summary>把上一次的跃点数信息带到新的快照里（跃点数要靠 PowerShell 取，开销较大，不每次刷）。</summary>
        public static void CarryMetrics(List<AdapterInfo> previous, List<AdapterInfo> current)
        {
            foreach (var c in current)
            {
                var old = previous?.FirstOrDefault(p => p.IfIndex >= 0 && p.IfIndex == c.IfIndex);
                if (old != null)
                {
                    c.Metric = old.Metric;
                    c.RouteMetric = old.RouteMetric;
                }
            }
        }

        public static AdapterInfo PickDefault(IEnumerable<AdapterInfo> list, bool wireless, string rememberedName) =>
            PickDefault(list, wireless, "", rememberedName);

        public static AdapterInfo PickDefault(IEnumerable<AdapterInfo> list, bool wireless, string rememberedId, string rememberedName)
        {
            var all = list.Where(a => wireless ? a.IsWireless : a.IsEthernet).ToList();
            if (all.Count == 0) return null;

            // 1) 稳定 ID 优先；兼容旧配置时再按名称匹配
            if (!string.IsNullOrEmpty(rememberedId))
            {
                var hit = all.FirstOrDefault(a => string.Equals(a.Id, rememberedId, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            if (!string.IsNullOrEmpty(rememberedName))
            {
                var hit = all.FirstOrDefault(a => a.Name == rememberedName);
                if (hit != null) return hit;
            }

            // 2) 真正的物理网卡排在虚拟网卡前面，能上网的排在最前面
            return all
                .OrderBy(a => LooksVirtual(a) ? 1 : 0)
                .ThenBy(a => !a.CanRoute)
                .ThenBy(a => !a.IsUp)
                .ThenBy(a => !a.HasIp)
                .First();
        }

        /// <summary>下拉框候选项：物理网卡优先，若用户上次的网卡是虚拟的也保留。</summary>
        public static List<AdapterInfo> BuildCandidateList(IEnumerable<AdapterInfo> all, bool wireless, string rememberedName) =>
            BuildCandidateList(all, wireless, "", rememberedName);

        public static List<AdapterInfo> BuildCandidateList(IEnumerable<AdapterInfo> all, bool wireless, string rememberedId, string rememberedName)
        {
            var same = all.Where(a => wireless ? a.IsWireless : a.IsEthernet).ToList();
            // 物理网卡在前，能上网的在前；虚拟网卡排在后面备用
            var ordered = same
                .OrderBy(a => LooksVirtual(a) ? 1 : 0)
                .ThenBy(a => !a.CanRoute)
                .ThenBy(a => !a.IsUp)
                .ToList();

            var list = ordered.Where(a => !LooksVirtual(a)).ToList();
            if (list.Count == 0)
            {
                // 本机没有这类物理网卡时，仍然把虚拟的放出来，让用户能手动选择
                list = ordered.ToList();
            }

            if (!string.IsNullOrEmpty(rememberedId) || !string.IsNullOrEmpty(rememberedName))
            {
                var missing = ordered.FirstOrDefault(a =>
                    ((string.IsNullOrEmpty(rememberedId) && a.Name == rememberedName) ||
                     string.Equals(a.Id, rememberedId, StringComparison.OrdinalIgnoreCase)) && !list.Contains(a));
                if (missing != null) list.Insert(0, missing);
            }
            return list;
        }

        public static AdapterInfo FindByIdentity(IEnumerable<AdapterInfo> list, string id, string name)
        {
            if (list == null) return null;
            if (!string.IsNullOrEmpty(id))
            {
                var byId = list.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                if (byId != null) return byId;
            }
            return string.IsNullOrEmpty(name) ? null : list.FirstOrDefault(a => a.Name == name);
        }

        #endregion

        #region 切换

        public static async Task<SwitchResult> SwitchExitWithRollbackAsync(
            AdapterInfo target, AdapterInfo other, bool hardIsolate)
        {
            var result = new SwitchResult();
            if (target == null)
            {
                result.Message = "没有可用的目标网卡。";
                result.Steps.Add(new OperationStep { Name = "检查目标网卡", Success = false, Detail = result.Message });
                return result;
            }

            var selected = new[] { target, other }.Where(a => a != null).Distinct().ToArray();
            var captured = await Task.Run(() => TryCaptureConfiguration(selected, out var snapshot, out var error)
                ? (success: true, snapshot, error: "")
                : (success: false, snapshot: (NetworkConfigurationSnapshot)null, error));
            result.Steps.Add(new OperationStep
            {
                Name = "保存原网络配置",
                Success = captured.success,
                Detail = captured.success ? "已保存网卡启用状态和跃点数" : captured.error
            });
            if (!captured.success)
            {
                result.Message = "无法保存切换前的网络配置，已取消操作以避免无法回滚。";
                AppLogger.Error(result.Message + " " + captured.error);
                return result;
            }

            var applied = await Task.Run(() => SetExit(target, other, hardIsolate, out var message)
                ? (success: true, message)
                : (success: false, message));
            result.Steps.Add(new OperationStep { Name = "应用出口配置", Success = applied.success, Detail = applied.message });
            if (!applied.success)
            {
                await RollBackAsync(captured.snapshot, result);
                result.Message = result.RolledBack
                    ? "切换未完成，已恢复原网络配置。"
                    : "切换失败，并且自动恢复未完全成功，请查看操作详情。";
                AppLogger.Error(result.ToDetailText());
                return result;
            }

            AdapterInfo verifiedTarget = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(attempt == 0 ? 700 : 900);
                var current = await Task.Run(Enumerate);
                verifiedTarget = FindByIdentity(current, target.Id, target.Name);
                if (verifiedTarget?.CanRoute == true) break;
            }

            var addressReady = verifiedTarget?.CanRoute == true;
            result.Steps.Add(new OperationStep
            {
                Name = "获取网络地址",
                Success = addressReady,
                Detail = addressReady
                    ? "IP " + verifiedTarget.Ip + "，网关 " + verifiedTarget.Gateway
                    : "目标网卡未在限定时间内获得 IP 和默认网关"
            });

            if (addressReady)
            {
                var connectivity = await ConnectivityService.CheckAsync(verifiedTarget);
                result.Steps.AddRange(connectivity);
            }

            var internetOk = result.Steps.Any(s => s.Name == "公网 HTTPS" && s.Success);
            if (!addressReady || !internetOk)
            {
                await RollBackAsync(captured.snapshot, result);
                result.Message = result.RolledBack
                    ? "新出口无法访问公网，已自动恢复原网络配置。"
                    : "新出口无法访问公网，自动恢复也未完全成功，请立即查看详情。";
                AppLogger.Error(result.ToDetailText());
                return result;
            }

            result.Success = true;
            result.Message = "已切换到「" + target.Name + "」，联网检测通过。";
            AppLogger.Info(result.ToDetailText());
            return result;
        }

        private static async Task RollBackAsync(NetworkConfigurationSnapshot snapshot, SwitchResult result)
        {
            var rollback = await Task.Run(() => RestoreConfiguration(snapshot, out var message)
                ? (success: true, message)
                : (success: false, message));
            result.RolledBack = rollback.success;
            result.Steps.Add(new OperationStep
            {
                Name = "恢复原网络配置",
                Success = rollback.success,
                Detail = rollback.message
            });
        }

        /// <summary>
        /// 把 target 设为上网出口；other 为另一张网卡（可为 null）。
        /// hardIsolate 为 true 时额外禁用另一张网卡。
        /// </summary>
        public static bool SetExit(AdapterInfo target, AdapterInfo other, bool hardIsolate, out string message)
        {
            message = "";
            if (target == null)
            {
                message = "没有可用的目标网卡";
                return false;
            }
            if (target.IfIndex < 0)
            {
                message = "网卡「" + target.Name + "」未启用 IPv4，无法设置跃点数";
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8");
            sb.AppendLine("$ErrorActionPreference='Stop'");

            // 确保目标网卡是启用的（硬隔离模式下它可能上次被禁用过）
            sb.AppendLine("$targetAdapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(target.Name) + "' } | Select-Object -First 1");
            sb.AppendLine("if($null -eq $targetAdapter){ throw '找不到目标网卡' }");
            sb.AppendLine("if($targetAdapter.Status -eq 'Disabled'){ $targetAdapter | Enable-NetAdapter -Confirm:$false }");
            sb.AppendLine("$targetV4=Get-NetIPInterface -InterfaceIndex " + target.IfIndex + " -AddressFamily IPv4");

            // 跃点数：目标最低，另一张抬高（仍保留，作为备用出口）
            sb.AppendLine("$targetV4 | Set-NetIPInterface -InterfaceMetric " + PreferredMetric);
            sb.AppendLine("$targetV6=Get-NetIPInterface -InterfaceIndex " + target.IfIndex + " -AddressFamily IPv6 -ErrorAction SilentlyContinue");
            sb.AppendLine("if($targetV6){ $targetV6 | Set-NetIPInterface -InterfaceMetric " + PreferredMetric + " }");

            if (other != null && other != target)
            {
                if (!hardIsolate)
                {
                    sb.AppendLine("$otherAdapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(other.Name) + "' } | Select-Object -First 1");
                    sb.AppendLine("if($null -eq $otherAdapter){ throw '找不到备用网卡' }");
                    sb.AppendLine("if($otherAdapter.Status -eq 'Disabled'){ $otherAdapter | Enable-NetAdapter -Confirm:$false }");
                }
                if (other.IfIndex >= 0)
                {
                    sb.AppendLine("$otherV4=Get-NetIPInterface -InterfaceIndex " + other.IfIndex + " -AddressFamily IPv4");
                    sb.AppendLine("$otherV4 | Set-NetIPInterface -InterfaceMetric " + SecondaryMetric);
                    sb.AppendLine("$otherV6=Get-NetIPInterface -InterfaceIndex " + other.IfIndex + " -AddressFamily IPv6 -ErrorAction SilentlyContinue");
                    sb.AppendLine("if($otherV6){ $otherV6 | Set-NetIPInterface -InterfaceMetric " + SecondaryMetric + " }");
                }
            }

            if (hardIsolate && other != null && other != target)
            {
                sb.AppendLine("Start-Sleep -Milliseconds 150");
                sb.AppendLine("$otherAdapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(other.Name) + "' } | Select-Object -First 1");
                sb.AppendLine("if($null -eq $otherAdapter){ throw '找不到要隔离的网卡' }");
                sb.AppendLine("$otherAdapter | Disable-NetAdapter -Confirm:$false");
                sb.AppendLine("$otherState=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(other.Name) + "' } | Select-Object -First 1");
                sb.AppendLine("if($otherState.Status -ne 'Disabled'){ throw '未能禁用另一张网卡' }");
            }

            var execution = RunPowerShell(sb.ToString());
            if (!execution.Success)
            {
                message = execution.TimedOut
                    ? "切换超时，系统网络服务可能没有响应。"
                    : "切换命令执行失败：" + ShortError(execution.StandardError);
                AppLogger.Error(message);
                return false;
            }

            // 回读校验
            var check = Enumerate();
            if (!RefreshMetrics(check))
            {
                message = "切换命令已执行，但无法回读网卡跃点数进行确认。";
                AppLogger.Error(message);
                return false;
            }
            var verify = check.FirstOrDefault(a => a.IfIndex == target.IfIndex);
            if (verify == null || verify.Metric != PreferredMetric)
            {
                message = "未能修改网卡跃点数，请以管理员身份运行本程序后重试。";
                AppLogger.Error(message);
                return false;
            }

            if (!hardIsolate && other != null && other.IfIndex >= 0)
            {
                var otherVerify = check.FirstOrDefault(a => a.IfIndex == other.IfIndex);
                if (otherVerify == null || otherVerify.Metric != SecondaryMetric)
                {
                    message = "目标出口已设置，但未能确认备用网卡的跃点数。";
                    AppLogger.Error(message);
                    return false;
                }
            }

            message = hardIsolate && other != null
                ? "已切换到「" + target.Name + "」，另一张网卡已禁用。"
                : !hardIsolate && other != null
                    ? "已切换到「" + target.Name + "」，两张网卡都保持启用。"
                    : "已切换到「" + target.Name + "」。";
            return true;
        }

        /// <summary>单独启用 / 禁用某张网卡（取消硬隔离时用于恢复）。</summary>
        public static bool SetAdapterEnabled(AdapterInfo adapter, bool enabled, out string message)
        {
            message = "";
            if (adapter == null)
            {
                message = "没有找到要操作的网卡。";
                return false;
            }
            var cmd = enabled ? "Enable-NetAdapter" : "Disable-NetAdapter";
            var invalidStateCheck = enabled
                ? "if($state.Status -eq 'Disabled'){ throw '网卡仍处于禁用状态' }"
                : "if($state.Status -ne 'Disabled'){ throw '网卡未被禁用' }";
            var result = RunPowerShell("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" +
                          "$ErrorActionPreference='Stop'\n" +
                          "$adapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(adapter.Name) + "' } | Select-Object -First 1\n" +
                          "if($null -eq $adapter){ throw '找不到网卡' }\n" +
                          "$adapter | " + cmd + " -Confirm:$false\n" +
                          "$state=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(adapter.Name) + "' } | Select-Object -First 1\n" +
                          invalidStateCheck);
            if (result.Success) return true;

            message = result.TimedOut
                ? "操作网卡「" + adapter.Name + "」超时。"
                : "操作网卡「" + adapter.Name + "」失败：" + ShortError(result.StandardError);
            AppLogger.Error(message);
            return false;
        }

        public static bool RestoreAutomaticMetrics(IEnumerable<AdapterInfo> adapters, out string message)
        {
            var list = (adapters ?? Enumerable.Empty<AdapterInfo>())
                .Where(a => a != null)
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (list.Count == 0)
            {
                message = "没有可恢复的网卡。";
                return false;
            }

            var script = new StringBuilder();
            script.AppendLine("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8");
            script.AppendLine("$ErrorActionPreference='Stop'");
            foreach (var adapter in list)
            {
                script.AppendLine("$adapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(adapter.Name) + "' } | Select-Object -First 1");
                script.AppendLine("if($null -eq $adapter){ throw '找不到网卡：" + Esc(adapter.Name) + "' }");
                script.AppendLine("if($adapter.Status -eq 'Disabled'){ $adapter | Enable-NetAdapter -Confirm:$false }");
                script.AppendLine("Get-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Set-NetIPInterface -AutomaticMetric Enabled");
                script.AppendLine("Get-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue | Set-NetIPInterface -AutomaticMetric Enabled");
            }

            var execution = RunPowerShell(script.ToString());
            if (!execution.Success)
            {
                message = execution.TimedOut
                    ? "恢复 Windows 自动跃点数超时。"
                    : "恢复失败：" + ShortError(execution.StandardError);
                AppLogger.Error(message);
                return false;
            }

            message = "已启用所选网卡，并恢复 Windows 自动跃点数。";
            AppLogger.Info(message);
            return true;
        }

        private static bool TryCaptureConfiguration(
            IEnumerable<AdapterInfo> adapters,
            out NetworkConfigurationSnapshot snapshot,
            out string error)
        {
            snapshot = new NetworkConfigurationSnapshot();
            error = "";
            var names = (adapters ?? Enumerable.Empty<AdapterInfo>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => a.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
            {
                error = "没有需要保存的网卡。";
                return false;
            }

            var nameArray = string.Join(",", names.Select(n => "'" + Esc(n) + "'"));
            var script =
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" +
                "$ErrorActionPreference='Stop'\n" +
                "$names=@(" + nameArray + ")\n" +
                "$out=@()\n" +
                "foreach($name in $names){\n" +
                "  $adapter=Get-NetAdapter | Where-Object { $_.Name -eq $name } | Select-Object -First 1\n" +
                "  if($null -eq $adapter){ throw ('找不到网卡：'+$name) }\n" +
                "  $v4=Get-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1\n" +
                "  $v6=Get-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue | Select-Object -First 1\n" +
                "  $out+=[pscustomobject]@{ name=$adapter.Name; enabled=[bool]($adapter.Status -ne 'Disabled'); " +
                "v4Metric=$(if($v4){[int]$v4.InterfaceMetric}else{$null}); v4Automatic=$(if($v4){[string]$v4.AutomaticMetric}else{''}); " +
                "v6Metric=$(if($v6){[int]$v6.InterfaceMetric}else{$null}); v6Automatic=$(if($v6){[string]$v6.AutomaticMetric}else{''}) }\n" +
                "}\n" +
                "$out | ConvertTo-Json -Compress";

            var execution = RunPowerShell(script);
            if (!execution.Success || string.IsNullOrWhiteSpace(execution.StandardOutput))
            {
                error = execution.TimedOut ? "保存网络配置超时。" : ShortError(execution.StandardError);
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(execution.StandardOutput.Trim());
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in doc.RootElement.EnumerateArray()) AddConfigurationState(node, snapshot);
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddConfigurationState(doc.RootElement, snapshot);
                }
                if (snapshot.Adapters.Count == names.Count) return true;
                error = "未能读取全部网卡的原始状态。";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLogger.Error("解析网络配置快照失败。", ex);
                return false;
            }
        }

        private static void AddConfigurationState(JsonElement node, NetworkConfigurationSnapshot snapshot)
        {
            var state = new AdapterConfigurationState
            {
                Name = node.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                Enabled = node.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                V4Metric = NullableInt(node, "v4Metric"),
                V4Automatic = IsAutomatic(node, "v4Automatic"),
                V6Metric = NullableInt(node, "v6Metric"),
                V6Automatic = IsAutomatic(node, "v6Automatic")
            };
            if (!string.IsNullOrWhiteSpace(state.Name)) snapshot.Adapters.Add(state);
        }

        private static int? NullableInt(JsonElement node, string property)
        {
            return node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : (int?)null;
        }

        private static bool IsAutomatic(JsonElement node, string property)
        {
            return node.TryGetProperty(property, out var value) &&
                   string.Equals(value.GetString(), "Enabled", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RestoreConfiguration(NetworkConfigurationSnapshot snapshot, out string message)
        {
            if (snapshot == null || snapshot.Adapters.Count == 0)
            {
                message = "没有可用于恢复的网络配置快照。";
                return false;
            }

            var script = new StringBuilder();
            script.AppendLine("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8");
            script.AppendLine("$ErrorActionPreference='Stop'");
            foreach (var state in snapshot.Adapters)
            {
                script.AppendLine("$adapter=Get-NetAdapter | Where-Object { $_.Name -eq '" + Esc(state.Name) + "' } | Select-Object -First 1");
                script.AppendLine("if($null -eq $adapter){ throw '找不到要恢复的网卡：" + Esc(state.Name) + "' }");
                script.AppendLine("if($adapter.Status -eq 'Disabled'){ $adapter | Enable-NetAdapter -Confirm:$false }");
                AppendMetricRestore(script, "$adapter.ifIndex", "IPv4", state.V4Automatic, state.V4Metric);
                AppendMetricRestore(script, "$adapter.ifIndex", "IPv6", state.V6Automatic, state.V6Metric);
                if (!state.Enabled)
                    script.AppendLine("$adapter | Disable-NetAdapter -Confirm:$false");
            }

            var execution = RunPowerShell(script.ToString());
            if (!execution.Success)
            {
                message = execution.TimedOut ? "恢复操作超时。" : ShortError(execution.StandardError);
                AppLogger.Error("恢复原网络配置失败：" + message);
                return false;
            }

            message = "已恢复切换前的网卡状态和跃点数。";
            return true;
        }

        private static void AppendMetricRestore(
            StringBuilder script, string interfaceIndex, string family, bool automatic, int? metric)
        {
            if (!automatic && !metric.HasValue) return;
            var variable = "$restore" + family;
            script.AppendLine(variable + "=Get-NetIPInterface -InterfaceIndex " + interfaceIndex +
                              " -AddressFamily " + family + " -ErrorAction SilentlyContinue | Select-Object -First 1");
            if (automatic)
                script.AppendLine("if(" + variable + "){ " + variable + " | Set-NetIPInterface -AutomaticMetric Enabled }");
            else
                script.AppendLine("if(" + variable + "){ " + variable + " | Set-NetIPInterface -AutomaticMetric Disabled -InterfaceMetric " + metric.Value + " }");
        }

        #endregion

        #region 跃点数读取

        /// <summary>通过 PowerShell 读取每张网卡的接口跃点数与默认网关路由跃点数。</summary>
        public static bool RefreshMetrics(List<AdapterInfo> list)
        {
            const string script =
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" +
                "$ErrorActionPreference='Stop'\n" +
                "$routes=@(Get-NetRoute -DestinationPrefix '0.0.0.0/0')\n" +
                "$rm=@{}\n" +
                "foreach($r in $routes){\n" +
                "  $key=[string]$r.InterfaceIndex; $value=[int]$r.RouteMetric\n" +
                "  if(!$rm.ContainsKey($key) -or $value -lt $rm[$key]){ $rm[$key]=$value }\n" +
                "}\n" +
                "$out=@()\n" +
                "foreach($i in @(Get-NetIPInterface -AddressFamily IPv4)){\n" +
                "  $m=0\n" +
                "  if($rm.ContainsKey([string]$i.InterfaceIndex)){ $m=$rm[[string]$i.InterfaceIndex] }\n" +
                "  $out+=[pscustomobject]@{ idx=[int]$i.InterfaceIndex; metric=[int]$i.InterfaceMetric; rmetric=$m }\n" +
                "}\n" +
                "$out | ConvertTo-Json -Compress";

            var execution = RunPowerShell(script);
            var raw = execution.StandardOutput;
            if (!execution.Success || string.IsNullOrWhiteSpace(raw))
            {
                AppLogger.WarningThrottled(
                    "refresh-metrics",
                    "读取网卡跃点数失败：" + ShortError(execution.StandardError),
                    TimeSpan.FromMinutes(1));
                return false;
            }

            var map = new Dictionary<int, int[]>();
            try
            {
                using var doc = JsonDocument.Parse(raw.Trim());
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray()) AddNode(el, map);
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddNode(doc.RootElement, map);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("解析网卡跃点数信息失败。", ex);
                return false;
            }

            foreach (var a in list)
            {
                if (a.IfIndex >= 0 && map.TryGetValue(a.IfIndex, out var v))
                {
                    a.Metric = v[0];
                    a.RouteMetric = v[1];
                }
            }
            return true;
        }

        private static void AddNode(JsonElement el, Dictionary<int, int[]> map)
        {
            if (!el.TryGetProperty("idx", out var idxProp)) return;
            if (idxProp.ValueKind != JsonValueKind.Number) return;
            int idx = idxProp.GetInt32();
            int metric = el.TryGetProperty("metric", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : -1;
            int rmetric = el.TryGetProperty("rmetric", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : -1;
            map[idx] = new[] { metric, rmetric };
        }

        #endregion

        #region 工具

        private static string Esc(string name) => (name ?? "").Replace("'", "''");

        private sealed class PowerShellResult
        {
            public bool Success { get; set; }
            public bool TimedOut { get; set; }
            public int ExitCode { get; set; } = -1;
            public string StandardOutput { get; set; } = "";
            public string StandardError { get; set; } = "";
        }

        private static PowerShellResult RunPowerShell(string script)
        {
            try
            {
                var powerShellPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo(powerShellPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);

                using var p = Process.Start(psi);
                if (p == null)
                    return new PowerShellResult { StandardError = "无法启动 PowerShell。" };

                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(true); } catch { }
                    if (!p.WaitForExit(5000))
                    {
                        AppLogger.WarningThrottled(
                            "powershell-timeout",
                            "PowerShell 执行超时且无法终止。",
                            TimeSpan.FromMinutes(1));
                        return new PowerShellResult
                        {
                            TimedOut = true,
                            StandardError = "PowerShell 超时且无法终止。"
                        };
                    }

                    return new PowerShellResult
                    {
                        TimedOut = true,
                        ExitCode = p.ExitCode,
                        StandardOutput = stdout.GetAwaiter().GetResult(),
                        StandardError = stderr.GetAwaiter().GetResult()
                    };
                }

                var output = stdout.GetAwaiter().GetResult();
                var error = stderr.GetAwaiter().GetResult();
                return new PowerShellResult
                {
                    Success = p.ExitCode == 0,
                    ExitCode = p.ExitCode,
                    StandardOutput = output,
                    StandardError = error
                };
            }
            catch (Exception ex)
            {
                AppLogger.Error("启动或执行 PowerShell 时发生异常。", ex);
                return new PowerShellResult { StandardError = ex.Message };
            }
        }

        private static string ShortError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "未返回具体原因。";
            var line = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return "未返回具体原因。";
            return line.Length <= 180 ? line : line.Substring(0, 180) + "…";
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int unit = 0;
            double v = bytesPerSecond;
            while (v >= 1024 && unit < units.Length - 1)
            {
                v /= 1024;
                unit++;
            }
            return v >= 100 || unit == 0
                ? v.ToString("0") + " " + units[unit]
                : v.ToString("0.0") + " " + units[unit];
        }

        public static string FormatLinkSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0) return "未知";
            var g = bitsPerSecond / 1000000000.0;
            if (g >= 1) return g.ToString("0.0") + " Gbps";
            var m = bitsPerSecond / 1000000.0;
            return m >= 1 ? m.ToString("0") + " Mbps" : (bitsPerSecond / 1000.0).ToString("0") + " Kbps";
        }

        #endregion
    }
}

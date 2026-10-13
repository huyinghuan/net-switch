using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace NetSwitcher
{
    /// <summary>一个可重复应用到指定网卡的静态 IPv4 配置。</summary>
    public sealed class IpProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string AdapterId { get; set; } = "";
        public string AdapterName { get; set; } = "";
        public string Address { get; set; } = "";
        public string SubnetMask { get; set; } = "255.255.255.0";
        public string Gateway { get; set; } = "";
        public List<string> DnsServers { get; set; } = new List<string>();
        public List<IpRouteProfile> Routes { get; set; } = new List<IpRouteProfile>();
        public bool ShowInTray { get; set; } = true;

        public IpProfile Clone() => new IpProfile
        {
            Id = Id,
            Name = Name,
            AdapterId = AdapterId,
            AdapterName = AdapterName,
            Address = Address,
            SubnetMask = SubnetMask,
            Gateway = Gateway,
            DnsServers = (DnsServers ?? new List<string>()).ToList(),
            Routes = (Routes ?? new List<IpRouteProfile>()).Select(route => route.Clone()).ToList(),
            ShowInTray = ShowInTray
        };

        public bool TryValidate(out string error)
        {
            error = "";
            Name = (Name ?? "").Trim();
            AdapterId = (AdapterId ?? "").Trim();
            AdapterName = (AdapterName ?? "").Trim();
            Address = (Address ?? "").Trim();
            SubnetMask = (SubnetMask ?? "").Trim();
            Gateway = (Gateway ?? "").Trim();
            DnsServers ??= new List<string>();
            Routes ??= new List<IpRouteProfile>();

            if (string.IsNullOrWhiteSpace(Name))
            {
                error = "请输入配置名称。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(AdapterId) && string.IsNullOrWhiteSpace(AdapterName))
            {
                error = "请选择要应用配置的网卡。";
                return false;
            }
            if (!IsIpv4(Address))
            {
                error = "IPv4 地址格式不正确。";
                return false;
            }
            if (!TryMaskToPrefixLength(SubnetMask, out _))
            {
                error = "子网掩码格式不正确，或不是连续的子网掩码。";
                return false;
            }
            if (!string.IsNullOrEmpty(Gateway) && !IsIpv4(Gateway))
            {
                error = "默认网关格式不正确。";
                return false;
            }

            DnsServers = DnsServers
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (DnsServers.Any(value => !IsIpv4(value)))
            {
                error = "DNS 服务器地址格式不正确。";
                return false;
            }

            foreach (var route in Routes)
            {
                if (!route.TryValidate(out error)) return false;
            }
            return true;
        }

        public static bool TryMaskToPrefixLength(string mask, out int prefixLength)
        {
            prefixLength = 0;
            if (!IPAddress.TryParse(mask, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            var value = address.GetAddressBytes();
            var foundZero = false;
            foreach (var octet in value)
            {
                for (var bit = 7; bit >= 0; bit--)
                {
                    var isOne = (octet & (1 << bit)) != 0;
                    if (foundZero && isOne) return false;
                    if (isOne) prefixLength++;
                    else foundZero = true;
                }
            }
            return prefixLength > 0 && prefixLength <= 32;
        }

        private static bool IsIpv4(string value) =>
            IPAddress.TryParse(value, out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    public sealed class IpRouteProfile
    {
        public string DestinationPrefix { get; set; } = "";
        public string Gateway { get; set; } = "";
        public int Metric { get; set; } = 10;

        public IpRouteProfile Clone() => new IpRouteProfile
        {
            DestinationPrefix = DestinationPrefix,
            Gateway = Gateway,
            Metric = Metric
        };

        public bool TryValidate(out string error)
        {
            error = "";
            DestinationPrefix = (DestinationPrefix ?? "").Trim();
            Gateway = (Gateway ?? "").Trim();
            if (!TryParsePrefix(DestinationPrefix))
            {
                error = "静态路由目标格式不正确，请使用类似 10.20.0.0/16 的格式。";
                return false;
            }
            if (string.Equals(DestinationPrefix, "0.0.0.0/0", StringComparison.OrdinalIgnoreCase))
            {
                error = "默认路由请填写在“默认网关”中，不要添加为静态路由。";
                return false;
            }
            if (!IPAddress.TryParse(Gateway, out var gateway) ||
                gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                error = "静态路由网关格式不正确。";
                return false;
            }
            if (Metric < 1 || Metric > 9999)
            {
                error = "静态路由跃点数应在 1 到 9999 之间。";
                return false;
            }
            return true;
        }

        private static bool TryParsePrefix(string value)
        {
            var parts = (value ?? "").Split('/');
            return parts.Length == 2 &&
                   IPAddress.TryParse(parts[0], out var address) &&
                   address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                   int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= 32;
        }
    }
}

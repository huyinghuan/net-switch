using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NetSwitcher
{
    internal static class ConnectivityService
    {
        private static readonly string[] DnsHosts = { "www.baidu.com", "www.msftconnecttest.com" };
        private static readonly string[] HttpsUrls =
        {
            "https://www.baidu.com/",
            "https://www.msftconnecttest.com/connecttest.txt"
        };

        public static async Task<List<OperationStep>> CheckAsync(AdapterInfo target)
        {
            var steps = new List<OperationStep>();

            var gateway = await CheckGatewayAsync(target?.Gateway);
            steps.Add(new OperationStep
            {
                Name = "默认网关",
                Success = gateway.success,
                Detail = gateway.detail
            });

            var dns = await CheckDnsAsync();
            steps.Add(new OperationStep
            {
                Name = "DNS 解析",
                Success = dns.success,
                Detail = dns.detail
            });

            var https = await CheckHttpsAsync(target?.Ip);
            steps.Add(new OperationStep
            {
                Name = "公网 HTTPS",
                Success = https.success,
                Detail = https.detail
            });

            return steps;
        }

        private static async Task<(bool success, string detail)> CheckGatewayAsync(string gateway)
        {
            if (string.IsNullOrWhiteSpace(gateway)) return (false, "没有默认网关");
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(gateway, 1800);
                return reply.Status == IPStatus.Success
                    ? (true, gateway + "，" + reply.RoundtripTime + " ms")
                    : (false, gateway + "，" + reply.Status);
            }
            catch (Exception ex)
            {
                return (false, gateway + "，" + ex.Message);
            }
        }

        private static async Task<(bool success, string detail)> CheckDnsAsync()
        {
            foreach (var host in DnsHosts)
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(3));
                    var address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                  ?? addresses.FirstOrDefault();
                    if (address != null) return (true, host + " → " + address);
                }
                catch
                {
                    // 尝试下一个公共域名。
                }
            }
            return (false, "公共域名解析失败");
        }

        private static async Task<(bool success, string detail)> CheckHttpsAsync(string localIp)
        {
            if (!IPAddress.TryParse(localIp, out var localAddress))
                return (false, "目标网卡没有可绑定的本地 IP");

            using var client = CreateClient(localAddress);
            var checks = HttpsUrls.Select(url => CheckUrlAsync(client, url)).ToArray();
            var results = await Task.WhenAll(checks);
            var success = results.FirstOrDefault(r => r.success);
            if (success.success) return success;
            return (false, string.Join("；", results.Select(r => r.detail)));
        }

        private static async Task<(bool success, string detail)> CheckUrlAsync(HttpClient client, string url)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                return response.IsSuccessStatusCode
                    ? (true, new Uri(url).Host + "，HTTP " + (int)response.StatusCode)
                    : (false, new Uri(url).Host + "，HTTP " + (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                return (false, new Uri(url).Host + "，" + ex.GetType().Name);
            }
        }

        private static HttpClient CreateClient(IPAddress localAddress)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(localAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        socket.Bind(new IPEndPoint(localAddress, 0));
                        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetSwitcher/1.0");
            return client;
        }
    }
}

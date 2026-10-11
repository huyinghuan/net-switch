using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NetSwitcher
{
    internal static class DiagnosticsService
    {
        public static string BuildReport(IEnumerable<AdapterInfo> adapters, AdapterInfo currentExit, SwitchResult lastOperation)
        {
            var sb = new StringBuilder();
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
            sb.AppendLine("NetSwitcher 诊断信息");
            sb.AppendLine("生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine("软件版本：" + version);
            sb.AppendLine("操作系统：" + RuntimeInformation.OSDescription);
            sb.AppendLine("进程架构：" + RuntimeInformation.ProcessArchitecture);
            sb.AppendLine("日志文件：" + AppLogger.LogPath);
            sb.AppendLine("当前出口：" + (currentExit == null ? "未识别" : currentExit.Name + " / " + currentExit.Id));
            sb.AppendLine();
            sb.AppendLine("网卡：");

            foreach (var a in adapters ?? Enumerable.Empty<AdapterInfo>())
            {
                sb.AppendLine($"- {a.Name} [{a.Id}]");
                sb.AppendLine($"  类型={a.KindText} 状态={a.Status} IfIndex={a.IfIndex} IP={a.Ip} 网关={a.Gateway}");
                sb.AppendLine($"  接口跃点数={a.Metric} 路由跃点数={a.RouteMetric} 虚拟网卡={NetworkService.LooksVirtual(a)}");
            }

            if (lastOperation != null)
            {
                sb.AppendLine();
                sb.AppendLine("最近一次操作：");
                sb.AppendLine(lastOperation.ToDetailText());
            }

            return sb.ToString().TrimEnd();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NetSwitcher
{
    public sealed class OperationStep
    {
        public string Name { get; set; } = "";
        public bool Success { get; set; }
        public string Detail { get; set; } = "";

        public override string ToString() =>
            (Success ? "✓ " : "✗ ") + Name + (string.IsNullOrWhiteSpace(Detail) ? "" : "：" + Detail);
    }

    public sealed class SwitchResult
    {
        public bool Success { get; set; }
        public bool RolledBack { get; set; }
        public string Message { get; set; } = "";
        public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.Now;
        public List<OperationStep> Steps { get; } = new List<OperationStep>();

        public string ToDetailText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Message);
            sb.AppendLine("时间：" + CompletedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            if (RolledBack) sb.AppendLine("安全保护：已尝试恢复切换前的网络配置。");
            sb.AppendLine();
            foreach (var step in Steps) sb.AppendLine(step.ToString());
            return sb.ToString().TrimEnd();
        }

        public string FailedStepSummary => string.Join("；",
            Steps.Where(s => !s.Success).Select(s => s.Name + "：" + s.Detail));
    }

    internal sealed class AdapterConfigurationState
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public int? V4Metric { get; set; }
        public bool V4Automatic { get; set; }
        public int? V6Metric { get; set; }
        public bool V6Automatic { get; set; }
    }

    internal sealed class NetworkConfigurationSnapshot
    {
        public List<AdapterConfigurationState> Adapters { get; } = new List<AdapterConfigurationState>();
    }
}

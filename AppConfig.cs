using System;
using System.IO;
using System.Text.Json;

namespace NetSwitcher
{
    /// <summary>
    /// 本地持久化配置：记住用户选择的网卡、上次的出口选择以及切换偏好。
    /// 存放位置：%LOCALAPPDATA%\NetSwitcher\config.json
    /// </summary>
    public sealed class AppConfig
    {
        public string EthernetAdapter { get; set; } = "";   // 用户指定的有线网卡名
        public string WifiAdapter { get; set; } = "";       // 用户指定的无线网卡名
        public bool HardIsolate { get; set; } = false;      // 是否在切换时禁用另一张网卡
        public string LastExit { get; set; } = "";          // 上次设为出口的网卡名
        public bool RestoreOnStartup { get; set; } = true;  // 启动时恢复上次的出口

        private static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetSwitcher");

        private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch
            {
                // 配置文件损坏时忽略，退回默认值
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, options));
            }
            catch
            {
                // 保存失败不影响主功能
            }
        }
    }
}

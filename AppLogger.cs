using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NetSwitcher
{
    /// <summary>
    /// 进程级日志。日志固定写在可执行文件目录，写入失败时不会影响主程序。
    /// </summary>
    internal static class AppLogger
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, DateTimeOffset> LastThrottledEntries =
            new Dictionary<string, DateTimeOffset>();

        public static string LogPath => Path.Combine(AppContext.BaseDirectory, "NetSwitcher.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warning(string message) => Write("WARN", message, null);

        public static void WarningThrottled(string key, string message, TimeSpan interval)
        {
            var now = DateTimeOffset.Now;
            lock (SyncRoot)
            {
                if (LastThrottledEntries.TryGetValue(key, out var last) && now - last < interval)
                    return;
                LastThrottledEntries[key] = now;
            }
            Write("WARN", message, null);
        }

        public static void Error(string message, Exception exception = null) =>
            Write("ERROR", message, exception);

        public static void Fatal(string message, Exception exception = null) =>
            Write("FATAL", message, exception);

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                var entry = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ")
                    .Append('[').Append(level).Append("] ")
                    .Append("[Thread ").Append(Environment.CurrentManagedThreadId).Append("] ")
                    .AppendLine(message ?? "")
                    .ToString();

                if (exception != null)
                    entry += exception + Environment.NewLine;

                lock (SyncRoot)
                {
                    File.AppendAllText(LogPath, entry, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志系统自身失败时不能再次触发异常。
            }
        }
    }
}

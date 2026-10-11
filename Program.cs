using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                AppLogger.Fatal("UI 线程发生未处理异常。", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                AppLogger.Fatal(
                    e.IsTerminating ? "进程即将因未处理异常终止。" : "后台线程发生未处理异常。",
                    ex ?? new Exception(e.ExceptionObject?.ToString() ?? "未知异常"));
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                AppLogger.Error("后台任务异常未被观察。", e.Exception);
                e.SetObserved();
            };

            try
            {
                AppLogger.Info("NetSwitcher 启动。日志文件：" + AppLogger.LogPath);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                AppLogger.Fatal("主程序发生致命异常。", ex);
                MessageBox.Show(
                    "程序发生错误并即将退出。详细信息已写入：\n" + AppLogger.LogPath,
                    "NetSwitcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                AppLogger.Info("NetSwitcher 退出。");
            }
        }
    }
}

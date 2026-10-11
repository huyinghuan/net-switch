using System;
using System.IO;
using System.Windows.Forms;

namespace NetSwitcher
{
    internal static class Program
    {
        private static string LogPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetSwitcher", "error.log");

        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);

            Application.Run(new MainForm());
        }

        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
            }
            catch
            {
                // 日志失败时不再抛出
            }
        }
    }
}

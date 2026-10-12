using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NetSwitcher
{
    /// <summary>
    /// 程序图标与圆角路径的工具类。
    /// </summary>
    public static class IconFactory
    {
        public static readonly Color WiredColor = ColorTranslator.FromHtml("#4A9EFF");
        public static readonly Color WifiColor = ColorTranslator.FromHtml("#A78BFA");

        /// <summary>
        /// 从当前 EXE 读取构建时嵌入的多尺寸图标；读取失败时回退到运行时绘制的简易图标。
        /// 返回独立克隆，调用方负责释放。
        /// </summary>
        public static Icon LoadApplicationIcon()
        {
            try
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    using var extracted = Icon.ExtractAssociatedIcon(executable);
                    if (extracted != null) return (Icon)extracted.Clone();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("读取嵌入式应用图标失败，将使用备用图标：" + ex.Message);
            }
            return Create(WiredColor);
        }

        public static Icon Create(Color accent)
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                // 圆角底板
                using (var path = RoundedRect(new Rectangle(2, 2, 60, 60), 14))
                using (var brush = new SolidBrush(accent))
                {
                    g.FillPath(brush, path);
                }

                // 上下两个箭头，表示"来回切换"
                using (var white = new SolidBrush(Color.FromArgb(245, 248, 252)))
                {
                    g.FillPolygon(white, new[]
                    {
                        new Point(32, 11), new Point(47, 30), new Point(17, 30)
                    });
                    g.FillPolygon(white, new[]
                    {
                        new Point(32, 53), new Point(17, 34), new Point(47, 34)
                    });
                }
            }
            var handle = bmp.GetHicon();
            try
            {
                using var borrowed = Icon.FromHandle(handle);
                return (Icon)borrowed.Clone();
            }
            finally
            {
                if (!DestroyIcon(handle))
                    AppLogger.Warning("释放应用图标句柄失败，Win32 错误码：" + Marshal.GetLastWin32Error());
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.StartFigure();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

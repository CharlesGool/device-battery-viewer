using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryViewer
{
    /// <summary>界面共用: DPI 缩放、配色、托盘数字图标和程序图标的绘制。</summary>
    internal static class Ui
    {
        public static readonly float DpiScale;
        public static readonly Font Font = new Font("Microsoft YaHei UI", 9f);
        public static readonly Color Back = Color.FromArgb(243, 243, 243);
        public static readonly Color Card = Color.White;
        public static readonly Color Line = Color.FromArgb(229, 229, 229);
        public static readonly Color Sub = Color.FromArgb(96, 96, 96);
        public static readonly Color Red = Color.FromArgb(196, 43, 28);
        public static readonly Color Green = Color.FromArgb(15, 123, 15);

        static Ui()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) DpiScale = g.DpiX / 96f;
        }

        public static int S(int v) { return (int)Math.Round(v * DpiScale); }
        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
        public static Padding Pd(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }
        public static Padding Pd(int all) { return Pd(all, all, all, all); }

        public static string KindName(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Mouse: return "鼠标";
                case DeviceKind.Keyboard: return "键盘";
                case DeviceKind.Headset: return "耳机";
                case DeviceKind.Gamepad: return "手柄";
                case DeviceKind.Speaker: return "音箱";
                default: return "设备";
            }
        }

        /// <summary>托盘图标上的颜色(深色任务栏上要亮)。</summary>
        public static Color TrayColor(int? level, bool charging, bool online)
        {
            if (!online || !level.HasValue) return Color.FromArgb(158, 158, 158);
            if (charging) return Color.FromArgb(79, 195, 247);
            return level >= 60 ? Color.FromArgb(76, 217, 100) : level >= 30 ? Color.FromArgb(255, 193, 7) : Color.FromArgb(244, 67, 54);
        }

        /// <summary>窗口里白底上的文字颜色。</summary>
        public static Color TextColor(int? level, bool charging, bool online)
        {
            if (!online || !level.HasValue) return Color.FromArgb(128, 128, 128);
            if (charging) return Color.FromArgb(0, 103, 192);
            return level >= 60 ? Green : level >= 30 ? Color.FromArgb(157, 93, 0) : Red;
        }

        // ---------------------------------------------------------------- 图标
        public static int TrayIconSize { get { return SystemInformation.SmallIconSize.Width; } }

        /// <summary>把电量数字画成托盘图标。underline=true 时底部画一条短横线(充电中)。</summary>
        public static Icon NumberIcon(string text, Color color, bool underline)
        {
            int size = TrayIconSize;
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(color))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var family = new FontFamily("Segoe UI"))
                        path.AddString(text, family, (int)FontStyle.Bold, 100f, Point.Empty, StringFormat.GenericTypographic);
                    // 把文字轮廓缩放到图标里, 宽不超过图标, 高约占 80%
                    var b = path.GetBounds();
                    float boxH = size * (underline ? 0.72f : 0.82f);
                    float k = Math.Min(size / b.Width, boxH / b.Height);
                    float drawW = b.Width * k, drawH = b.Height * k;
                    float top = underline ? (size * 0.86f - drawH) / 2 : (size - drawH) / 2;
                    using (var m = new Matrix())
                    {
                        m.Translate((size - drawW) / 2, top);
                        m.Scale(k, k);
                        m.Translate(-b.X, -b.Y);
                        path.Transform(m);
                    }
                    g.FillPath(brush, path);
                    if (underline)
                        g.FillRectangle(brush, size / 4f, size - Math.Max(1.5f, size / 10f), size / 2f, Math.Max(1.5f, size / 10f));
                }
                return ToIcon(bmp);
            }
        }

        /// <summary>程序图标: 一节电池。</summary>
        public static Icon AppIcon(int size)
        {
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float u = size / 16f;
                    var body = new RectangleF(1 * u, 4 * u, 12.5f * u, 8 * u);
                    using (var pen = new Pen(Color.FromArgb(32, 32, 32), Math.Max(1f, 1.2f * u)))
                    using (var rr = RoundRect(body, 1.5f * u))
                        g.DrawPath(pen, rr);
                    using (var nub = new SolidBrush(Color.FromArgb(32, 32, 32)))
                        g.FillRectangle(nub, 13.8f * u, 6.5f * u, 1.4f * u, 3f * u);
                    using (var fill = new SolidBrush(Color.FromArgb(16, 137, 62)))
                    using (var rr = RoundRect(new RectangleF(2.6f * u, 5.6f * u, 7.2f * u, 4.8f * u), 0.6f * u))
                        g.FillPath(fill, rr);
                }
                return ToIcon(bmp);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Icon ToIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BatteryViewer
{
    /// <summary>
    /// 入口。
    ///   DeviceBatteryViewer.exe              打开面板, 常驻托盘
    ///   DeviceBatteryViewer.exe --tray       只常驻托盘(开机自启用)
    ///   DeviceBatteryViewer.exe --once       读一次电量, 打印后退出
    ///   DeviceBatteryViewer.exe --install    复制到 %LOCALAPPDATA%\Programs\DeviceBatteryViewer 并设为开机自启
    ///   DeviceBatteryViewer.exe --uninstall  取消开机自启并删除本机副本
    /// </summary>
    internal static class Program
    {
        public static readonly int ShowMessage = RegisterWindowMessage("DeviceBatteryViewer.Show");
        public static readonly int QuitMessage = RegisterWindowMessage("DeviceBatteryViewer.Quit");
        private const string MutexName = @"Local\" + Settings.AppName;
        private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Settings.AppName);

        [STAThread]
        private static int Main(string[] args)
        {
            var flags = new HashSet<string>(args.Select(a => a.ToLowerInvariant()));
            if (flags.Contains("--once")) return Once();
            if (flags.Contains("--hid")) return ListHid();
            if (flags.Contains("--install")) return Install();
            if (flags.Contains("--uninstall")) return Uninstall();

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // 已经在运行: 让它把面板显示出来
                    PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new AppController(Settings.Load(), !flags.Contains("--tray")));
            }
            return 0;
        }

        // ---------------------------------------------------------------- 命令行
        private static void UseConsole()
        {
            AttachConsole(-1);   // 从命令行启动时把输出接到那个控制台上
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }
        }

        private static int Once()
        {
            UseConsole();
            var poller = new Poller(Settings.Load());
            poller.RunOnce();
            List<DeviceState> devices;
            List<PluginStatus> plugins;
            poller.Snapshot(out devices, out plugins);
            foreach (var d in devices)
                Console.WriteLine("{0} {1,5} {2}[{3}] {4}", Pad(d.Name, 28), d.Level.HasValue ? d.Level + "%" : "--",
                    Pad(d.Charging ? "充电中" : d.Online ? "" : "离线", 8), d.PluginName, d.Detail);
            if (devices.Count == 0) Console.WriteLine("没有发现设备");
            foreach (var p in plugins.Where(p => p.Error.Length > 0))
                Console.WriteLine("\n插件 {0} 出错:\n{1}", p.Id, p.Error);
            poller.Stop();
            return 0;
        }

        /// <summary>列出所有 HID 接口, 写新插件时用来找 VID / PID / usage page。</summary>
        private static int ListHid()
        {
            UseConsole();
            foreach (var i in Hid.Enumerate().OrderBy(i => i.Vid).ThenBy(i => i.Pid).ThenBy(i => i.UsagePage).ThenBy(i => i.Usage))
                Console.WriteLine(i);
            return 0;
        }

        /// <summary>按显示宽度补空格(中文占两格)。</summary>
        private static string Pad(string s, int width)
        {
            int w = s.Sum(c => c >= 0x1100 ? 2 : 1);
            return s + new string(' ', Math.Max(0, width - w));
        }

        private static int Install()
        {
            UseConsole();
            StopRunning();
            Directory.CreateDirectory(InstallDir);
            // 清掉旧的 Python 版(只认我们自己装进去的那几个文件)
            if (File.Exists(Path.Combine(InstallDir, "main.py")))
            {
                foreach (var dir in new[] { "core", "winapi", "ui", "plugins" })
                    if (Directory.Exists(Path.Combine(InstallDir, dir))) Directory.Delete(Path.Combine(InstallDir, dir), true);
                foreach (var file in new[] { "main.py", "README.md" })
                    if (File.Exists(Path.Combine(InstallDir, file))) File.Delete(Path.Combine(InstallDir, file));
            }
            string target = Path.Combine(InstallDir, Settings.AppName + ".exe");
            if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                File.Copy(Application.ExecutablePath, target, true);
            Settings.SetAutoStart(true, target);
            Process.Start(target);
            Console.WriteLine("已安装到 " + target + ", 并设为开机自启");
            return 0;
        }

        private static int Uninstall()
        {
            UseConsole();
            StopRunning();
            Settings.SetAutoStart(false, null);
            string target = Path.Combine(InstallDir, Settings.AppName + ".exe");
            try
            {
                if (File.Exists(target)) File.Delete(target);
                if (Directory.Exists(InstallDir) && !Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
                Console.WriteLine("已取消开机自启并删除 " + InstallDir + "; 配置和插件留在 " + Settings.DataDir);
            }
            catch (Exception e)
            {
                Console.WriteLine("已取消开机自启, 但删除 " + target + " 失败: " + e.Message);
            }
            return 0;
        }

        /// <summary>让正在运行的实例(包括旧的 Python 版)退出, 最多等 5 秒。</summary>
        private static void StopRunning()
        {
            PostMessage((IntPtr)0xFFFF, QuitMessage, IntPtr.Zero, IntPtr.Zero);
            IntPtr legacy = FindWindow("DeviceBatteryViewerTray", null);
            if (legacy != IntPtr.Zero) PostMessage(legacy, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
            for (int i = 0; i < 50; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(MutexName, out m)) return;
                m.Dispose();
                Thread.Sleep(100);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    }
}

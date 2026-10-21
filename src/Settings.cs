using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace BatteryViewer
{
    /// <summary>配置文件 %APPDATA%\DeviceBatteryViewer\config.json, 以及开机自启、数据目录。</summary>
    public class Settings
    {
        public const string AppName = "DeviceBatteryViewer";
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        public static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
        public static readonly string StatePath = Path.Combine(DataDir, "devices.json");   // 上次读到的设备和电量
        public static readonly string PluginDir = Path.Combine(DataDir, "plugins");
        public static readonly int[] Intervals = { 30, 60, 120, 300, 600, 1800 };

        public int Interval = 60;                                   // 刷新间隔, 秒
        public List<string> DisabledPlugins = new List<string>();   // 停用的插件 id
        public List<string> HiddenDevices = new List<string>();     // 不显示托盘图标的设备 id("插件id:key")

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var d = Json.Parse(File.ReadAllText(ConfigPath)) as Dictionary<string, object>;
                if (d == null) return s;
                object v;
                if (d.TryGetValue("interval", out v) && v is int) s.Interval = Math.Max(10, (int)v);
                if (d.TryGetValue("disabled_plugins", out v)) s.DisabledPlugins = Json.Strings(v);
                if (d.TryGetValue("hidden_devices", out v)) s.HiddenDevices = Json.Strings(v);
            }
            catch (Exception) { }
            return s;
        }

        public void Save()
        {
            Json.WriteFile(ConfigPath, new Dictionary<string, object> {
                { "interval", Interval }, { "disabled_plugins", DisabledPlugins }, { "hidden_devices", HiddenDevices },
            });
        }

        // ---------------------------------------------------------------- 开机自启(HKCU\...\Run)
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool AutoStart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
        }

        public static void SetAutoStart(bool on, string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + exePath + "\" --tray");
                else if (k.GetValue(AppName) != null) k.DeleteValue(AppName);
            }
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static object Parse(string text) { return Serializer.DeserializeObject(text); }

        public static List<string> Strings(object v)
        {
            var arr = v as object[];
            return arr == null ? new List<string>() : arr.Select(o => Convert.ToString(o)).ToList();
        }

        public static void WriteFile(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serializer.Serialize(data));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatteryViewer
{
    /// <summary>程序主体: 托盘图标(每台设备一个)、托盘菜单、两个窗口和后台轮询之间的协调。只在界面线程上运行。</summary>
    internal class AppController : ApplicationContext
    {
        public readonly Settings Settings;
        public bool Exiting { get; private set; }

        private readonly Poller _poller;
        private readonly MainForm _main;
        private PluginsForm _plugins;
        private readonly ContextMenu _menu = new ContextMenu();
        private readonly Dictionary<string, TrayEntry> _icons = new Dictionary<string, TrayEntry>();
        private volatile bool _updatePending;

        private class TrayEntry
        {
            public NotifyIcon Icon;
            public string Look = "";
        }

        public AppController(Settings settings, bool showWindow)
        {
            Settings = settings;
            _main = new MainForm(this);
            var unused = _main.Handle;   // 先建好窗口句柄: 后台线程的通知靠它转到界面线程, 单实例消息也发给它
            _menu.Popup += (s, e) => BuildMenu();

            _poller = new Poller(settings);
            _poller.Changed += () =>
            {
                // 后台线程上可能连续触发很多次, 合并成一次界面更新
                if (_updatePending || Exiting) return;
                _updatePending = true;
                try { _main.BeginInvoke(new Action(UpdateAll)); }
                catch (InvalidOperationException) { }
            };
            UpdateAll();
            _poller.Start();
            if (showWindow) ShowMain();
        }

        // ---------------------------------------------------------------- 给窗口用的操作
        public void ShowMain()
        {
            _main.ShowNearTray();
            UpdateAll();   // 窗口隐藏时不更新内容, 所以要在显示之后
        }

        public void ShowPlugins()
        {
            if (_plugins == null || _plugins.IsDisposed) _plugins = new PluginsForm(this);
            _plugins.ShowWindow();
            UpdateAll();
        }

        public void RefreshNow() { _poller.RefreshNow(); }

        public void SetInterval(int seconds)
        {
            Settings.Interval = seconds;
            Settings.Save();
            _poller.SetInterval(seconds);
        }

        public void SetAutoStart(bool on)
        {
            Settings.SetAutoStart(on, Application.ExecutablePath);
        }

        public void SetDeviceHidden(string id, bool hidden)
        {
            Settings.HiddenDevices.Remove(id);
            if (hidden) Settings.HiddenDevices.Add(id);
            Settings.Save();
            UpdateAll();
        }

        public void SetPluginEnabled(string id, bool on)
        {
            Settings.DisabledPlugins.Remove(id);
            if (!on) Settings.DisabledPlugins.Add(id);
            Settings.Save();
            ReloadPlugins();
        }

        public void ReloadPlugins() { _poller.RequestReload(Settings.DisabledPlugins); }

        public void Exit()
        {
            if (Exiting) return;
            Exiting = true;
            foreach (var e in _icons.Values) { e.Icon.Visible = false; e.Icon.Dispose(); }
            _icons.Clear();
            _poller.Stop();
            if (_plugins != null) _plugins.Close();
            _main.Close();
            ExitThread();
        }

        // ---------------------------------------------------------------- 刷新界面
        private void UpdateAll()
        {
            _updatePending = false;
            if (Exiting) return;
            List<DeviceState> devices;
            List<PluginStatus> plugins;
            _poller.Snapshot(out devices, out plugins);
            UpdateTray(devices);
            bool busy = _poller.Scanning || _poller.Loading;
            if (_main.Visible) _main.UpdateView(devices, plugins, busy, Settings);
            if (_plugins != null && !_plugins.IsDisposed && _plugins.Visible) _plugins.UpdateView(plugins, _poller.Loading, _poller.Scanning);
        }

        private void UpdateTray(List<DeviceState> devices)
        {
            var hidden = new HashSet<string>(Settings.HiddenDevices);
            var wanted = new List<KeyValuePair<string, string[]>>();   // id -> [文字, 提示, 颜色, 充电]
            foreach (var d in devices.Where(d => !hidden.Contains(d.Id)))
            {
                string state = d.Charging ? " 充电中" : !d.Online ? " 离线" : "";
                string when = d.Updated > 0 ? new DateTime(1970, 1, 1).AddSeconds(d.Updated).ToLocalTime().ToString("HH:mm") : "未读到";
                string tip = d.Name + ": " + (d.Level.HasValue ? d.Level + "%" : "--") + state + "\n" + d.PluginName + " · " + when;
                var c = Ui.TrayColor(d.Level, d.Charging, d.Online);
                wanted.Add(new KeyValuePair<string, string[]>(d.Id, new[] { d.Level.HasValue ? d.Level.ToString() : "-", tip, c.ToArgb().ToString(), d.Charging ? "1" : "" }));
            }
            if (wanted.Count == 0)
            {
                // 一个设备都没有时也留一个图标, 否则找不到程序入口
                string tip = _poller.Loading || _poller.Scanning ? "设备电量: 正在扫描…" : "设备电量: 没有发现设备";
                wanted.Add(new KeyValuePair<string, string[]>("_placeholder", new[] { "?", tip, Color.FromArgb(158, 158, 158).ToArgb().ToString(), "" }));
            }

            foreach (var id in _icons.Keys.Where(k => wanted.All(w => w.Key != k)).ToList())
            {
                _icons[id].Icon.Visible = false;
                _icons[id].Icon.Dispose();
                _icons.Remove(id);
            }
            foreach (var w in wanted)
            {
                TrayEntry e;
                if (!_icons.TryGetValue(w.Key, out e))
                {
                    e = new TrayEntry { Icon = new NotifyIcon { ContextMenu = _menu } };
                    e.Icon.MouseClick += (s, a) => { if (a.Button == MouseButtons.Left) ShowMain(); };
                    _icons[w.Key] = e;
                }
                string look = string.Join("|", w.Value);
                if (look == e.Look) continue;
                var old = e.Icon.Icon;
                e.Icon.Icon = Ui.NumberIcon(w.Value[0], Color.FromArgb(int.Parse(w.Value[2])), w.Value[3] == "1");
                if (old != null) old.Dispose();
                string text = w.Value[1];
                e.Icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;   // NotifyIcon 提示最多 63 个字符
                e.Icon.Visible = true;
                e.Look = look;
            }
        }

        private void BuildMenu()
        {
            _menu.MenuItems.Clear();
            List<DeviceState> devices;
            List<PluginStatus> plugins;
            _poller.Snapshot(out devices, out plugins);
            foreach (var d in devices)
            {
                string text = d.Name + "\t" + (d.Level.HasValue ? d.Level + "%" : "--") + (d.Charging ? " ⚡" : "") + (d.Online ? "" : " (离线)");
                _menu.MenuItems.Add(new MenuItem(text) { Enabled = false });
            }
            if (devices.Count > 0) _menu.MenuItems.Add("-");
            _menu.MenuItems.Add(new MenuItem("打开面板", (s, e) => ShowMain()) { DefaultItem = true });
            _menu.MenuItems.Add(new MenuItem("立即刷新", (s, e) => RefreshNow()));
            _menu.MenuItems.Add(new MenuItem("插件管理…", (s, e) => ShowPlugins()));
            _menu.MenuItems.Add(new MenuItem("开机自启", (s, e) => SetAutoStart(!Settings.AutoStart)) { Checked = Settings.AutoStart });
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add(new MenuItem("退出", (s, e) => Exit()));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace BatteryViewer
{
    public class DeviceState
    {
        public string Id;             // "插件id:key"
        public string PluginId, PluginName, Name;
        public DeviceKind Kind;
        public int? Level;            // 最后一次读到的电量(离线时保留)
        public bool Charging, Online;
        public string Detail = "";
        public double Updated;        // 最后一次读到电量的时间(Unix 秒), 0 表示从未读到

        public DeviceState Clone() { return (DeviceState)MemberwiseClone(); }
    }

    public class PluginStatus
    {
        public string Id, Name, Description = "", Path, Error = "";
        public bool Builtin, Disabled;
        public bool Scanned;          // 加载后是否已经扫描过一轮
        public int LastScanMs, DeviceCount;

        public PluginStatus Clone() { return (PluginStatus)MemberwiseClone(); }
    }

    /// <summary>
    /// 后台线程: 加载插件、按间隔依次调用各插件的 Scan(), 合并成设备状态表, 变化时触发 Changed(在后台线程上)。
    /// 插件只在这个线程里被创建、调用和释放, 不用担心并发。
    /// </summary>
    public class Poller
    {
        public event Action Changed = delegate { };

        public volatile bool Scanning, Loading;
        public volatile int Interval;

        private readonly object _lock = new object();
        private readonly Dictionary<string, DeviceState> _devices = new Dictionary<string, DeviceState>();
        private List<PluginStatus> _status = new List<PluginStatus>();
        private List<LoadedPlugin> _plugins = new List<LoadedPlugin>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stop;
        private List<string> _reloadDisabled;   // 非 null 表示要重新加载插件
        private List<DeviceState> _saved;       // 从 devices.json 读到、等插件加载后合并的设备
        private string _savedText = "";

        public Poller(Settings settings)
        {
            Interval = settings.Interval;
            _reloadDisabled = settings.DisabledPlugins.ToList();
            _saved = LoadState();
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "poller" };
            _thread.Start();
        }

        public void RefreshNow() { _wake.Set(); }

        public void SetInterval(int seconds) { Interval = seconds; _wake.Set(); }

        /// <summary>重新加载插件(添加、启停、修改插件后调用); 当前这轮扫描结束后在后台线程上执行。</summary>
        public void RequestReload(IEnumerable<string> disabled)
        {
            lock (_lock) _reloadDisabled = disabled.ToList();
            _wake.Set();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            if (_thread != null) _thread.Join(5000);
            foreach (var lp in _plugins) DisposePlugin(lp);
        }

        public void Snapshot(out List<DeviceState> devices, out List<PluginStatus> plugins)
        {
            lock (_lock)
            {
                devices = _devices.Values.OrderBy(d => d.PluginId).ThenBy(d => d.Name).Select(d => d.Clone()).ToList();
                plugins = _status.Select(s => s.Clone()).ToList();
            }
        }

        // ---------------------------------------------------------------- 后台线程
        private void Run()
        {
            while (!_stop)
            {
                ReloadIfRequested();
                ScanOnce();
                if (_stop) break;
                _wake.WaitOne(TimeSpan.FromSeconds(Interval));
            }
        }

        /// <summary>同步加载 + 扫描一轮, 给 --once 用。</summary>
        public void RunOnce()
        {
            ReloadIfRequested();
            ScanOnce();
        }

        private void ReloadIfRequested()
        {
            List<string> disabled;
            lock (_lock) { disabled = _reloadDisabled; _reloadDisabled = null; }
            if (disabled == null) return;

            Loading = true;
            Changed();
            foreach (var lp in _plugins) DisposePlugin(lp);
            var plugins = PluginLoader.LoadAll(disabled);
            lock (_lock)
            {
                _plugins = plugins;
                _status = plugins.Select(lp => new PluginStatus
                {
                    Id = lp.Id, Name = lp.Plugin != null ? lp.Plugin.Name : lp.Id,
                    Description = lp.Plugin != null ? lp.Plugin.Description : "",
                    Path = lp.Path, Builtin = lp.Builtin, Error = lp.Error, Disabled = lp.Disabled,
                }).OrderBy(s => s.Builtin ? 0 : 1).ThenBy(s => s.Name).ToList();
                var names = plugins.Where(Active).ToDictionary(lp => lp.Id, lp => lp.Plugin.Name);
                if (_saved != null)
                {
                    foreach (var d in _saved) if (!_devices.ContainsKey(d.Id)) _devices[d.Id] = d;
                    _saved = null;
                }
                // 停用或删掉的插件, 它的设备也一起去掉
                foreach (var id in _devices.Keys.ToList())
                {
                    string name;
                    if (names.TryGetValue(_devices[id].PluginId, out name)) _devices[id].PluginName = name;
                    else _devices.Remove(id);
                }
            }
            Loading = false;
            Changed();
        }

        private static bool Active(LoadedPlugin lp) { return lp.Plugin != null && !lp.Disabled; }

        private static void DisposePlugin(LoadedPlugin lp)
        {
            if (lp.Plugin == null) return;
            try { lp.Plugin.Dispose(); } catch (Exception) { }
        }

        private void ScanOnce()
        {
            Scanning = true;
            Changed();
            try
            {
                foreach (var lp in _plugins.Where(Active)) ScanPlugin(lp);
            }
            finally
            {
                Scanning = false;
                SaveState();
                Changed();
            }
        }

        private void ScanPlugin(LoadedPlugin lp)
        {
            var sw = Stopwatch.StartNew();
            List<Reading> readings = null;
            string error = "";
            try { readings = lp.Plugin.Scan() ?? new List<Reading>(); }
            catch (Exception e) { error = e.ToString(); }
            lock (_lock)
            {
                var st = _status.First(s => s.Id == lp.Id);
                st.Error = error;
                st.LastScanMs = (int)sw.ElapsedMilliseconds;
                st.Scanned = true;
                if (readings != null)
                {
                    Merge(lp, readings);
                    st.DeviceCount = readings.Count;
                }
            }
            Changed();
        }

        private void Merge(LoadedPlugin lp, List<Reading> readings)
        {
            double now = UnixNow();
            var reported = new HashSet<string>();
            foreach (var r in readings.Where(r => r != null && r.Key != null))
            {
                string id = lp.Id + ":" + r.Key;
                reported.Add(id);
                DeviceState d;
                if (!_devices.TryGetValue(id, out d))
                    _devices[id] = d = new DeviceState { Id = id, PluginId = lp.Id, PluginName = lp.Plugin.Name };
                d.Name = string.IsNullOrEmpty(r.Name) ? r.Key : r.Name;
                d.Kind = r.Kind;
                d.Detail = r.Detail ?? "";
                d.Online = r.Online;
                if (r.Level.HasValue)
                {
                    d.Level = Math.Max(0, Math.Min(100, r.Level.Value));
                    d.Charging = r.Charging;
                    d.Updated = now;
                }
                else if (!r.Online) d.Charging = false;
            }
            // 这次没报告的设备(拔掉了接收器、蓝牙断开)标为离线, 保留最后的电量
            foreach (var d in _devices.Values)
                if (d.PluginId == lp.Id && !reported.Contains(d.Id)) { d.Online = false; d.Charging = false; }
        }

        public static double UnixNow() { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

        // ---------------------------------------------------------------- 状态持久化
        private static List<DeviceState> LoadState()
        {
            var list = new List<DeviceState>();
            try
            {
                var arr = Json.Parse(File.ReadAllText(Settings.StatePath)) as object[];
                foreach (var o in arr ?? new object[0])
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("id") || !d.ContainsKey("plugin_id")) continue;
                    DeviceKind kind;
                    Enum.TryParse(Convert.ToString(Get(d, "kind")), true, out kind);
                    var level = Get(d, "level");
                    list.Add(new DeviceState
                    {
                        Id = Convert.ToString(d["id"]), PluginId = Convert.ToString(d["plugin_id"]),
                        Name = Convert.ToString(Get(d, "name")), Kind = kind,
                        Level = level == null ? (int?)null : Convert.ToInt32(level),
                        Updated = Convert.ToDouble(Get(d, "updated") ?? 0),
                    });
                }
            }
            catch (Exception) { }
            return list;
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        private void SaveState()
        {
            List<Dictionary<string, object>> data;
            lock (_lock)
            {
                data = _devices.Values.Where(d => d.Updated > 0).OrderBy(d => d.Id).Select(d => new Dictionary<string, object> {
                    { "id", d.Id }, { "plugin_id", d.PluginId }, { "name", d.Name }, { "kind", d.Kind.ToString().ToLowerInvariant() },
                    { "level", d.Level }, { "updated", d.Updated },
                }).ToList();
            }
            string text = string.Join("|", data.Select(x => x["id"] + "=" + x["level"] + "@" + x["updated"]));
            if (text == _savedText) return;
            try
            {
                Json.WriteFile(Settings.StatePath, data);
                _savedText = text;
            }
            catch (Exception) { }
        }
    }
}

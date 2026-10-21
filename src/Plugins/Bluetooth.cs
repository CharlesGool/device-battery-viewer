using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BatteryViewer.Plugins
{
    /// <summary>
    /// 蓝牙设备电量: 读 Windows 蓝牙协议栈写在设备节点上的电量属性。
    ///
    /// 经典蓝牙耳机通过 HFP 上报电量, 低功耗(BLE)设备通过 Battery Service 上报, Windows 收到后都会写进
    /// PnP 属性 {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2(一个字节, 0-100), 系统设置里看到的电量就是它。
    /// 经典蓝牙的电量挂在 Hands-Free 子节点上, 不在设备主节点上, 所以把所有 BTHENUM / BTHLE 节点都扫一遍,
    /// 再按蓝牙地址归到同一台设备。
    /// </summary>
    public class Bluetooth : BatteryPlugin
    {
        public override string Name { get { return "蓝牙"; } }
        public override string Description { get { return "Windows 系统记录的蓝牙设备电量(经典蓝牙 HFP / BLE Battery Service)"; } }

        private static readonly DevPropKey BatteryKey = new DevPropKey("{104EA319-6EE2-4701-BD47-8DDBF425BBE5}", 2);
        // 未公开的"已连接"标志, 经典蓝牙主节点上有; 读不到就按有没有电量判断
        private static readonly DevPropKey ConnectedKey = new DevPropKey("{83DA6326-97A6-4088-9453-A1923F573B29}", 15);
        private static readonly string[] Enumerators = { "BTHENUM", "BTHLE", "BTHLEDEVICE" };

        // 子节点名字上的后缀, 去掉后就是设备名
        private static readonly Regex NameSuffix = new Regex(@"\s*(hands-free( ag)?( audio)?|handsfree|免提|avrcp transport|stereo|立体声)\s*$", RegexOptions.IgnoreCase);

        private static readonly KeyValuePair<DeviceKind, string[]>[] KindWords = {
            new KeyValuePair<DeviceKind, string[]>(DeviceKind.Headset, new[] { "耳机", "headphone", "headset", "earbud", "buds", "airpods", "wh-", "wf-" }),
            new KeyValuePair<DeviceKind, string[]>(DeviceKind.Mouse, new[] { "鼠标", "mouse", "mx master", "mx anywhere" }),
            new KeyValuePair<DeviceKind, string[]>(DeviceKind.Keyboard, new[] { "键盘", "keyboard", "keys" }),
            new KeyValuePair<DeviceKind, string[]>(DeviceKind.Gamepad, new[] { "手柄", "controller", "gamepad", "joy-con" }),
            new KeyValuePair<DeviceKind, string[]>(DeviceKind.Speaker, new[] { "音箱", "speaker", "soundbar" }),
        };

        /// <summary>从实例 ID 里取 12 位蓝牙地址(先去掉 {GUID}, 免得把 GUID 的最后一节当成地址)。</summary>
        public static string Address(string instanceId)
        {
            var s = Regex.Replace(instanceId.ToUpperInvariant(), @"\{[^}]*\}", "");
            var m = Regex.Matches(s, "(?<![0-9A-F])([0-9A-F]{12})(?![0-9A-F])");
            return m.Count > 0 ? m[m.Count - 1].Groups[1].Value : "";
        }

        private static DeviceKind GuessKind(string name)
        {
            var low = name.ToLowerInvariant();
            foreach (var kv in KindWords)
                if (kv.Value.Any(w => low.Contains(w))) return kv.Key;
            return DeviceKind.Other;
        }

        public override List<Reading> Scan()
        {
            var keys = new Dictionary<string, DevPropKey> { { "battery", BatteryKey }, { "connected", ConnectedKey } };
            var groups = new Dictionary<string, List<DevNode>>();
            foreach (var n in DevProp.List(Enumerators, keys))
            {
                var addr = Address(n.InstanceId);
                if (addr.Length == 0) continue;
                if (!groups.ContainsKey(addr)) groups[addr] = new List<DevNode>();
                groups[addr].Add(n);
            }

            var readings = new List<Reading>();
            foreach (var kv in groups)
            {
                var levels = kv.Value.Select(n => n.Prop("battery")).OfType<int>().ToList();
                if (levels.Count == 0) continue;   // 这台设备不上报电量
                var main = kv.Value.FirstOrDefault(n => Regex.IsMatch(n.InstanceId, @"^BTH(ENUM|LE)\\DEV_", RegexOptions.IgnoreCase));
                string name = main != null && main.Name.Length > 0 ? main.Name : NameSuffix.Replace(kv.Value[0].Name, "");
                if (name.Length == 0) name = kv.Key;
                var flags = kv.Value.Select(n => n.Prop("connected")).OfType<bool>().ToList();
                int level = levels.Max();
                // 断开后 Windows 会把电量清成 0 或保留旧值; 有连接标志就以它为准
                bool online = flags.Count > 0 ? flags.Any(f => f) : level > 0;
                readings.Add(new Reading(kv.Key, name, online && level > 0 ? (int?)level : null)
                {
                    Online = online, Kind = GuessKind(name), Detail = online ? "" : "未连接",
                });
            }
            return readings;
        }
    }
}

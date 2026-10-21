using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BatteryViewer.Plugins
{
    /// <summary>
    /// 罗技 HID++ 2.0 设备电量: G502 X LIGHTSPEED 等 LIGHTSPEED / Unifying / Bolt 接收器上的设备, 以及有线直连的设备。
    ///
    /// 协议和 Linux 内核 hid-logitech-hidpp、Solaar 用的是同一套:
    /// - 报文: 短 0x10(7 字节) / 长 0x11(20 字节), [报文ID, 设备号, 特性下标, 功能&lt;&lt;4|swid, 参数...]
    /// - Windows 把短/长报文拆成两个 HID 集合: usage page 0xFF00, usage 1 短, usage 2 长。
    ///   请求一律用长报文发; 正常回复走长集合, HID++ 1.0 错误(设备没连上等)走短集合, 所以两边一起读。
    /// - 设备号: 接收器上的设备是 1-6, 有线直连是 0xFF。
    /// - 先用 ROOT(0x0000) 的 getFeature 查特性下标, 再调用:
    ///   0x1004 统一电池(G502 X 用这个, 直接给百分比) / 0x1000 电池状态 / 0x1001 电压(按放电曲线估算)
    /// </summary>
    public class LogitechHidpp : BatteryPlugin
    {
        public override string Name { get { return "罗技 HID++"; } }
        public override string Description { get { return "LIGHTSPEED / Unifying / Bolt 接收器和有线直连的罗技设备(G502 X 等)"; } }

        private const int LogitechVid = 0x046D;
        private const byte Swid = 0x0D, Short = 0x10, Long = 0x11;
        private const int TimeoutMs = 400;
        // 省电状态的鼠标第一次回复要 0.5 秒以上(先唤醒无线链路), ping 多等一会儿
        private const int PingTimeoutMs = 1500;
        private const int FDeviceInfo = 0x0003, FDeviceName = 0x0005;
        private const int FBatteryStatus = 0x1000, FBatteryVoltage = 0x1001, FUnifiedBattery = 0x1004;
        private const int ErrInvalidSubId = 0x01;

        private static readonly int[,] VoltageCurve = {
            { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
            { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 } };

        /// <summary>连上过的设备: 记住特性下标和名称, 离线时也能显示。键是 "接口分组|设备号"。</summary>
        private readonly Dictionary<string, Cache> _known = new Dictionary<string, Cache>();

        private class Cache
        {
            public string Key = "", Name = "";
            public DeviceKind Kind = DeviceKind.Other;
            public Dictionary<int, byte> Features = new Dictionary<int, byte>();
        }

        private class HidppException : Exception
        {
            public readonly int Code;
            public HidppException(int code, bool v1) : base(string.Format("HID++ {0} 错误 0x{1:X2}", v1 ? "1.0" : "2.0", code)) { Code = code; }
        }

        public static int VoltageToPercent(int mv)
        {
            if (mv >= VoltageCurve[0, 0]) return 100;
            for (int i = 1; i < VoltageCurve.GetLength(0); i++)
            {
                int vHi = VoltageCurve[i - 1, 0], pHi = VoltageCurve[i - 1, 1], vLo = VoltageCurve[i, 0], pLo = VoltageCurve[i, 1];
                if (mv >= vLo) return (int)Math.Round(pLo + (pHi - pLo) * (mv - vLo) / (double)(vHi - vLo));
            }
            return 0;
        }

        public override List<Reading> Scan()
        {
            var readings = new Dictionary<string, Reading>();
            var infos = Hid.Enumerate(LogitechVid, null, 0xFF00);
            var shorts = new Dictionary<string, HidInfo>();
            foreach (var i in infos.Where(i => i.Usage == 0x0001)) shorts[GroupKey(i.Path)] = i;
            foreach (var longInfo in infos.Where(i => i.Usage == 0x0002))
            {
                string group = GroupKey(longInfo.Path);
                HidInfo shortInfo;
                shorts.TryGetValue(group, out shortInfo);
                Channel ch;
                try { ch = new Channel(longInfo, shortInfo); }
                catch (Exception) { continue; }
                using (ch)
                {
                    bool receiver = longInfo.Product.ToLowerInvariant().Contains("receiver") || (longInfo.Pid >= 0xC500 && longInfo.Pid <= 0xC5FF);
                    var indices = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
                    foreach (var index in indices)
                    {
                        var r = ReadOne(ch, group, index, longInfo);
                        // 同一只鼠标同时插着线和接收器时, 留在线的那条
                        if (r != null && (!readings.ContainsKey(r.Key) || !readings[r.Key].Online)) readings[r.Key] = r;
                    }
                }
            }
            return readings.Values.ToList();
        }

        private Reading ReadOne(Channel ch, string group, byte index, HidInfo info)
        {
            Cache cache;
            _known.TryGetValue(group + "|" + index, out cache);
            try
            {
                // ROOT.getProtocolVersion 当 ping 用
                var proto = ch.Request(index, 0x00, 1, new byte[] { 0, 0, 0x5A }, PingTimeoutMs);
                if (proto[0] < 2) return null;
            }
            catch (Exception e)
            {
                var he = e as HidppException;
                if (he != null && he.Code == ErrInvalidSubId) return null;   // HID++ 1.0 老设备
                if (!(e is TimeoutException) && he == null) throw;
                if (cache == null) return null;                               // 这个槽位没配对过设备
                return new Reading(cache.Key, cache.Name, null) { Online = false, Detail = "休眠或未开机", Kind = cache.Kind };
            }

            var dev = new Device(ch, index, cache ?? new Cache());
            try
            {
                if (cache == null)
                {
                    var di = dev.Call(FDeviceInfo, 0);
                    string unit = di != null ? BitConverter.ToString(di, 1, 4).Replace("-", "") : "";
                    dev.Cache.Key = unit.Length > 0 ? unit : string.Format("{0:X4}-{1}", info.Pid, index);
                    string name = dev.ReadName();
                    dev.Cache.Name = name.Length > 0 ? name : (info.Product.Length > 0 ? info.Product : "罗技设备 #" + index);
                    dev.Cache.Kind = dev.ReadKind();
                    cache = dev.Cache;
                    _known[group + "|" + index] = cache;
                }
                int? level; bool charging; string detail;
                dev.ReadBattery(out level, out charging, out detail);
                return new Reading(cache.Key, cache.Name, level) { Charging = charging, Detail = detail, Kind = cache.Kind };
            }
            catch (Exception e)
            {
                if (!(e is TimeoutException) && !(e is HidppException)) throw;
                // 读到一半设备睡了; 特性下标可能失效, 清掉下次重查
                if (cache == null) return null;
                cache.Features.Clear();
                return new Reading(cache.Key, cache.Name, null) { Online = false, Detail = "读取失败: " + e.Message, Kind = cache.Kind };
            }
        }

        /// <summary>同一个物理接口的短/长集合路径只差 &amp;colXX 和实例号最后一段, 去掉后相同。</summary>
        private static string GroupKey(string path)
        {
            var p = Regex.Replace(path.ToLowerInvariant(), "&col[0-9a-f]+", "");
            return Regex.Replace(p, @"&[0-9a-f]{4}#\{", "#{");
        }

        /// <summary>一个接收器(或有线设备)上的 HID++ 通道: 长集合负责收发, 短集合只收错误。</summary>
        private sealed class Channel : IDisposable
        {
            private readonly HidDevice _long, _short;
            private readonly List<HidDevice> _all = new List<HidDevice>();

            public Channel(HidInfo longInfo, HidInfo shortInfo)
            {
                _long = new HidDevice(longInfo.Path);
                _all.Add(_long);
                if (shortInfo != null)
                {
                    try { _short = new HidDevice(shortInfo.Path); _all.Add(_short); }
                    catch (Exception) { }
                }
            }

            public void Dispose()
            {
                foreach (var d in _all) d.Dispose();
            }

            /// <summary>发一个请求, 返回回复的参数部分; 超时抛 TimeoutException, 设备报错抛 HidppException。</summary>
            public byte[] Request(byte index, byte featIndex, int func, byte[] parameters, int timeoutMs = TimeoutMs)
            {
                byte fsw = (byte)((func << 4) | Swid);
                var msg = new byte[4 + (parameters == null ? 0 : parameters.Length)];
                msg[0] = Long; msg[1] = index; msg[2] = featIndex; msg[3] = fsw;
                if (parameters != null) Array.Copy(parameters, 0, msg, 4, parameters.Length);

                var result = Hid.Transact<object>(_all, () => _long.Write(msg), d =>
                {
                    if (d.Length < 6 || (d[0] != Short && d[0] != Long) || d[1] != index) return null;
                    if (d[2] == featIndex && d[3] == fsw) return d.Skip(4).ToArray();
                    if (d[0] == Long && d[2] == 0xFF && d[3] == featIndex && d[4] == fsw) return new HidppException(d[5], false);
                    if (d[0] == Short && d[2] == 0x8F && d[3] == featIndex && d[4] == fsw) return new HidppException(d[5], true);
                    return null;
                }, timeoutMs);
                if (result == null) throw new TimeoutException("HID++ 请求超时");
                if (result is HidppException) throw (HidppException)result;
                return (byte[])result;
            }
        }

        private class Device
        {
            private readonly Channel _ch;
            private readonly byte _index;
            public readonly Cache Cache;

            public Device(Channel ch, byte index, Cache cache) { _ch = ch; _index = index; Cache = cache; }

            /// <summary>调用某个特性的功能; 设备没有这个特性返回 null。</summary>
            public byte[] Call(int feature, int func, params byte[] parameters)
            {
                byte fi;
                if (!Cache.Features.TryGetValue(feature, out fi))
                {
                    var resp = _ch.Request(_index, 0x00, 0, new[] { (byte)(feature >> 8), (byte)feature });
                    fi = Cache.Features[feature] = resp[0];   // 0 表示没有这个特性
                }
                return fi == 0 ? null : _ch.Request(_index, fi, func, parameters);
            }

            public string ReadName()
            {
                var count = Call(FDeviceName, 0);
                if (count == null) return "";
                var name = new List<byte>();
                while (name.Count < count[0])
                {
                    var chunk = Call(FDeviceName, 1, (byte)name.Count);
                    var part = chunk.Take(count[0] - name.Count).TakeWhile(b => b != 0).ToArray();
                    if (part.Length == 0) break;
                    name.AddRange(part);
                }
                return Encoding.UTF8.GetString(name.ToArray()).Trim();
            }

            public DeviceKind ReadKind()
            {
                var r = Call(FDeviceName, 2);
                if (r == null) return DeviceKind.Other;
                switch (r[0])
                {
                    case 0: return DeviceKind.Keyboard;
                    case 3: case 4: case 5: return DeviceKind.Mouse;
                    case 8: return DeviceKind.Headset;
                    case 12: return DeviceKind.Gamepad;
                    default: return DeviceKind.Other;
                }
            }

            public void ReadBattery(out int? level, out bool charging, out string detail)
            {
                var r = Call(FUnifiedBattery, 1);
                if (r != null)
                {
                    var caps = Call(FUnifiedBattery, 0);
                    int soc = r[0], bits = r[1], status = r[2];
                    if (caps != null && (caps[1] & 0x02) == 0)
                        // 设备不支持百分比, 只有 满/好/低/危急 四档
                        soc = (bits & 8) != 0 ? 100 : (bits & 4) != 0 ? 50 : (bits & 2) != 0 ? 20 : 5;
                    // 充电状态: 0 放电 / 1 充电 / 2 慢充 / 3 已充满(仍接着电源) / 4 充电异常
                    level = soc;
                    charging = status >= 1 && status <= 3;
                    detail = status == 2 ? "慢速充电" : status == 3 ? "已充满" : status == 4 ? "充电异常" : "";
                    return;
                }
                r = Call(FBatteryStatus, 0);
                if (r != null)
                {
                    level = r[0] > 0 ? (int?)r[0] : null;
                    // 状态: 0 放电 / 1 充电 / 2 快充满 / 3 已充满 / 4 慢充 / 5 电池异常 / 6 温度异常
                    charging = r[2] >= 1 && r[2] <= 4;
                    detail = r[2] == 3 ? "已充满" : r[2] == 4 ? "慢速充电" : r[2] == 5 ? "电池异常" : r[2] == 6 ? "温度异常" : "";
                    return;
                }
                r = Call(FBatteryVoltage, 0);
                if (r != null)
                {
                    int mv = (r[0] << 8) | r[1];
                    level = VoltageToPercent(mv);
                    charging = (r[2] & 0x80) != 0;
                    detail = string.Format("电压 {0:0.00} V", mv / 1000.0);
                    return;
                }
                level = null; charging = false; detail = "设备不支持电量特性";
            }
        }
    }
}

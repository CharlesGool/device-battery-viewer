using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryViewer
{
    /// <summary>PnP 设备属性键, 写法和 PowerShell Get-PnpDeviceProperty 一致: ("{fmtid}", pid)。</summary>
    public struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;

        public DevPropKey(string fmtid, uint pid) { FmtId = new Guid(fmtid); Pid = pid; }
    }

    public class DevNode
    {
        public string InstanceId;
        public string Name;
        public Dictionary<string, object> Props = new Dictionary<string, object>();

        public object Prop(string name)
        {
            object v;
            return Props.TryGetValue(name, out v) ? v : null;
        }
    }

    /// <summary>PnP 设备节点枚举 + 读设备属性(SetupDiGetDeviceProperty)。</summary>
    public static class DevProp
    {
        public static readonly DevPropKey FriendlyName = new DevPropKey("{a45c254e-df1c-4efd-8020-67d146a850e0}", 14);
        public static readonly DevPropKey DeviceDesc = new DevPropKey("{a45c254e-df1c-4efd-8020-67d146a850e0}", 2);

        /// <summary>
        /// 列出当前存在的设备节点, 并读出 keys 里的属性(读不到的是 null)。
        /// enumerators 是实例 ID 的第一段, 如 "BTHENUM"、"USB"; null 表示全部。
        /// 属性值按类型转换: 字节/整数 -> int 或 long, 布尔 -> bool, 字符串 -> string, 其他 -> byte[]。
        /// </summary>
        public static List<DevNode> List(string[] enumerators, Dictionary<string, DevPropKey> keys)
        {
            var result = new List<DevNode>();
            foreach (var en in enumerators ?? new string[] { null })
            {
                IntPtr set = Native.SetupDiGetClassDevsAll(IntPtr.Zero, en, IntPtr.Zero, Native.DIGCF_ALLCLASSES | Native.DIGCF_PRESENT);
                if (set == Native.INVALID_HANDLE) continue;
                try
                {
                    for (int i = 0; ; i++)
                    {
                        var info = new Native.SP_DEVINFO_DATA();
                        info.cbSize = Marshal.SizeOf(info);
                        if (!Native.SetupDiEnumDeviceInfo(set, i, ref info)) break;
                        var sb = new StringBuilder(512);
                        int need;
                        if (!Native.SetupDiGetDeviceInstanceId(set, ref info, sb, sb.Capacity, out need)) continue;
                        var node = new DevNode { InstanceId = sb.ToString() };
                        node.Name = (Get(set, ref info, FriendlyName) ?? Get(set, ref info, DeviceDesc) ?? "").ToString();
                        if (keys != null)
                            foreach (var kv in keys) node.Props[kv.Key] = Get(set, ref info, kv.Value);
                        result.Add(node);
                    }
                }
                finally { Native.SetupDiDestroyDeviceInfoList(set); }
            }
            return result;
        }

        private static object Get(IntPtr set, ref Native.SP_DEVINFO_DATA info, DevPropKey key)
        {
            var k = new Native.DEVPROPKEY { fmtid = key.FmtId, pid = key.Pid };
            uint type;
            int need;
            Native.SetupDiGetDeviceProperty(set, ref info, ref k, out type, null, 0, out need, 0);
            if (need <= 0) return null;
            var buf = new byte[need];
            if (!Native.SetupDiGetDeviceProperty(set, ref info, ref k, out type, buf, need, out need, 0)) return null;
            switch (type)
            {
                case 0x02: case 0x03: return (int)buf[0];                          // SBYTE / BYTE
                case 0x11: return buf[0] != 0;                                      // BOOLEAN
                case 0x04: return (int)BitConverter.ToInt16(buf, 0);
                case 0x05: return (int)BitConverter.ToUInt16(buf, 0);
                case 0x06: return BitConverter.ToInt32(buf, 0);
                case 0x07: return (long)BitConverter.ToUInt32(buf, 0);
                case 0x08: case 0x09: case 0x10: return BitConverter.ToInt64(buf, 0); // INT64 / UINT64 / FILETIME
                case 0x12: return Encoding.Unicode.GetString(buf).TrimEnd('\0');    // STRING
                default: return buf;
            }
        }
    }
}

// ATK / VXE 2.4G 无线鼠标(NANO 接收器 373B:1216)电量插件。
//
// 协议来自 https://github.com/yszyszysz233/2.4G-battery-viewer 的公开说明(作者在真机上验证过), 本插件未在真机上测试。
// - 接口: usage page 0xFF05, 输入 / 输出报文 64 字节, Report ID 0x08
// - 请求: 08 7D 72 02 00 01 07 01, 后面补 0
// - 回复: [1] == 0x72 且 [5] == 0x07 时, [7] 就是电量
// - 鼠标休眠时接收器不回复, 动一下鼠标再刷新
//
// 安装: 插件管理 → 添加插件文件…, 选这个文件。

using System;
using System.Collections.Generic;
using BatteryViewer;

public class VxeMouse : BatteryPlugin
{
    const int Vid = 0x373B, Pid = 0x1216, UsagePage = 0xFF05;
    static readonly byte[] Request = { 0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 };

    public override string Name { get { return "ATK / VXE 鼠标"; } }
    public override string Description { get { return "ATK / VXE 2.4G 无线鼠标(接收器 373B:1216)"; } }

    public override List<Reading> Scan()
    {
        var result = new List<Reading>();
        foreach (var info in Hid.Enumerate(Vid, Pid, UsagePage))
        {
            object level;
            try
            {
                using (var dev = new HidDevice(info.Path))
                {
                    level = Hid.Transact<object>(new[] { dev }, () => dev.Write(Request),
                        d => d.Length > 7 && d[1] == 0x72 && d[5] == 0x07 ? (object)(int)d[7] : null, 800);
                }
            }
            catch (Exception)
            {
                continue;   // 打不开的集合(同一个接收器可能有好几个)跳过
            }

            string key = info.Serial.Length > 0 ? info.Serial : "373B:1216";
            string name = info.Product.Length > 0 ? info.Product : "VXE 无线鼠标";
            int value = level == null ? -1 : (int)level;
            if (value < 0 || value > 100)
                result.Add(new Reading(key, name, null) { Online = false, Detail = "没有回复, 动一下鼠标再刷新", Kind = DeviceKind.Mouse });
            else
                result.Add(new Reading(key, name, value) { Kind = DeviceKind.Mouse });
            break;   // 一个接收器只对应一只鼠标
        }
        return result;
    }
}

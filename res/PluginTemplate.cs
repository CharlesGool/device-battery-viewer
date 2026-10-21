// 设备电量插件模板。
//
// 改成你的设备后保存, 在「插件管理」里点「重新加载」就生效, 编译错误会显示在插件列表里。
// 语法是 C# 5(系统自带编译器): 不能用 $"..." 字符串插值、?. 运算符、=> 写的属性。
// 默认能用 System / System.Linq / System.Drawing 和本程序的 Hid、DevProp; 要引用别的程序集, 加一行:
//   // ref: System.Management.dll
//
// 下面以一个"发 64 字节命令、回复第 7 字节是电量"的私有协议 2.4G 鼠标为例。
// 设备的 VID / PID / usage page 可以在设备管理器 → 属性 → 详细信息 → 硬件 Id 里看到。

using System;
using System.Collections.Generic;
using BatteryViewer;

public class ExampleMouse : BatteryPlugin
{
    const int Vid = 0x1234, Pid = 0x5678;
    const int UsagePage = 0xFF05;          // 厂商自定义的那个 HID 集合
    static readonly byte[] Request = { 0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 };

    public override string Name { get { return "示例鼠标"; } }
    public override string Description { get { return "某某 2.4G 鼠标, 私有协议"; } }

    public override List<Reading> Scan()
    {
        var result = new List<Reading>();
        foreach (var info in Hid.Enumerate(Vid, Pid, UsagePage))
        {
            object level;
            using (var dev = new HidDevice(info.Path))
            {
                // 发请求, 等第一个满足条件的回复; 无关报文自动跳过, 超时返回 null
                level = Hid.Transact<object>(new[] { dev }, () => dev.Write(Request),
                    d => d.Length > 7 && d[1] == 0x72 ? (object)(int)d[7] : null, 500);
            }
            string key = info.Serial.Length > 0 ? info.Serial : Vid.ToString("X4") + ":" + Pid.ToString("X4");
            string name = info.Product.Length > 0 ? info.Product : "示例鼠标";
            if (level == null)
                result.Add(new Reading(key, name, null) { Online = false, Detail = "没有回复, 鼠标可能在休眠", Kind = DeviceKind.Mouse });
            else
                result.Add(new Reading(key, name, (int)level) { Kind = DeviceKind.Mouse });
        }
        return result;
    }
}

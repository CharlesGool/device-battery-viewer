using System;
using System.Collections.Generic;

namespace BatteryViewer
{
    /// <summary>设备类型, 界面用它显示"鼠标/键盘/耳机"标签。</summary>
    public enum DeviceKind { Other, Mouse, Keyboard, Headset, Gamepad, Speaker }

    /// <summary>
    /// 一次读取结果, 对应一台设备。
    ///   Key      在本插件内唯一且稳定(序列号、蓝牙地址等), 用来跨次刷新对应同一台设备
    ///   Name     显示名称
    ///   Level    电量 0-100; 读不到填 null
    ///   Charging 是否在充电
    ///   Online   设备是否在线; 离线时界面显示上一次读到的电量
    ///   Detail   额外说明, 如 "HID++ 0x1004"、"耳机未开机"
    /// </summary>
    public class Reading
    {
        public string Key;
        public string Name;
        public int? Level;
        public bool Charging;
        public bool Online = true;
        public string Detail = "";
        public DeviceKind Kind = DeviceKind.Other;

        public Reading() { }

        public Reading(string key, string name, int? level)
        {
            Key = key; Name = name; Level = level;
        }
    }

    /// <summary>
    /// 插件基类。接入新设备只需要写一个子类, 实现 Scan()。
    /// Scan() 在后台线程里按刷新间隔调用, 可以阻塞(建议几秒内返回); 抛异常只影响这个插件。
    /// </summary>
    public abstract class BatteryPlugin : IDisposable
    {
        /// <summary>插件标识, 由加载器设置(内置插件是固定名字, 自定义插件是文件名)。</summary>
        public string Id { get; internal set; }

        public virtual string Name { get { return GetType().Name; } }

        public virtual string Description { get { return ""; } }

        public abstract List<Reading> Scan();

        /// <summary>插件被卸载(重新加载、停用、退出)时调用, 用来释放句柄。</summary>
        public virtual void Dispose() { }
    }
}

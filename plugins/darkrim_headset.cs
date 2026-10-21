// DARKRIM SHADOW 2.4G 耳机(杰理方案接收器, VID 3654)电量插件。
//
// 协议来自 https://github.com/yszyszysz233/2.4G-battery-viewer 的公开说明(作者在真机上验证过), 本插件未在真机上测试。
// - 接口: usage page 0xFF02, 报文 65 字节, 没有 Report ID(第 0 字节固定 0x00)
// - 帧: 00 FE <序号> <10=接收器 / 11=耳机> 00 02 <分片号> <还有后续?> <载荷长度 LE16> <载荷> <CRC BE16>
//   CRC 是 CRC16-CCITT-FALSE(poly 0x1021, init 0xFFFF), 覆盖 帧[1 .. 10+载荷长度)
// - 载荷是 TLV(类型 1 字节 + 长度 1 字节 + 值)。查询某一项 = 发 [类型, 0]
//   0x01 电量(值的第一个字节是百分比)  0x02 设备名  0x04 接收器固件  0x05 耳机固件
// - 耳机没开机或没连上时接收器不回复
//
// 安装: 插件管理 → 添加插件文件…, 选这个文件。

using System;
using System.Collections.Generic;
using System.Text;
using BatteryViewer;

public class DarkrimHeadset : BatteryPlugin
{
    const int Vid = 0x3654, UsagePage = 0xFF02, FrameLength = 65;
    const byte TlvBattery = 0x01, TlvName = 0x02, TlvHeadsetFirmware = 0x05;

    byte _seq;
    string _name, _firmware;   // 第一次读到电量后顺便查一次, 之后复用

    public override string Name { get { return "DARKRIM 耳机"; } }
    public override string Description { get { return "DARKRIM SHADOW 等杰理方案 2.4G 耳机(接收器 VID 3654)"; } }

    public override List<Reading> Scan()
    {
        var result = new List<Reading>();
        HidInfo info = null;
        foreach (var i in Hid.Enumerate(Vid, null, UsagePage))
            if (info == null || i.OutputLength == FrameLength) info = i;   // 优先 65 字节的命令接口
        if (info == null) return result;

        string key = info.Serial.Length > 0 ? info.Serial : string.Format("3654:{0:X4}", info.Pid);
        using (var dev = new HidDevice(info.Path))
        {
            byte[] battery = Query(dev, TlvBattery);
            if (battery == null || battery.Length == 0)
            {
                result.Add(new Reading(key, _name ?? "DARKRIM 耳机", null)
                    { Online = false, Detail = "耳机没开机或没连上", Kind = DeviceKind.Headset });
                return result;
            }
            if (_name == null)
            {
                _name = Text(Query(dev, TlvName));
                _firmware = Text(Query(dev, TlvHeadsetFirmware));
            }
            int level = battery[0];
            result.Add(new Reading(key, _name.Length > 0 ? _name : "DARKRIM 耳机", level <= 100 ? (int?)level : null)
            {
                Kind = DeviceKind.Headset,
                Detail = _firmware.Length > 0 ? "固件 " + _firmware : "",
            });
        }
        return result;
    }

    /// <summary>查询一项 TLV, 返回它的值; 没有回复返回 null。</summary>
    byte[] Query(HidDevice dev, byte type)
    {
        byte seq = _seq++;
        var frame = BuildFrame(seq, new byte[] { type, 0x00 });
        return Hid.Transact<byte[]>(new[] { dev }, () => dev.Write(frame), d => FindTlv(d, seq, type), 600);
    }

    public static byte[] BuildFrame(byte seq, byte[] payload)
    {
        var f = new byte[FrameLength];
        f[1] = 0xFE;
        f[2] = seq;
        f[3] = 0x10;        // 发给接收器
        f[5] = 0x02;
        f[8] = (byte)payload.Length;
        f[9] = (byte)(payload.Length >> 8);
        Array.Copy(payload, 0, f, 10, payload.Length);
        int crc = Crc16(f, 1, 9 + payload.Length);
        f[10 + payload.Length] = (byte)(crc >> 8);
        f[11 + payload.Length] = (byte)crc;
        return f;
    }

    /// <summary>回复帧校验通过、序号对得上时, 取出指定类型的 TLV 值。</summary>
    static byte[] FindTlv(byte[] d, byte seq, byte type)
    {
        if (d.Length < 12 || d[1] != 0xFE || d[2] != seq) return null;
        int len = d[8] | (d[9] << 8);
        if (10 + len + 2 > d.Length) return null;
        if (Crc16(d, 1, 9 + len) != ((d[10 + len] << 8) | d[11 + len])) return null;
        for (int i = 10; i + 2 <= 10 + len; )
        {
            int t = d[i], n = d[i + 1];
            if (i + 2 + n > 10 + len) break;
            if (t == type)
            {
                var v = new byte[n];
                Array.Copy(d, i + 2, v, 0, n);
                return v;
            }
            i += 2 + n;
        }
        return null;
    }

    /// <summary>CRC16-CCITT-FALSE: poly 0x1021, init 0xFFFF, 不反转。</summary>
    public static int Crc16(byte[] data, int offset, int count)
    {
        int crc = 0xFFFF;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= data[i] << 8;
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }

    static string Text(byte[] v)
    {
        if (v == null) return "";
        var sb = new StringBuilder();
        foreach (var b in v) if (b >= 32 && b < 127) sb.Append((char)b);
        return sb.ToString().Trim();
    }
}

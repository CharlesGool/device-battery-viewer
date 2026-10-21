"""插件模板(文件名以 _ 开头, 不会被加载)。

接入新设备: 复制这个文件, 改名(不要以 _ 开头), 放到 plugins/ 或 %APPDATA%\\DeviceBatteryViewer\\plugins\\,
实现 scan() 就行。用 `python main.py --once --debug` 测试, `python main.py --hid` 看本机有哪些 HID 接口。

下面以一个"发 64 字节命令、回复第 7 字节是电量"的私有协议 2.4G 鼠标为例。
"""

from __future__ import annotations

from core.plugin import Plugin, Reading
from winapi import hid

VID, PID = 0x1234, 0x5678
USAGE_PAGE = 0xFF05          # 厂商自定义的那个集合, --hid 里能看到
REQUEST = bytes([0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01])


class ExampleMouse(Plugin):
    name = "示例鼠标"
    description = "某某 2.4G 鼠标, 私有协议"

    def scan(self) -> list[Reading]:
        out = []
        for info in hid.enumerate(vid=VID, pid=PID, usage_page=USAGE_PAGE):
            with hid.HidDevice(info.path) as dev:
                # 发请求, 等到第一个满足条件的回复; 无关报文自动跳过, 超时返回 None
                level = hid.transact(
                    [dev], lambda: dev.write(REQUEST),
                    lambda d: d[7] if len(d) > 7 and d[1] == 0x72 else None,
                    timeout_ms=500,
                )
            key = info.serial or f"{VID:04X}:{PID:04X}"
            if level is None:
                out.append(Reading(key, info.product, None, online=False, detail="没有回复, 鼠标可能在休眠", kind="mouse"))
            else:
                out.append(Reading(key, info.product or "示例鼠标", level, kind="mouse"))
        return out

    # 读 Windows 设备属性的例子见 plugins/bluetooth.py (winapi.devprop)

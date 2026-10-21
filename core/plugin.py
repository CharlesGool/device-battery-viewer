"""插件接口。新设备接入只需要写一个 Plugin 子类, 实现 scan()。

    from core.plugin import Plugin, Reading

    class MyHeadset(Plugin):
        name = "我的耳机"

        def scan(self) -> list[Reading]:
            ...  # 找设备、读电量
            return [Reading(key="ABC123", name="XX 耳机", level=80, kind="headset")]

把文件放进 plugins/ 目录(或 %APPDATA%\\DeviceBatteryViewer\\plugins\\)就会被自动加载。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

# Reading.kind 的取值, 界面用它选图标文字
KINDS = ("mouse", "keyboard", "headset", "gamepad", "speaker", "other")


@dataclass
class Reading:
    """一次读取结果, 对应一台设备。

    key       在本插件内唯一且稳定的标识(序列号、蓝牙地址等), 用来跨次刷新对应同一台设备
    name      显示名称
    level     电量 0-100; 读不到填 None
    charging  是否在充电
    online    设备是否在线; 离线时界面会显示上一次读到的电量
    detail    额外说明, 显示在悬停提示和面板里, 比如 "HID++ 0x1004"、"耳机未开机"
    kind      设备类型, 见 KINDS
    """
    key: str
    name: str
    level: Optional[int] = None
    charging: bool = False
    online: bool = True
    detail: str = ""
    kind: str = "other"


class Plugin:
    """插件基类。

    id           插件标识, 默认取文件名; 出现在配置文件 disabled_plugins 里
    name         显示名称
    description  一句话说明
    """
    id: str = ""
    name: str = ""
    description: str = ""

    def scan(self) -> list[Reading]:
        """找出本插件负责的所有设备并读电量。

        在后台线程里按刷新间隔调用, 可以阻塞(建议整体控制在几秒内)。
        抛异常不会影响其他插件, 异常信息会显示在插件列表里。
        """
        raise NotImplementedError

    def close(self) -> None:
        """程序退出时调用, 用来释放句柄等资源。"""

"""蓝牙设备电量: 读 Windows 蓝牙协议栈写在设备节点上的电量属性。

经典蓝牙耳机通过 HFP 上报电量, 低功耗(BLE)设备通过 Battery Service 上报, Windows 收到后都会写进
PnP 属性 {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2(一个字节, 0-100), 系统设置里看到的电量就是它。
经典蓝牙的电量挂在 Hands-Free 子节点上, 不在设备主节点上, 所以要把所有 BTHENUM / BTHLE 节点都扫一遍,
再按蓝牙地址归到同一台设备。
"""

from __future__ import annotations

import re

from core.plugin import Plugin, Reading
from winapi import devprop

BATTERY = devprop.propkey("{104EA319-6EE2-4701-BD47-8DDBF425BBE5}", 2)
# 未公开的"已连接"标志, 经典蓝牙主节点上有; 读不到就按有没有电量判断
CONNECTED = devprop.propkey("{83DA6326-97A6-4088-9453-A1923F573B29}", 15)
ENUMERATORS = ("BTHENUM", "BTHLE", "BTHLEDEVICE")

# 子节点名字上的后缀, 去掉后就是设备名
NAME_SUFFIXES = re.compile(r"\s*(hands-free( ag)?( audio)?|handsfree|免提|avrcp transport|stereo|立体声)\s*$", re.I)
KIND_WORDS = [
    ("headset", ("耳机", "headphone", "headset", "earbud", "buds", "airpods", "wh-", "wf-")),
    ("mouse", ("鼠标", "mouse", "mx master", "mx anywhere")),
    ("keyboard", ("键盘", "keyboard", "keys")),
    ("gamepad", ("手柄", "controller", "gamepad", "joy-con")),
    ("speaker", ("音箱", "speaker", "soundbar")),
]


def _address(instance_id: str) -> str:
    """从实例 ID 里取 12 位蓝牙地址(先去掉 {GUID}, 免得把 GUID 的最后一节当成地址)。"""
    s = re.sub(r"\{[^}]*\}", "", instance_id.upper())
    m = re.findall(r"(?<![0-9A-F])([0-9A-F]{12})(?![0-9A-F])", s)
    return m[-1] if m else ""


def _guess_kind(name: str) -> str:
    low = name.lower()
    for kind, words in KIND_WORDS:
        if any(w in low for w in words):
            return kind
    return "other"


class Bluetooth(Plugin):
    name = "蓝牙"
    description = "Windows 系统记录的蓝牙设备电量(经典蓝牙 HFP / BLE Battery Service)"

    def scan(self) -> list[Reading]:
        nodes = devprop.list_devices(ENUMERATORS, {"battery": BATTERY, "connected": CONNECTED})
        groups: dict[str, list[devprop.DevNode]] = {}
        for n in nodes:
            addr = _address(n.instance_id)
            if addr:
                groups.setdefault(addr, []).append(n)

        readings = []
        for addr, group in groups.items():
            levels = [n.props["battery"] for n in group if isinstance(n.props["battery"], int)]
            if not levels:
                continue  # 这台设备不上报电量
            main = next((n for n in group if re.match(r"BTH(ENUM|LE)\\DEV_", n.instance_id, re.I)), None)
            name = (main.name if main and main.name else NAME_SUFFIXES.sub("", group[0].name)) or addr
            flags = [n.props["connected"] for n in group if isinstance(n.props["connected"], bool)]
            level = max(levels)
            # 断开后 Windows 会把电量清成 0 或保留旧值; 有连接标志就以它为准
            online = any(flags) if flags else level > 0
            readings.append(Reading(
                key=addr, name=name, level=level if online and level > 0 else None,
                online=online, kind=_guess_kind(name),
                detail="" if online else "未连接",
            ))
        return readings

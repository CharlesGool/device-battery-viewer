"""罗技 HID++ 2.0 设备电量: G502 X LIGHTSPEED 等 LIGHTSPEED / Unifying / Bolt 接收器上的设备, 以及有线直连的设备。

协议要点(和 Linux 内核 hid-logitech-hidpp、Solaar 用的是同一套):

- 报文: 短 0x10(7 字节) / 长 0x11(20 字节), 格式 [报文ID, 设备号, 特性下标, 功能<<4|swid, 参数...]
- Windows 把短报文和长报文拆成两个 HID 集合: usage page 0xFF00, usage 1 是短的, usage 2 是长的。
  请求一律用长报文发; 正常回复走长集合, HID++ 1.0 错误(设备没连上等)走短集合, 所以两边一起读。
- 设备号: 接收器上的设备是 1-6, 有线直连是 0xFF。
- 先用 ROOT(0x0000) 的 getFeature 查到特性下标, 再调用特性:
    0x1004 UNIFIED_BATTERY   G502 X 用这个, 直接给百分比
    0x1000 BATTERY_STATUS    老设备
    0x1001 BATTERY_VOLTAGE   只给电压, 按放电曲线估算百分比
"""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from typing import Optional

from core.plugin import Plugin, Reading
from winapi import hid

log = logging.getLogger(__name__)

LOGITECH_VID = 0x046D
SWID = 0x0D
SHORT, LONG = 0x10, 0x11
TIMEOUT_MS = 400

F_ROOT = 0x0000
F_DEVICE_INFO = 0x0003
F_DEVICE_NAME = 0x0005
F_BATTERY_STATUS = 0x1000
F_BATTERY_VOLTAGE = 0x1001
F_UNIFIED_BATTERY = 0x1004

# HID++ 1.0 错误码: 设备不存在 / 没连上
ERR_INVALID_SUBID = 0x01
ERR_UNKNOWN_DEVICE = 0x08
ERR_RESOURCE_ERROR = 0x09

DEVICE_TYPES = {0: "keyboard", 3: "mouse", 4: "mouse", 5: "mouse", 8: "headset", 12: "gamepad"}

# 0x1001 电压 (mV) -> 电量 (%), 锂电池放电曲线近似
VOLTAGE_CURVE = [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
                 (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)]


class HidppError(Exception):
    def __init__(self, code: int, v1: bool):
        super().__init__(f"HID++ {'1.0' if v1 else '2.0'} 错误 0x{code:02X}")
        self.code = code
        self.v1 = v1


def voltage_to_percent(mv: int) -> int:
    if mv >= VOLTAGE_CURVE[0][0]:
        return 100
    for (v_hi, p_hi), (v_lo, p_lo) in zip(VOLTAGE_CURVE, VOLTAGE_CURVE[1:]):
        if mv >= v_lo:
            return round(p_lo + (p_hi - p_lo) * (mv - v_lo) / (v_hi - v_lo))
    return 0


class Channel:
    """一个接收器(或有线设备)上的 HID++ 通道: 长集合负责收发, 短集合只收错误。"""

    def __init__(self, long_info: hid.HidInfo, short_info: Optional[hid.HidInfo]):
        self.info = long_info
        self.long = hid.HidDevice(long_info.path)
        self.short = None
        if short_info:
            try:
                self.short = hid.HidDevice(short_info.path)
            except OSError:
                pass

    def close(self) -> None:
        self.long.close()
        if self.short:
            self.short.close()

    def request(self, index: int, feat_index: int, func: int, params: bytes = b"") -> bytes:
        """发一个请求, 返回回复的参数部分; 超时抛 TimeoutError, 设备报错抛 HidppError。"""
        fsw = (func << 4) | SWID
        msg = bytes([LONG, index, feat_index, fsw]) + params

        def match(d: bytes):
            if len(d) < 6 or d[0] not in (SHORT, LONG) or d[1] != index:
                return None
            if d[2] == feat_index and d[3] == fsw:
                return d[4:]
            if d[0] == LONG and d[2] == 0xFF and d[3] == feat_index and d[4] == fsw:
                return HidppError(d[5], v1=False)
            if d[0] == SHORT and d[2] == 0x8F and d[3] == feat_index and d[4] == fsw:
                return HidppError(d[5], v1=True)
            return None

        devices = [self.long] + ([self.short] if self.short else [])
        result = hid.transact(devices, lambda: self.long.write(msg), match, TIMEOUT_MS)
        if result is None:
            raise TimeoutError("HID++ 请求超时")
        if isinstance(result, HidppError):
            raise result
        return result


@dataclass
class DeviceCache:
    """连上过的设备, 记住它的特性下标和名称, 离线时也能显示。"""
    key: str
    name: str = ""
    kind: str = "other"
    features: dict = field(default_factory=dict)


class HidppDevice:
    def __init__(self, ch: Channel, index: int, cache: DeviceCache):
        self.ch, self.index, self.cache = ch, index, cache

    def call(self, feature: int, func: int, params: bytes = b"") -> Optional[bytes]:
        """调用某个特性的功能; 设备没有这个特性返回 None。"""
        fi = self.cache.features.get(feature)
        if fi is None:
            resp = self.ch.request(self.index, 0x00, 0, feature.to_bytes(2, "big"))
            fi = self.cache.features[feature] = resp[0]  # 0 表示没有这个特性
        if not fi:
            return None
        return self.ch.request(self.index, fi, func, params)

    def read_name(self) -> str:
        count = self.call(F_DEVICE_NAME, 0)
        if not count:
            return ""
        name = b""
        while len(name) < count[0]:
            chunk = self.call(F_DEVICE_NAME, 1, bytes([len(name)]))
            part = chunk[:count[0] - len(name)].rstrip(b"\x00")
            if not part:
                break
            name += part
        return name.decode("utf-8", "ignore").strip()

    def read_kind(self) -> str:
        r = self.call(F_DEVICE_NAME, 2)
        return DEVICE_TYPES.get(r[0], "other") if r else "other"

    def read_battery(self) -> tuple[Optional[int], bool, str]:
        """返回 (电量, 是否充电, 说明)。"""
        r = self.call(F_UNIFIED_BATTERY, 1)
        if r is not None:
            caps = self.call(F_UNIFIED_BATTERY, 0)
            soc, level_bits, status = r[0], r[1], r[2]
            if caps and not caps[1] & 0x02:
                # 设备不支持百分比, 只有 满/好/低/危急 四档
                soc = 100 if level_bits & 8 else 50 if level_bits & 4 else 20 if level_bits & 2 else 5
            detail = {3: "已充满", 4: "充电异常"}.get(status, "")
            return soc, status in (1, 2), detail or "HID++ 0x1004"
        r = self.call(F_BATTERY_STATUS, 0)
        if r is not None:
            status = r[2]
            detail = {3: "已充满", 5: "电池异常", 6: "温度异常"}.get(status, "HID++ 0x1000")
            return (r[0] or None), status in (1, 2, 4), detail
        r = self.call(F_BATTERY_VOLTAGE, 0)
        if r is not None:
            mv = int.from_bytes(r[0:2], "big")
            charging = bool(r[2] & 0x80)
            return voltage_to_percent(mv), charging, f"电压 {mv / 1000:.2f} V"
        return None, False, "设备不支持电量特性"


def _group_key(path: str) -> str:
    """同一个物理接口的短/长集合路径只差 &colXX 和实例号最后一段, 去掉后相同。"""
    p = re.sub(r"&col[0-9a-f]+", "", path.lower())
    return re.sub(r"&[0-9a-f]{4}#\{", "#{", p)


def _is_receiver(info: hid.HidInfo) -> bool:
    return "receiver" in info.product.lower() or 0xC500 <= info.pid <= 0xC5FF


class LogitechHidpp(Plugin):
    name = "罗技 HID++"
    description = "LIGHTSPEED / Unifying / Bolt 接收器和有线直连的罗技设备(G502 X 等)"

    def __init__(self):
        # (接口分组, 设备号) -> DeviceCache
        self.known: dict[tuple[str, int], DeviceCache] = {}

    def _channels(self) -> list[tuple[hid.HidInfo, Optional[hid.HidInfo]]]:
        infos = hid.enumerate(vid=LOGITECH_VID, usage_page=0xFF00)
        longs = [i for i in infos if i.usage == 0x0002]
        shorts = {_group_key(i.path): i for i in infos if i.usage == 0x0001}
        return [(l, shorts.get(_group_key(l.path))) for l in longs]

    def scan(self) -> list[Reading]:
        readings: dict[str, Reading] = {}
        for long_info, short_info in self._channels():
            group = _group_key(long_info.path)
            try:
                ch = Channel(long_info, short_info)
            except OSError as e:
                log.debug("打开 %s 失败: %s", long_info, e)
                continue
            try:
                indices = range(1, 7) if _is_receiver(long_info) else [0xFF]
                for index in indices:
                    r = self._read_one(ch, group, index, long_info)
                    # 同一只鼠标同时插着线和接收器时, 留在线的那条
                    if r and (r.key not in readings or not readings[r.key].online):
                        readings[r.key] = r
            finally:
                ch.close()
        return list(readings.values())

    def _read_one(self, ch: Channel, group: str, index: int, info: hid.HidInfo) -> Optional[Reading]:
        cache = self.known.get((group, index))
        try:
            # ROOT.getProtocolVersion 当 ping 用
            proto = ch.request(index, 0x00, 1, bytes([0, 0, 0x5A]))
            if proto[0] < 2:
                return None
        except (TimeoutError, HidppError) as e:
            if isinstance(e, HidppError) and e.code == ERR_INVALID_SUBID:
                return None  # HID++ 1.0 老设备, 不支持
            if cache is None:
                return None  # 这个槽位没配对过设备
            return Reading(cache.key, cache.name, None, online=False, detail="休眠或未开机", kind=cache.kind)

        dev = HidppDevice(ch, index, cache or DeviceCache(key=""))
        try:
            if cache is None:
                info_resp = dev.call(F_DEVICE_INFO, 0)
                unit_id = info_resp[1:5].hex().upper() if info_resp else ""
                dev.cache.key = unit_id or f"{info.pid:04X}-{index}"
                dev.cache.name = dev.read_name() or info.product or f"罗技设备 #{index}"
                dev.cache.kind = dev.read_kind()
                self.known[(group, index)] = cache = dev.cache
            level, charging, detail = dev.read_battery()
        except (TimeoutError, HidppError) as e:
            # 读到一半设备睡了; 特性下标可能失效, 清掉下次重查
            if cache is None:
                return None
            cache.features.clear()
            return Reading(cache.key, cache.name, None, online=False, detail=f"读取失败: {e}", kind=cache.kind)
        return Reading(cache.key, cache.name, level, charging=charging, detail=detail, kind=cache.kind)

"""PnP 设备节点枚举 + 读设备属性(DEVPROPKEY), 纯 ctypes 调 setupapi。

    list_devices(["BTHENUM", "BTHLE"], {"battery": BATTERY_KEY}) -> list[DevNode]
"""

from __future__ import annotations

import ctypes
import struct
from ctypes import wintypes
from dataclasses import dataclass, field
from typing import Iterable, Optional

from .hid import GUID, INVALID_HANDLE_VALUE, setupapi

DIGCF_PRESENT = 0x02
DIGCF_ALLCLASSES = 0x04


class DEVPROPKEY(ctypes.Structure):
    _fields_ = [("fmtid", GUID), ("pid", wintypes.ULONG)]


class SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("ClassGuid", GUID),
        ("DevInst", wintypes.DWORD),
        ("Reserved", ctypes.c_void_p),
    ]


setupapi.SetupDiEnumDeviceInfo.restype = wintypes.BOOL
setupapi.SetupDiEnumDeviceInfo.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(SP_DEVINFO_DATA)]
setupapi.SetupDiGetDeviceInstanceIdW.restype = wintypes.BOOL
setupapi.SetupDiGetDeviceInstanceIdW.argtypes = [
    ctypes.c_void_p, ctypes.POINTER(SP_DEVINFO_DATA), wintypes.LPWSTR, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
setupapi.SetupDiGetDevicePropertyW.restype = wintypes.BOOL
setupapi.SetupDiGetDevicePropertyW.argtypes = [
    ctypes.c_void_p, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.POINTER(DEVPROPKEY), ctypes.POINTER(wintypes.ULONG),
    ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]


def propkey(fmtid: str, pid: int) -> tuple[str, int]:
    """属性键写成 ("{fmtid}", pid), 和 PowerShell Get-PnpDeviceProperty 的写法一致。"""
    return fmtid, pid


FRIENDLY_NAME = propkey("{a45c254e-df1c-4efd-8020-67d146a850e0}", 14)
DEVICE_DESC = propkey("{a45c254e-df1c-4efd-8020-67d146a850e0}", 2)
CONTAINER_ID = propkey("{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c}", 2)

# DEVPROP_TYPE_*
_BYTE, _SBYTE, _INT16, _UINT16, _INT32, _UINT32 = 0x03, 0x02, 0x04, 0x05, 0x06, 0x07
_INT64, _UINT64, _GUID, _FILETIME, _BOOLEAN, _STRING = 0x08, 0x09, 0x0D, 0x10, 0x11, 0x12
_STRING_LIST = 0x2012


def _decode(ptype: int, raw: bytes):
    if ptype in (_BYTE, _SBYTE):
        return raw[0]
    if ptype == _BOOLEAN:
        return raw[0] != 0
    fmt = {_INT16: "<h", _UINT16: "<H", _INT32: "<i", _UINT32: "<I", _INT64: "<q", _UINT64: "<Q", _FILETIME: "<Q"}
    if ptype in fmt:
        return struct.unpack(fmt[ptype], raw[:struct.calcsize(fmt[ptype])])[0]
    if ptype == _STRING:
        return raw.decode("utf-16-le", "ignore").split("\x00")[0]
    if ptype == _STRING_LIST:
        return [s for s in raw.decode("utf-16-le", "ignore").split("\x00") if s]
    if ptype == _GUID:
        a, b, c = struct.unpack("<IHH", raw[:8])
        d = raw[8:16].hex()
        return f"{{{a:08x}-{b:04x}-{c:04x}-{d[:4]}-{d[4:]}}}"
    return raw


@dataclass
class DevNode:
    instance_id: str
    name: str
    props: dict = field(default_factory=dict)


def _get_property(hdev, info, key: tuple[str, int]):
    k = DEVPROPKEY(GUID.parse(key[0]), key[1])
    ptype = wintypes.ULONG(0)
    need = wintypes.DWORD(0)
    setupapi.SetupDiGetDevicePropertyW(hdev, ctypes.byref(info), ctypes.byref(k), ctypes.byref(ptype),
                                       None, 0, ctypes.byref(need), 0)
    if need.value == 0:
        return None
    buf = ctypes.create_string_buffer(need.value)
    if not setupapi.SetupDiGetDevicePropertyW(hdev, ctypes.byref(info), ctypes.byref(k), ctypes.byref(ptype),
                                              buf, need.value, None, 0):
        return None
    return _decode(ptype.value, buf.raw)


def list_devices(enumerators: Optional[Iterable[str]] = None,
                 keys: Optional[dict[str, tuple[str, int]]] = None) -> list[DevNode]:
    """列出当前存在的设备节点, 并读出 keys 里的属性(读不到的是 None)。

    enumerators 是实例 ID 的第一段, 如 "BTHENUM"、"USB"; None 表示全部。
    """
    keys = keys or {}
    out: list[DevNode] = []
    for enum in (list(enumerators) if enumerators else [None]):
        hdev = setupapi.SetupDiGetClassDevsW(None, enum, None, DIGCF_ALLCLASSES | DIGCF_PRESENT)
        if not hdev or hdev == INVALID_HANDLE_VALUE:
            continue
        try:
            index = 0
            while True:
                info = SP_DEVINFO_DATA()
                info.cbSize = ctypes.sizeof(info)
                if not setupapi.SetupDiEnumDeviceInfo(hdev, index, ctypes.byref(info)):
                    break
                index += 1
                buf = ctypes.create_unicode_buffer(512)
                if not setupapi.SetupDiGetDeviceInstanceIdW(hdev, ctypes.byref(info), buf, 512, None):
                    continue
                name = _get_property(hdev, info, FRIENDLY_NAME) or _get_property(hdev, info, DEVICE_DESC) or ""
                props = {k: _get_property(hdev, info, v) for k, v in keys.items()}
                out.append(DevNode(buf.value, name, props))
        finally:
            setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return out

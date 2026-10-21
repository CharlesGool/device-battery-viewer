"""Windows HID 访问层: 纯 ctypes 调 setupapi / hid / kernel32, 不依赖 hidapi。

插件作者常用的就两个东西:

    enumerate(vid=..., usage_page=...)  -> list[HidInfo]   列出 HID 接口
    HidDevice(info.path)                                    打开接口读写报文

读写全部用 overlapped I/O + 超时, 同一时刻每个句柄上只有一个操作在进行。
"""

from __future__ import annotations

import ctypes
import time
from ctypes import wintypes
from dataclasses import dataclass
from typing import Callable, Iterable, Optional

setupapi = ctypes.WinDLL("setupapi", use_last_error=True)
hid = ctypes.WinDLL("hid", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x01
FILE_SHARE_WRITE = 0x02
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
ERROR_IO_PENDING = 997
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 0x102
INFINITE = 0xFFFFFFFF


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", wintypes.DWORD),
        ("Data2", wintypes.WORD),
        ("Data3", wintypes.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    ]

    @classmethod
    def parse(cls, text: str) -> "GUID":
        a, b, c, d, e = text.strip("{}").split("-")
        tail = bytes.fromhex(d + e)
        return cls(int(a, 16), int(b, 16), int(c, 16), (ctypes.c_ubyte * 8)(*tail))


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("InterfaceClassGuid", GUID),
        ("Flags", wintypes.DWORD),
        ("Reserved", ctypes.c_void_p),
    ]


class OVERLAPPED(ctypes.Structure):
    _fields_ = [
        ("Internal", ctypes.c_void_p),
        ("InternalHigh", ctypes.c_void_p),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [
        ("Size", wintypes.ULONG),
        ("VendorID", wintypes.USHORT),
        ("ProductID", wintypes.USHORT),
        ("VersionNumber", wintypes.USHORT),
    ]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [
        ("Usage", wintypes.USHORT),
        ("UsagePage", wintypes.USHORT),
        ("InputReportByteLength", wintypes.USHORT),
        ("OutputReportByteLength", wintypes.USHORT),
        ("FeatureReportByteLength", wintypes.USHORT),
        ("Reserved", wintypes.USHORT * 17),
        ("_counts", wintypes.USHORT * 10),
    ]


HDEVINFO = ctypes.c_void_p

setupapi.SetupDiGetClassDevsW.restype = HDEVINFO
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), wintypes.LPCWSTR, wintypes.HWND, wintypes.DWORD]
setupapi.SetupDiEnumDeviceInterfaces.restype = wintypes.BOOL
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [
    HDEVINFO, ctypes.c_void_p, ctypes.POINTER(GUID), wintypes.DWORD, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wintypes.BOOL
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [
    HDEVINFO, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA), ctypes.c_void_p, wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.restype = wintypes.BOOL
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [HDEVINFO]

hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
hid.HidD_GetAttributes.restype = wintypes.BOOLEAN
hid.HidD_GetAttributes.argtypes = [wintypes.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
hid.HidD_GetPreparsedData.restype = wintypes.BOOLEAN
hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid.HidD_FreePreparsedData.restype = wintypes.BOOLEAN
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidP_GetCaps.restype = wintypes.LONG
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
hid.HidD_FlushQueue.restype = wintypes.BOOLEAN
hid.HidD_FlushQueue.argtypes = [wintypes.HANDLE]
hid.HidD_GetFeature.restype = wintypes.BOOLEAN
hid.HidD_GetFeature.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
hid.HidD_SetFeature.restype = wintypes.BOOLEAN
hid.HidD_SetFeature.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
for _name in ("HidD_GetProductString", "HidD_GetManufacturerString", "HidD_GetSerialNumberString"):
    getattr(hid, _name).restype = wintypes.BOOLEAN
    getattr(hid, _name).argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]

kernel32.CreateFileW.restype = wintypes.HANDLE
kernel32.CreateFileW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CreateEventW.restype = wintypes.HANDLE
kernel32.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.ResetEvent.argtypes = [wintypes.HANDLE]
kernel32.ReadFile.restype = wintypes.BOOL
kernel32.ReadFile.argtypes = [
    wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(OVERLAPPED)]
kernel32.WriteFile.restype = wintypes.BOOL
kernel32.WriteFile.argtypes = [
    wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(OVERLAPPED)]
kernel32.GetOverlappedResult.restype = wintypes.BOOL
kernel32.GetOverlappedResult.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(OVERLAPPED), ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
kernel32.CancelIoEx.restype = wintypes.BOOL
kernel32.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(OVERLAPPED)]
kernel32.WaitForMultipleObjects.restype = wintypes.DWORD
kernel32.WaitForMultipleObjects.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE), wintypes.BOOL, wintypes.DWORD]


class HidError(OSError):
    pass


@dataclass
class HidInfo:
    path: str
    vid: int
    pid: int
    usage_page: int
    usage: int
    input_len: int
    output_len: int
    feature_len: int
    product: str = ""
    manufacturer: str = ""
    serial: str = ""

    def __str__(self) -> str:
        return (f"{self.vid:04X}:{self.pid:04X} page={self.usage_page:04X} usage={self.usage:04X} "
                f"in={self.input_len} out={self.output_len} feat={self.feature_len} {self.product!r}")


def _hid_string(handle, fn) -> str:
    buf = ctypes.create_unicode_buffer(256)
    if fn(handle, buf, ctypes.sizeof(buf)):
        return buf.value
    return ""


def _interface_paths() -> list[str]:
    guid = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(guid))
    hdev = setupapi.SetupDiGetClassDevsW(ctypes.byref(guid), None, None, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if not hdev or hdev == INVALID_HANDLE_VALUE:
        return []
    paths = []
    try:
        index = 0
        while True:
            data = SP_DEVICE_INTERFACE_DATA()
            data.cbSize = ctypes.sizeof(data)
            if not setupapi.SetupDiEnumDeviceInterfaces(hdev, None, ctypes.byref(guid), index, ctypes.byref(data)):
                break
            index += 1
            need = wintypes.DWORD(0)
            setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(data), None, 0, ctypes.byref(need), None)
            buf = ctypes.create_string_buffer(need.value + 4)
            # SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 64 位是 8, 32 位是 6
            ctypes.c_uint32.from_buffer(buf).value = 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6
            if setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(data), buf, need, None, None):
                paths.append(ctypes.wstring_at(ctypes.addressof(buf) + 4))
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return paths


def _query(path: str) -> Optional[HidInfo]:
    # 访问权限 0: 鼠标/键盘这类被系统独占的集合也能打开读属性
    handle = kernel32.CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING, 0, None)
    if not handle or handle == INVALID_HANDLE_VALUE:
        return None
    try:
        attrs = HIDD_ATTRIBUTES()
        attrs.Size = ctypes.sizeof(attrs)
        if not hid.HidD_GetAttributes(handle, ctypes.byref(attrs)):
            return None
        caps = HIDP_CAPS()
        pre = ctypes.c_void_p()
        if hid.HidD_GetPreparsedData(handle, ctypes.byref(pre)):
            hid.HidP_GetCaps(pre, ctypes.byref(caps))
            hid.HidD_FreePreparsedData(pre)
        return HidInfo(
            path=path, vid=attrs.VendorID, pid=attrs.ProductID,
            usage_page=caps.UsagePage, usage=caps.Usage,
            input_len=caps.InputReportByteLength, output_len=caps.OutputReportByteLength,
            feature_len=caps.FeatureReportByteLength,
            product=_hid_string(handle, hid.HidD_GetProductString),
            manufacturer=_hid_string(handle, hid.HidD_GetManufacturerString),
            serial=_hid_string(handle, hid.HidD_GetSerialNumberString),
        )
    finally:
        kernel32.CloseHandle(handle)


def enumerate(vid: Optional[int] = None, pid: Optional[int] = None,
              usage_page: Optional[int] = None, usage: Optional[int] = None) -> list[HidInfo]:
    """列出当前存在的 HID 接口, 参数为 None 表示不过滤。"""
    out = []
    for path in _interface_paths():
        # 路径里带 vid_xxxx&pid_xxxx, 先按字符串粗筛, 少开很多句柄
        low = path.lower()
        if vid is not None and f"vid_{vid:04x}" not in low:
            continue
        if pid is not None and f"pid_{pid:04x}" not in low:
            continue
        info = _query(path)
        if info is None:
            continue
        if usage_page is not None and info.usage_page != usage_page:
            continue
        if usage is not None and info.usage != usage:
            continue
        out.append(info)
    return out


class HidDevice:
    """一个以读写权限、overlapped 方式打开的 HID 接口。"""

    def __init__(self, path: str):
        self.path = path
        self.handle = kernel32.CreateFileW(
            path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            None, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, None)
        if not self.handle or self.handle == INVALID_HANDLE_VALUE:
            raise HidError(ctypes.get_last_error(), f"打开 HID 接口失败: {path}")
        info = _query(path)
        self.input_len = info.input_len if info else 64
        self.output_len = info.output_len if info else 64
        self._read_ov = OVERLAPPED()
        self._read_ov.hEvent = kernel32.CreateEventW(None, True, False, None)
        self._read_buf = ctypes.create_string_buffer(max(self.input_len, 1))
        self._reading = False

    def close(self) -> None:
        if self.handle:
            self._cancel_read()
            kernel32.CloseHandle(self._read_ov.hEvent)
            kernel32.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def flush(self) -> None:
        """丢掉驱动缓冲里还没读的输入报文。"""
        self._cancel_read()
        hid.HidD_FlushQueue(self.handle)

    def write(self, data: bytes, timeout_ms: int = 1000) -> None:
        """写 Output Report, data[0] 是 Report ID; 不足报文长度时补 0。"""
        data = bytes(data).ljust(self.output_len, b"\x00")
        buf = ctypes.create_string_buffer(data, len(data))
        ov = OVERLAPPED()
        ov.hEvent = kernel32.CreateEventW(None, True, False, None)
        try:
            done = wintypes.DWORD(0)
            if not kernel32.WriteFile(self.handle, buf, len(data), None, ctypes.byref(ov)):
                if ctypes.get_last_error() != ERROR_IO_PENDING:
                    raise HidError(ctypes.get_last_error(), "WriteFile 失败")
                if kernel32.WaitForMultipleObjects(1, (wintypes.HANDLE * 1)(ov.hEvent), False, timeout_ms) != WAIT_OBJECT_0:
                    kernel32.CancelIoEx(self.handle, ctypes.byref(ov))
                    kernel32.GetOverlappedResult(self.handle, ctypes.byref(ov), ctypes.byref(done), True)
                    raise HidError(0, "WriteFile 超时")
            if not kernel32.GetOverlappedResult(self.handle, ctypes.byref(ov), ctypes.byref(done), True):
                raise HidError(ctypes.get_last_error(), "WriteFile 失败")
        finally:
            kernel32.CloseHandle(ov.hEvent)

    # ---- 读: 发起一次 ReadFile, 等事件, 取结果 ----
    def _arm_read(self) -> None:
        if self._reading:
            return
        kernel32.ResetEvent(self._read_ov.hEvent)
        ok = kernel32.ReadFile(self.handle, self._read_buf, len(self._read_buf), None, ctypes.byref(self._read_ov))
        if not ok and ctypes.get_last_error() != ERROR_IO_PENDING:
            raise HidError(ctypes.get_last_error(), "ReadFile 失败")
        self._reading = True

    def _collect_read(self) -> Optional[bytes]:
        done = wintypes.DWORD(0)
        ok = kernel32.GetOverlappedResult(self.handle, ctypes.byref(self._read_ov), ctypes.byref(done), False)
        self._reading = False
        if not ok:
            return None
        return self._read_buf.raw[:done.value]

    def _cancel_read(self) -> None:
        if self._reading:
            kernel32.CancelIoEx(self.handle, ctypes.byref(self._read_ov))
            done = wintypes.DWORD(0)
            kernel32.GetOverlappedResult(self.handle, ctypes.byref(self._read_ov), ctypes.byref(done), True)
            self._reading = False

    def read(self, timeout_ms: int = 1000) -> Optional[bytes]:
        """读一个 Input Report, 超时返回 None。"""
        _, data = read_any([self], timeout_ms)
        return data

    def get_feature(self, report_id: int, length: int) -> Optional[bytes]:
        buf = ctypes.create_string_buffer(bytes([report_id]).ljust(length, b"\x00"), length)
        if hid.HidD_GetFeature(self.handle, buf, length):
            return buf.raw
        return None

    def set_feature(self, data: bytes) -> bool:
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        return bool(hid.HidD_SetFeature(self.handle, buf, len(data)))


def read_any(devices: Iterable[HidDevice], timeout_ms: int) -> tuple[Optional[HidDevice], Optional[bytes]]:
    """在多个接口上同时等输入报文, 返回最先到的 (设备, 数据); 超时返回 (None, None)。

    没被取走的读请求会留着, 下次 read / read_any 接着用, 不会丢报文。
    """
    devices = list(devices)
    for dev in devices:
        dev._arm_read()
    events = (wintypes.HANDLE * len(devices))(*[d._read_ov.hEvent for d in devices])
    rc = kernel32.WaitForMultipleObjects(len(devices), events, False, max(0, int(timeout_ms)))
    if WAIT_OBJECT_0 <= rc < WAIT_OBJECT_0 + len(devices):
        dev = devices[rc - WAIT_OBJECT_0]
        return dev, dev._collect_read()
    return None, None


def transact(devices: Iterable[HidDevice], send: Callable[[], None],
             match: Callable[[bytes], Optional[object]], timeout_ms: int = 1000):
    """清空缓冲 -> send() -> 在 devices 上读, 直到 match(报文) 返回非 None 或超时。

    返回 match 的结果; 超时返回 None。中途收到的无关报文(比如鼠标移动通知)会被跳过。
    """
    devices = list(devices)
    for dev in devices:
        dev.flush()
    send()
    deadline = time.monotonic() + timeout_ms / 1000
    while True:
        left = int((deadline - time.monotonic()) * 1000)
        if left <= 0:
            return None
        _, data = read_any(devices, left)
        if data:
            result = match(data)
            if result is not None:
                return result

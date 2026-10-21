"""通知区域图标: 每台设备一个图标, 图标就是电量数字。纯 Win32 (Shell_NotifyIconW), 在自己的线程里跑消息循环。

其他线程只通过 post_update() / post_quit() / post_show() 和它交互(PostMessage 是线程安全的)。
"""

from __future__ import annotations

import ctypes
import logging
from ctypes import wintypes
from dataclasses import dataclass
from typing import Callable

from . import icon as iconlib

log = logging.getLogger(__name__)

user32 = ctypes.WinDLL("user32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)

WM_DESTROY, WM_CLOSE, WM_COMMAND, WM_NULL = 0x0002, 0x0010, 0x0111, 0x0000
WM_LBUTTONUP, WM_RBUTTONUP, WM_LBUTTONDBLCLK = 0x0202, 0x0205, 0x0203
WM_APP = 0x8000
WM_TRAY = WM_APP + 1      # 图标回调
WM_UPDATE = WM_APP + 2    # 设备状态变了
WM_SHOW = WM_APP + 3      # 另一个实例请求显示面板
NIM_ADD, NIM_MODIFY, NIM_DELETE = 0, 1, 2
NIF_MESSAGE, NIF_ICON, NIF_TIP = 0x1, 0x2, 0x4
MF_STRING, MF_SEPARATOR, MF_CHECKED, MF_GRAYED = 0x0, 0x800, 0x8, 0x1
TPM_RIGHTBUTTON, TPM_RETURNCMD, TPM_NONOTIFY = 0x2, 0x100, 0x80

CLASS_NAME = "DeviceBatteryViewerTray"


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD), ("hWnd", wintypes.HWND), ("uID", wintypes.UINT), ("uFlags", wintypes.UINT),
        ("uCallbackMessage", wintypes.UINT), ("hIcon", wintypes.HICON), ("szTip", wintypes.WCHAR * 128),
        ("dwState", wintypes.DWORD), ("dwStateMask", wintypes.DWORD), ("szInfo", wintypes.WCHAR * 256),
        ("uVersion", wintypes.UINT), ("szInfoTitle", wintypes.WCHAR * 64), ("dwInfoFlags", wintypes.DWORD),
        ("guidItem", ctypes.c_byte * 16), ("hBalloonIcon", wintypes.HICON),
    ]


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.UINT), ("style", wintypes.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
        ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH), ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR), ("hIconSm", wintypes.HICON),
    ]


user32.DefWindowProcW.restype = LRESULT
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.RegisterClassExW.restype = wintypes.ATOM
user32.RegisterClassExW.argtypes = [ctypes.POINTER(WNDCLASSEXW)]
user32.CreateWindowExW.restype = wintypes.HWND
user32.CreateWindowExW.argtypes = [
    wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    ctypes.c_int, wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID]
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.TranslateMessage.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.CreatePopupMenu.restype = wintypes.HMENU
user32.AppendMenuW.argtypes = [wintypes.HMENU, wintypes.UINT, ctypes.c_size_t, wintypes.LPCWSTR]
user32.TrackPopupMenu.argtypes = [
    wintypes.HMENU, wintypes.UINT, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND, ctypes.c_void_p]
user32.DestroyMenu.argtypes = [wintypes.HMENU]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
user32.RegisterWindowMessageW.restype = wintypes.UINT
user32.RegisterWindowMessageW.argtypes = [wintypes.LPCWSTR]
user32.FindWindowW.restype = wintypes.HWND
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.DestroyIcon.argtypes = [wintypes.HICON]
shell32.Shell_NotifyIconW.argtypes = [wintypes.DWORD, ctypes.POINTER(NOTIFYICONDATAW)]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]


@dataclass
class TrayItem:
    key: str                     # 设备 id, 跨刷新稳定
    text: str                    # 图标上的字
    color: tuple[int, int, int]
    tooltip: str
    charging: bool = False


@dataclass
class MenuItem:
    label: str = ""              # 空字符串表示分隔线
    command: Callable[[], None] | None = None
    checked: bool = False
    enabled: bool = True


class Tray:
    def __init__(self, items: Callable[[], list[TrayItem]], menu: Callable[[], list[MenuItem]],
                 on_click: Callable[[str], None]):
        self._items = items
        self._menu = menu
        self._on_click = on_click
        self.hwnd = None
        self._icons: dict[str, tuple[int, int, tuple]] = {}   # key -> (uID, HICON, 上次画的内容)
        self._next_uid = 1
        self._wndproc = WNDPROC(self._proc)   # 必须留引用, 否则回调被回收后进程直接崩
        self._taskbar_created = user32.RegisterWindowMessageW("TaskbarCreated")

    # ---- 线程安全的入口 ----
    def post_update(self) -> None:
        if self.hwnd:
            user32.PostMessageW(self.hwnd, WM_UPDATE, 0, 0)

    def post_quit(self) -> None:
        if self.hwnd:
            user32.PostMessageW(self.hwnd, WM_CLOSE, 0, 0)

    @staticmethod
    def notify_running_instance() -> bool:
        """已有实例在跑时让它显示面板, 返回是否找到。"""
        hwnd = user32.FindWindowW(CLASS_NAME, None)
        if hwnd:
            user32.PostMessageW(hwnd, WM_SHOW, 0, 0)
        return bool(hwnd)

    # ---- 消息循环(在托盘线程里调用) ----
    def run(self, on_show: Callable[[], None]) -> None:
        self._on_show = on_show
        hinst = kernel32.GetModuleHandleW(None)
        wc = WNDCLASSEXW()
        wc.cbSize = ctypes.sizeof(wc)
        wc.lpfnWndProc = self._wndproc
        wc.hInstance = hinst
        wc.lpszClassName = CLASS_NAME
        user32.RegisterClassExW(ctypes.byref(wc))
        self.hwnd = user32.CreateWindowExW(0, CLASS_NAME, CLASS_NAME, 0, 0, 0, 0, 0, None, None, hinst, None)
        self._sync()
        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        self.hwnd = None

    def _nid(self, uid: int) -> NOTIFYICONDATAW:
        nid = NOTIFYICONDATAW()
        nid.cbSize = ctypes.sizeof(nid)
        nid.hWnd = self.hwnd
        nid.uID = uid
        return nid

    def _sync(self, force: bool = False) -> None:
        """按当前设备列表增删改图标。内容没变的图标不重画。"""
        items = {i.key: i for i in self._items()}
        for key in list(self._icons):
            if key not in items:
                uid, hicon, _ = self._icons.pop(key)
                shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(self._nid(uid)))
                user32.DestroyIcon(hicon)
        for key, it in items.items():
            look = (it.text, it.color, it.charging, it.tooltip)
            old = self._icons.get(key)
            if old and old[2] == look and not force:
                continue
            uid = old[0] if old else self._next_uid
            if not old:
                self._next_uid += 1
            hicon = iconlib.make_icon(it.text, it.color, underline=it.charging)
            nid = self._nid(uid)
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP
            nid.uCallbackMessage = WM_TRAY
            nid.hIcon = hicon
            nid.szTip = it.tooltip[:127]
            # 资源管理器重启后旧图标都没了, 只能重新 ADD; MODIFY 失败时也补一次 ADD
            if force or not old or not shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(nid)):
                shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))
            if old:
                user32.DestroyIcon(old[1])
            self._icons[key] = (uid, hicon, look)

    def _remove_all(self) -> None:
        for uid, hicon, _ in self._icons.values():
            shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(self._nid(uid)))
            user32.DestroyIcon(hicon)
        self._icons.clear()

    def _show_menu(self) -> None:
        items = self._menu()
        hmenu = user32.CreatePopupMenu()
        for i, m in enumerate(items, start=1):
            if not m.label:
                user32.AppendMenuW(hmenu, MF_SEPARATOR, 0, None)
                continue
            flags = MF_STRING | (MF_CHECKED if m.checked else 0) | (0 if m.enabled else MF_GRAYED)
            user32.AppendMenuW(hmenu, flags, i, m.label)
        pt = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(pt))
        user32.SetForegroundWindow(self.hwnd)   # 不加这句, 点菜单外面菜单不会消失
        cmd = user32.TrackPopupMenu(hmenu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                                    pt.x, pt.y, 0, self.hwnd, None)
        user32.PostMessageW(self.hwnd, WM_NULL, 0, 0)
        user32.DestroyMenu(hmenu)
        if 0 < cmd <= len(items) and items[cmd - 1].command:
            items[cmd - 1].command()

    def _key_of(self, uid: int) -> str:
        return next((k for k, v in self._icons.items() if v[0] == uid), "")

    def _proc(self, hwnd, msg, wparam, lparam):
        try:
            if msg == WM_TRAY:
                event = lparam & 0xFFFF
                if event == WM_LBUTTONUP:
                    self._on_click(self._key_of(wparam))
                elif event == WM_RBUTTONUP:
                    self._show_menu()
                return 0
            if msg == WM_UPDATE:
                self._sync()
                return 0
            if msg == WM_SHOW:
                self._on_show()
                return 0
            if msg == self._taskbar_created:
                self._sync(force=True)
                return 0
            if msg == WM_CLOSE:
                self._remove_all()
                user32.DestroyWindow(hwnd)
                return 0
            if msg == WM_DESTROY:
                # 不发 WM_QUIT 的话消息循环不会退出, 进程会挂在后台
                user32.PostQuitMessage(0)
                return 0
        except Exception:
            log.exception("托盘消息处理出错")
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

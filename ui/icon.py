"""把电量数字画成托盘图标(HICON): GDI 画白字 -> 按亮度算透明度 -> 上色 -> CreateIconIndirect。"""

from __future__ import annotations

import ctypes
from ctypes import wintypes

gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)

SM_CXSMICON = 49
ANTIALIASED_QUALITY = 4
FW_BOLD = 700
DT_CENTER, DT_VCENTER, DT_SINGLELINE, DT_NOPREFIX = 0x01, 0x04, 0x20, 0x800
TRANSPARENT = 1


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG), ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD),
    ]


class ICONINFO(ctypes.Structure):
    _fields_ = [
        ("fIcon", wintypes.BOOL), ("xHotspot", wintypes.DWORD), ("yHotspot", wintypes.DWORD),
        ("hbmMask", wintypes.HBITMAP), ("hbmColor", wintypes.HBITMAP),
    ]


class SIZE(ctypes.Structure):
    _fields_ = [("cx", wintypes.LONG), ("cy", wintypes.LONG)]


gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateDIBSection.restype = wintypes.HBITMAP
gdi32.CreateDIBSection.argtypes = [
    wintypes.HDC, ctypes.c_void_p, wintypes.UINT, ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD]
gdi32.CreateBitmap.restype = wintypes.HBITMAP
gdi32.CreateBitmap.argtypes = [ctypes.c_int, ctypes.c_int, wintypes.UINT, wintypes.UINT, ctypes.c_void_p]
gdi32.SelectObject.restype = wintypes.HGDIOBJ
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
gdi32.DeleteDC.argtypes = [wintypes.HDC]
gdi32.CreateFontW.restype = wintypes.HFONT
gdi32.CreateFontW.argtypes = [ctypes.c_int] * 5 + [wintypes.DWORD] * 8 + [wintypes.LPCWSTR]
gdi32.SetTextColor.argtypes = [wintypes.HDC, wintypes.COLORREF]
gdi32.SetBkMode.argtypes = [wintypes.HDC, ctypes.c_int]
gdi32.GetTextExtentPoint32W.argtypes = [wintypes.HDC, wintypes.LPCWSTR, ctypes.c_int, ctypes.POINTER(SIZE)]
gdi32.GdiFlush.restype = wintypes.BOOL
user32.DrawTextW.argtypes = [wintypes.HDC, wintypes.LPCWSTR, ctypes.c_int, ctypes.POINTER(wintypes.RECT), wintypes.UINT]
user32.CreateIconIndirect.restype = wintypes.HICON
user32.CreateIconIndirect.argtypes = [ctypes.POINTER(ICONINFO)]
user32.DestroyIcon.argtypes = [wintypes.HICON]

GREEN, YELLOW, RED, BLUE, GRAY = (0x4C, 0xD9, 0x64), (0xFF, 0xC1, 0x07), (0xF4, 0x43, 0x36), (0x4F, 0xC3, 0xF7), (0x9E, 0x9E, 0x9E)


def level_color(level, charging=False, online=True) -> tuple[int, int, int]:
    if not online or level is None:
        return GRAY
    if charging:
        return BLUE
    return GREEN if level >= 60 else YELLOW if level >= 30 else RED


def icon_size() -> int:
    return user32.GetSystemMetrics(SM_CXSMICON) or 16


def render(text: str, rgb: tuple[int, int, int], size: int, underline: bool = False) -> list[int]:
    """把文字画成 size x size 的预乘 alpha BGRA 像素(每个是 0xAARRGGBB)。"""
    hdc = gdi32.CreateCompatibleDC(None)
    bmi = BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), size, -size, 1, 32, 0)
    bits = ctypes.c_void_p()
    hbm = gdi32.CreateDIBSection(hdc, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    old_bm = gdi32.SelectObject(hdc, hbm)
    gdi32.SetBkMode(hdc, TRANSPARENT)
    gdi32.SetTextColor(hdc, 0xFFFFFF)

    # 从大到小找一个能放下文字的字号
    height = int(size * 1.05)
    while True:
        font = gdi32.CreateFontW(-height, 0, 0, 0, FW_BOLD, 0, 0, 0, 1, 0, 0, ANTIALIASED_QUALITY, 0, "Segoe UI")
        old_font = gdi32.SelectObject(hdc, font)
        ext = SIZE()
        gdi32.GetTextExtentPoint32W(hdc, text, len(text), ctypes.byref(ext))
        if ext.cx <= size + 1 or height <= 6:
            break
        gdi32.SelectObject(hdc, old_font)
        gdi32.DeleteObject(font)
        height -= 1
    rect = wintypes.RECT(0, -2 if underline else -1, size, size)
    user32.DrawTextW(hdc, text, len(text), ctypes.byref(rect), DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX)
    gdi32.GdiFlush()
    raw = list((ctypes.c_uint32 * (size * size)).from_address(bits.value))
    gdi32.SelectObject(hdc, old_font)
    gdi32.DeleteObject(font)
    gdi32.SelectObject(hdc, old_bm)
    gdi32.DeleteObject(hbm)
    gdi32.DeleteDC(hdc)

    # 白字的亮度当透明度, 换成目标颜色
    r, g, b = rgb
    px = []
    for v in raw:
        a = max(v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF)
        px.append((a << 24) | ((r * a // 255) << 16) | ((g * a // 255) << 8) | (b * a // 255) if a else 0)
    if underline:
        bar_h = max(1, size // 10)
        for y in range(size - bar_h, size):
            for x in range(size // 4, size - size // 4):
                px[y * size + x] = 0xFF000000 | (r << 16) | (g << 8) | b
    return px


def make_icon(text: str, rgb: tuple[int, int, int], underline: bool = False) -> int:
    """返回 HICON, 用完要 DestroyIcon。underline=True 时在底部画一条短横线(表示充电)。"""
    size = icon_size()
    px = render(text, rgb, size, underline)
    hdc = gdi32.CreateCompatibleDC(None)
    bmi = BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), size, -size, 1, 32, 0)
    bits = ctypes.c_void_p()
    hbm = gdi32.CreateDIBSection(hdc, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    ctypes.memmove(bits, (ctypes.c_uint32 * len(px))(*px), len(px) * 4)
    mask = gdi32.CreateBitmap(size, size, 1, 1, None)
    info = ICONINFO(True, 0, 0, mask, hbm)
    hicon = user32.CreateIconIndirect(ctypes.byref(info))
    gdi32.DeleteObject(hbm)
    gdi32.DeleteObject(mask)
    gdi32.DeleteDC(hdc)
    return hicon

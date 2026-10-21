"""设备电量托盘。

    pythonw main.py            托盘常驻(默认)
    python main.py --once      读一次, 打印后退出
    python main.py --plugins   列出插件及加载状态
    python main.py --hid       列出所有 HID 接口(写新插件时用)
    python main.py --install   复制到本机 %LOCALAPPDATA%\\Programs 并设为开机自启
    python main.py --uninstall 取消开机自启并删除本机副本
    加 --debug 在控制台输出调试信息
"""

from __future__ import annotations

import argparse
import ctypes
import logging
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
import winreg
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from core import config  # noqa: E402
from core.loader import load_plugins  # noqa: E402
from core.poller import DeviceState, Poller  # noqa: E402

log = logging.getLogger("main")

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_NAME = config.APP_NAME
INSTALL_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "Programs" / config.APP_NAME
INSTALL_ITEMS = ("main.py", "core", "winapi", "ui", "plugins", "README.md")


# ---------------------------------------------------------------- 开机自启
def _pythonw() -> Path:
    exe = Path(sys.executable)
    w = exe.with_name("pythonw.exe")
    return w if w.exists() else exe


def autostart_command(main_py: Path = ROOT / "main.py") -> str:
    return f'"{_pythonw()}" "{main_py}"'


def autostart_value() -> str:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            return winreg.QueryValueEx(k, RUN_NAME)[0]
    except OSError:
        return ""


def set_autostart(on: bool, main_py: Path = ROOT / "main.py") -> None:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        if on:
            winreg.SetValueEx(k, RUN_NAME, 0, winreg.REG_SZ, autostart_command(main_py))
        else:
            try:
                winreg.DeleteValue(k, RUN_NAME)
            except FileNotFoundError:
                pass


def install() -> None:
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    for name in INSTALL_ITEMS:
        src, dst = ROOT / name, INSTALL_DIR / name
        if src.is_dir():
            shutil.copytree(src, dst, dirs_exist_ok=True, ignore=shutil.ignore_patterns("__pycache__"))
        elif src.exists():
            shutil.copy2(src, dst)
    set_autostart(True, INSTALL_DIR / "main.py")
    print(f"已安装到 {INSTALL_DIR}, 并设为开机自启")
    subprocess.Popen([str(_pythonw()), str(INSTALL_DIR / "main.py")], cwd=INSTALL_DIR,
                     creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
    print("托盘已启动(已经在运行的话, 先从托盘菜单退出旧的再运行一次)")


def uninstall() -> None:
    set_autostart(False)
    if INSTALL_DIR.exists():
        shutil.rmtree(INSTALL_DIR)
    print(f"已取消开机自启, 删除 {INSTALL_DIR}; 配置文件留在 {config.DATA_DIR}")


# ---------------------------------------------------------------- 托盘程序
def _pad(text: str, width: int) -> str:
    """按显示宽度补空格(中文占两格)。"""
    import unicodedata
    w = sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in text)
    return text + " " * max(0, width - w)


def _fmt_level(d: DeviceState) -> str:
    return "--" if d.level is None else f"{d.level}%"


class App:
    def __init__(self, cfg: dict):
        import tkinter as tk
        from ui.panel import Panel
        from ui.plugins_window import PluginWindow
        from ui.tray import Tray

        self.cfg = cfg
        self.poller = Poller(load_plugins(cfg["disabled_plugins"]), cfg["interval"], self._on_change)
        self.q: queue.Queue[str] = queue.Queue()
        self.root = tk.Tk()
        self.root.withdraw()
        self.panel = Panel(self.root, self)
        self.plugin_window = PluginWindow(self.root, self)
        self.tray = Tray(self._tray_items, self._tray_menu, lambda key: self.q.put("show"))
        self._tray_thread = threading.Thread(target=self._tray_main, name="tray", daemon=True)

    def _tray_main(self) -> None:
        try:
            self.tray.run(lambda: self.q.put("show"))
        finally:
            self.q.put("quit")   # 托盘窗口没了(被关掉或出错), 整个程序跟着退出, 不留后台进程

    # ---- 设置 ----
    def autostart_enabled(self) -> bool:
        return bool(autostart_value())

    def set_autostart(self, on: bool) -> None:
        set_autostart(on)

    def set_interval(self, sec: int) -> None:
        self.cfg["interval"] = sec
        config.save(self.cfg)
        self.poller.set_interval(sec)

    def set_device_hidden(self, device_id: str, hidden: bool) -> None:
        ids = [i for i in self.cfg["hidden_devices"] if i != device_id] + ([device_id] if hidden else [])
        self.cfg["hidden_devices"] = ids
        config.save(self.cfg)
        self.tray.post_update()

    # ---- 插件(主线程里调用) ----
    def show_plugins(self) -> None:
        self.plugin_window.show()

    def reload_plugins(self) -> None:
        """重新扫描插件目录并加载, 添加/修改/移除插件后立即生效。"""
        self.poller.set_plugins(load_plugins(self.cfg["disabled_plugins"]))

    def set_plugin_enabled(self, plugin_id: str, on: bool) -> None:
        ids = [i for i in self.cfg["disabled_plugins"] if i != plugin_id] + ([] if on else [plugin_id])
        self.cfg["disabled_plugins"] = ids
        config.save(self.cfg)
        self.reload_plugins()

    # ---- 托盘回调(托盘线程里调用) ----
    def _tray_items(self):
        from ui.icon import level_color
        from ui.tray import TrayItem

        devices, _ = self.poller.snapshot()
        hidden = set(self.cfg["hidden_devices"])
        items = []
        for d in devices:
            if d.id in hidden:
                continue
            text = "-" if d.level is None else str(d.level)
            state = "充电中" if d.charging else "离线" if not d.online else ""
            when = time.strftime("%H:%M", time.localtime(d.updated)) if d.updated else "未读到"
            tip = f"{d.name}: {_fmt_level(d)} {state}\n{d.plugin_name} · {when}"
            items.append(TrayItem(d.id, text, level_color(d.level, d.charging, d.online), tip, d.charging))
        if not items:
            # 一个设备都没有时也留一个图标, 否则找不到程序入口
            tip = "设备电量: 正在扫描…" if self.poller.scanning else "设备电量: 没有发现设备"
            items.append(TrayItem("_placeholder", "?", (0x9E, 0x9E, 0x9E), tip))
        return items

    def _tray_menu(self):
        from ui.tray import MenuItem

        devices, _ = self.poller.snapshot()
        menu = [MenuItem(f"{d.name}    {_fmt_level(d)}{' ⚡' if d.charging else ''}{'' if d.online else ' (离线)'}",
                         enabled=False) for d in devices]
        if menu:
            menu.append(MenuItem())
        menu += [
            MenuItem("打开面板", lambda: self.q.put("show")),
            MenuItem("立即刷新", self.poller.refresh_now),
            MenuItem("插件管理…", lambda: self.q.put("plugins")),
            MenuItem("开机自启", lambda: set_autostart(not self.autostart_enabled()), checked=self.autostart_enabled()),
            MenuItem(),
            MenuItem("退出", lambda: self.q.put("quit")),
        ]
        return menu

    # ---- 主线程 ----
    def _on_change(self) -> None:
        self.tray.post_update()
        self.q.put("render")

    def _pump(self) -> None:
        try:
            while True:
                cmd = self.q.get_nowait()
                if cmd == "show":
                    self.panel.show()
                elif cmd == "plugins":
                    self.plugin_window.show()
                elif cmd == "render":
                    self.panel.render()
                    self.plugin_window.render()
                elif cmd == "quit":
                    self._quit()
                    return
        except queue.Empty:
            pass
        self.root.after(200, self._pump)

    def _tick(self) -> None:
        # 面板开着时每 30 秒重画一次, 让"x 分钟前更新"跟着走
        self.panel.render()
        self.root.after(30_000, self._tick)

    def _quit(self) -> None:
        self.tray.post_quit()
        if threading.current_thread() is not self._tray_thread:
            self._tray_thread.join(timeout=3)   # 等托盘删掉图标再退, 否则会留下"幽灵图标"
        self.poller.stop()
        self.root.destroy()

    def run(self) -> None:
        self._tray_thread.start()
        self.poller.start()
        self.root.after(200, self._pump)
        self.root.after(30_000, self._tick)
        self.root.mainloop()


# ---------------------------------------------------------------- 命令行
def cmd_once(cfg: dict) -> None:
    plugins = load_plugins(cfg["disabled_plugins"])
    poller = Poller(plugins, cfg["interval"], lambda: None)
    poller.scan_once()
    devices, status = poller.snapshot()
    for d in devices:
        flags = " 充电中" if d.charging else "" if d.online else " 离线"
        print(f"{_pad(d.name, 28)} {_fmt_level(d):>5}{_pad(flags, 8)} [{d.plugin_name}] {d.detail}")
    if not devices:
        print("没有发现设备")
    for s in status:
        if s.error:
            print(f"\n插件 {s.id} 出错:\n{s.error}")
    poller.stop()


def cmd_plugins(cfg: dict) -> None:
    for lp in load_plugins(cfg["disabled_plugins"]):
        state = "停用" if lp.disabled else "出错" if lp.error else "正常"
        name = lp.plugin.name if lp.plugin else ""
        print(f"[{state}] {lp.id:<20} {name:<12} {lp.path}")
        if lp.error:
            print("    " + lp.error.strip().replace("\n", "\n    "))
    print(f"\n用户插件目录: {config.USER_PLUGIN_DIR}")


def cmd_hid() -> None:
    from winapi import hid
    for info in sorted(hid.enumerate(), key=lambda i: (i.vid, i.pid, i.usage_page, i.usage)):
        print(info)


def main() -> None:
    ap = argparse.ArgumentParser(description="设备电量托盘")
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--once", action="store_true", help="读一次, 打印后退出")
    g.add_argument("--plugins", action="store_true", help="列出插件")
    g.add_argument("--hid", action="store_true", help="列出所有 HID 接口")
    g.add_argument("--install", action="store_true", help="安装到本机并开机自启")
    g.add_argument("--uninstall", action="store_true", help="取消开机自启并删除本机副本")
    ap.add_argument("--debug", action="store_true", help="控制台输出调试信息")
    args = ap.parse_args()

    if args.debug and sys.stderr:
        logging.basicConfig(level=logging.DEBUG, format="%(asctime)s %(name)s: %(message)s")
    elif sys.stderr:
        logging.basicConfig(level=logging.WARNING, format="%(name)s: %(message)s")
    else:
        logging.getLogger().addHandler(logging.NullHandler())   # pythonw 没有控制台, 不写日志文件

    cfg = config.load()
    if args.once:
        return cmd_once(cfg)
    if args.plugins:
        return cmd_plugins(cfg)
    if args.hid:
        return cmd_hid()
    if args.install:
        return install()
    if args.uninstall:
        return uninstall()

    from ui.tray import Tray
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    mutex = kernel32.CreateMutexW(None, False, f"Local\\{config.APP_NAME}")
    if ctypes.get_last_error() == 183:  # ERROR_ALREADY_EXISTS
        Tray.notify_running_instance()
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except (AttributeError, OSError):
        ctypes.windll.user32.SetProcessDPIAware()
    App(cfg).run()
    del mutex


if __name__ == "__main__":
    main()

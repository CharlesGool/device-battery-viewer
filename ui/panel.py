"""设备面板(tkinter): 每台设备一张卡片, 底部是插件状态、刷新间隔、开机自启。

只在主线程里调用; 其他线程的通知经 App 的队列转进来。
"""

from __future__ import annotations

import time
import tkinter as tk
from tkinter import ttk
from typing import TYPE_CHECKING

from core.config import INTERVALS
from .icon import level_color

if TYPE_CHECKING:
    from main import App

KIND_NAMES = {"mouse": "鼠标", "keyboard": "键盘", "headset": "耳机", "gamepad": "手柄", "speaker": "音箱", "other": "设备"}
BG, CARD, FG, SUB, TRACK = "#f5f6f8", "#ffffff", "#1f2329", "#6b7280", "#e5e7eb"


def _hex(rgb) -> str:
    return "#%02x%02x%02x" % rgb


def _ago(ts: float) -> str:
    if not ts:
        return "从未读到"
    s = int(time.time() - ts)
    if s < 60:
        return "刚刚"
    if s < 3600:
        return f"{s // 60} 分钟前"
    return time.strftime("%m-%d %H:%M", time.localtime(ts))


def _interval_label(sec: int) -> str:
    return f"{sec} 秒" if sec < 60 else f"{sec // 60} 分钟"


class Panel:
    def __init__(self, root: tk.Tk, app: "App"):
        self.app = app
        self.win = tk.Toplevel(root)
        self.win.title("设备电量")
        self.win.configure(bg=BG)
        self.win.minsize(380, 200)
        self.win.protocol("WM_DELETE_WINDOW", self.hide)
        style = ttk.Style(self.win)
        style.configure("Card.TCheckbutton", background=CARD, foreground=SUB, font=("Microsoft YaHei UI", 8))
        style.configure("TCheckbutton", background=BG)
        self.win.withdraw()

        head = tk.Frame(self.win, bg=BG)
        head.pack(fill="x", padx=14, pady=(12, 4))
        tk.Label(head, text="设备电量", bg=BG, fg=FG, font=("Microsoft YaHei UI", 13, "bold")).pack(side="left")
        self.btn_refresh = ttk.Button(head, text="刷新", command=app.poller.refresh_now, width=8)
        self.btn_refresh.pack(side="right")

        self.cards = tk.Frame(self.win, bg=BG)
        self.cards.pack(fill="both", expand=True, padx=14)

        self.plugins_label = tk.Label(self.win, bg=BG, fg=SUB, justify="left", anchor="w",
                                      font=("Microsoft YaHei UI", 9))
        self.plugins_label.pack(fill="x", padx=14, pady=(6, 0))
        self.plugins_label.bind("<Configure>", lambda e: self.plugins_label.configure(wraplength=max(e.width - 4, 100)))

        foot = tk.Frame(self.win, bg=BG)
        foot.pack(fill="x", padx=14, pady=(6, 12))
        tk.Label(foot, text="刷新间隔", bg=BG, fg=FG, font=("Microsoft YaHei UI", 9)).pack(side="left")
        self._intervals = {_interval_label(s): s for s in INTERVALS}
        self.interval_var = tk.StringVar(value=_interval_label(app.cfg["interval"]))
        box = ttk.Combobox(foot, textvariable=self.interval_var, values=list(self._intervals), width=8, state="readonly")
        box.pack(side="left", padx=6)
        box.bind("<<ComboboxSelected>>", lambda e: app.set_interval(self._intervals[self.interval_var.get()]))
        self.autostart_var = tk.BooleanVar(value=app.autostart_enabled())
        ttk.Checkbutton(foot, text="开机自启", variable=self.autostart_var,
                        command=lambda: app.set_autostart(self.autostart_var.get())).pack(side="right")

        self._last = None

    def show(self) -> None:
        self.autostart_var.set(self.app.autostart_enabled())
        self.render(force=True)
        self.win.deiconify()
        self.win.lift()
        self.win.attributes("-topmost", True)
        self.win.after(200, lambda: self.win.attributes("-topmost", False))
        self.win.focus_force()

    def hide(self) -> None:
        self.win.withdraw()

    def visible(self) -> bool:
        return self.win.state() == "normal"

    def render(self, force: bool = False) -> None:
        if not force and not self.visible():
            return
        devices, plugins = self.app.poller.snapshot()
        self.btn_refresh.configure(text="刷新中…" if self.app.poller.scanning else "刷新")

        for w in self.cards.winfo_children():
            w.destroy()
        if not devices:
            tk.Label(self.cards, text="还没发现设备\n\n罗技接收器插好、鼠标动一下;蓝牙设备先在系统里连上",
                     bg=BG, fg=SUB, font=("Microsoft YaHei UI", 10), justify="center").pack(pady=30)
        hidden = set(self.app.cfg["hidden_devices"])
        for d in devices:
            self._card(d, d.id not in hidden)

        lines = []
        for p in plugins:
            if p.disabled:
                lines.append(f"{p.name}: 已停用")
            elif p.error:
                lines.append(f"{p.name}: 出错 - {p.error.strip().splitlines()[-1]}")
            else:
                lines.append(f"{p.name}: {p.device_count} 台, {p.last_scan_ms} ms")
        self.plugins_label.configure(text="插件  " + "  ·  ".join(lines))

    def _card(self, d, shown_in_tray: bool) -> None:
        card = tk.Frame(self.cards, bg=CARD, highlightthickness=1, highlightbackground=TRACK)
        card.pack(fill="x", pady=4)
        color = _hex(level_color(d.level, d.charging, d.online))

        top = tk.Frame(card, bg=CARD)
        top.pack(fill="x", padx=12, pady=(10, 2))
        tk.Label(top, text=KIND_NAMES.get(d.kind, "设备"), bg=TRACK, fg=SUB, padx=5,
                 font=("Microsoft YaHei UI", 8)).pack(side="left")
        tk.Label(top, text=d.name, bg=CARD, fg=FG, font=("Microsoft YaHei UI", 11, "bold")).pack(side="left", padx=6)
        pct = "--" if d.level is None else f"{d.level}%"
        tk.Label(top, text=pct, bg=CARD, fg=color, font=("Segoe UI", 18, "bold")).pack(side="right")

        bar = tk.Canvas(card, height=6, bg=TRACK, highlightthickness=0)
        bar.pack(fill="x", padx=12, pady=2)
        level = d.level or 0
        bar.bind("<Configure>", lambda e, b=bar: (b.delete("all"),
                                                  b.create_rectangle(0, 0, e.width * level / 100, 6, fill=color, width=0)))

        bits = [d.plugin_name]
        if d.charging:
            bits.append("充电中")
        if not d.online:
            bits.append("离线")
        if d.detail:
            bits.append(d.detail)
        bits.append(f"{_ago(d.updated)}更新" if d.updated else "从未读到电量")
        bottom = tk.Frame(card, bg=CARD)
        bottom.pack(fill="x", padx=12, pady=(2, 8))
        tk.Label(bottom, text=" · ".join(bits), bg=CARD, fg=SUB, font=("Microsoft YaHei UI", 8)).pack(side="left")
        var = tk.BooleanVar(value=shown_in_tray)
        ttk.Checkbutton(bottom, text="托盘图标", variable=var, style="Card.TCheckbutton",
                        command=lambda: self.app.set_device_hidden(d.id, not var.get())).pack(side="right")
        card._var = var  # 防止被回收

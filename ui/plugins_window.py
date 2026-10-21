"""插件管理窗口: 看每个插件的状态, 启用/停用, 添加插件文件, 从模板新建, 移除, 重新加载。"""

from __future__ import annotations

import os
import shutil
import subprocess
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk
from typing import TYPE_CHECKING

from core.config import USER_PLUGIN_DIR
from core.loader import PluginFileError, install_plugin_file, new_plugin_from_template, user_plugin_path

if TYPE_CHECKING:
    from main import App

BG, CARD, FG, SUB, LINE, RED, GREEN = "#f5f6f8", "#ffffff", "#1f2329", "#6b7280", "#e5e7eb", "#d93025", "#1e8e3e"
FONT = "Microsoft YaHei UI"


def open_in_editor(path: Path) -> None:
    """用 VS Code 打开, 没有就用记事本。不用 os.startfile: .py 的默认动作是"运行"。"""
    editor = shutil.which("code") or "notepad.exe"
    subprocess.Popen([editor, str(path)], creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))


def open_folder(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    os.startfile(path)


class PluginWindow:
    def __init__(self, root: tk.Tk, app: "App"):
        self.app = app
        self.win = tk.Toplevel(root)
        self.win.title("插件管理")
        self.win.configure(bg=BG)
        self.win.minsize(520, 300)
        self.win.protocol("WM_DELETE_WINDOW", self.win.withdraw)
        self.win.withdraw()

        tk.Label(self.win, text="插件管理", bg=BG, fg=FG, font=(FONT, 13, "bold")).pack(anchor="w", padx=14, pady=(12, 0))
        tk.Label(self.win, text="每种设备由一个插件负责读电量。插件是一个 .py 文件, 添加后立即生效, 不用重启。",
                 bg=BG, fg=SUB, font=(FONT, 9)).pack(anchor="w", padx=14, pady=(2, 8))

        self.list = tk.Frame(self.win, bg=BG)
        self.list.pack(fill="both", expand=True, padx=14)

        bar = tk.Frame(self.win, bg=BG)
        bar.pack(fill="x", padx=14, pady=12)
        ttk.Button(bar, text="添加插件文件…", command=self.add_file).pack(side="left")
        ttk.Button(bar, text="从模板新建…", command=self.new_from_template).pack(side="left", padx=6)
        ttk.Button(bar, text="打开插件目录", command=lambda: open_folder(USER_PLUGIN_DIR)).pack(side="left")
        ttk.Button(bar, text="重新加载", command=self.reload).pack(side="right")

        style = ttk.Style(self.win)
        style.configure("Plugin.TCheckbutton", background=CARD)

    def show(self) -> None:
        self.render(force=True)
        self.win.deiconify()
        self.win.lift()
        self.win.focus_force()

    def render(self, force: bool = False) -> None:
        if not force and self.win.state() != "normal":
            return
        _, plugins = self.app.poller.snapshot()
        for w in self.list.winfo_children():
            w.destroy()
        for p in sorted(plugins, key=lambda p: (not p.builtin, p.name)):
            self._row(p)

    def _row(self, p) -> None:
        row = tk.Frame(self.list, bg=CARD, highlightthickness=1, highlightbackground=LINE)
        row.pack(fill="x", pady=3)

        top = tk.Frame(row, bg=CARD)
        top.pack(fill="x", padx=10, pady=(8, 0))
        var = tk.BooleanVar(value=not p.disabled)
        cb = ttk.Checkbutton(top, variable=var, style="Plugin.TCheckbutton",
                             command=lambda: self.app.set_plugin_enabled(p.id, var.get()))
        cb.pack(side="left")
        cb._var = var
        if p.error and not p.name:
            cb.state(["disabled"])
        tk.Label(top, text=p.name, bg=CARD, fg=FG, font=(FONT, 10, "bold")).pack(side="left")
        tk.Label(top, text="内置" if p.builtin else "自定义", bg=LINE, fg=SUB, padx=5,
                 font=(FONT, 8)).pack(side="left", padx=6)

        if not p.builtin:
            ttk.Button(top, text="移除", width=6, command=lambda: self.remove(p)).pack(side="right")
            ttk.Button(top, text="编辑", width=6, command=lambda: open_in_editor(Path(p.path))).pack(side="right", padx=4)
        if p.error:
            ttk.Button(top, text="错误详情", command=lambda: self.show_error(p)).pack(side="right")

        if p.disabled:
            state, color = "已停用", SUB
        elif p.error:
            state, color = "出错: " + p.error.strip().splitlines()[-1][:80], RED
        elif not p.scanned:
            state, color = "等待扫描…", SUB
        else:
            state, color = f"{p.device_count} 台设备 · {p.last_scan_ms} ms", GREEN
        tk.Label(row, text=state, bg=CARD, fg=color, font=(FONT, 9), anchor="w").pack(fill="x", padx=34)
        desc = p.description or Path(p.path).name
        label = tk.Label(row, text=desc, bg=CARD, fg=SUB, font=(FONT, 8), anchor="w", justify="left")
        label.pack(fill="x", padx=34, pady=(0, 8))
        label.bind("<Configure>", lambda e: label.configure(wraplength=max(e.width - 4, 100)))

    # ---- 操作 ----
    def reload(self) -> None:
        self.app.reload_plugins()

    def add_file(self) -> None:
        path = filedialog.askopenfilename(parent=self.win, title="选择插件文件",
                                          filetypes=[("Python 插件", "*.py")])
        if not path:
            return
        src = Path(path)
        dest = user_plugin_path(src)
        if dest.exists() and dest.resolve() != src.resolve():
            if not messagebox.askyesno("插件已存在", f"{dest.name} 已经存在, 要替换吗?", parent=self.win):
                return
        try:
            loaded = install_plugin_file(src)
        except PluginFileError as e:
            messagebox.showerror("不能添加这个插件", str(e), parent=self.win)
            return
        self.app.reload_plugins()
        names = "、".join(lp.plugin.name for lp in loaded)
        messagebox.showinfo("已添加", f"已添加插件: {names}\n\n文件复制到了 {dest}", parent=self.win)

    def new_from_template(self) -> None:
        name = simpledialog.askstring("从模板新建插件", "插件文件名(英文, 如 my_mouse):", parent=self.win)
        if not name:
            return
        try:
            path = new_plugin_from_template(name.strip())
        except PluginFileError as e:
            messagebox.showerror("不能新建", str(e), parent=self.win)
            return
        self.app.reload_plugins()
        open_in_editor(path)
        messagebox.showinfo("已新建", f"已建好 {path.name} 并用编辑器打开。\n\n"
                            "按注释改成你的设备, 保存后回到这里点「重新加载」。\n"
                            "写法见 README 的「写一个插件」。", parent=self.win)

    def remove(self, p) -> None:
        if not messagebox.askyesno("移除插件", f"移除「{p.name}」?\n\n文件会挪到插件目录下的 _removed 文件夹, 需要时可以挪回来。",
                                   parent=self.win):
            return
        src = Path(p.path)
        trash = USER_PLUGIN_DIR / "_removed"
        trash.mkdir(parents=True, exist_ok=True)
        dest = trash / src.name
        if dest.exists():
            dest = trash / f"{src.stem}_{os.getpid()}{src.suffix}"
        shutil.move(str(src), dest)
        self.app.reload_plugins()

    def show_error(self, p) -> None:
        top = tk.Toplevel(self.win)
        top.title(f"{p.name} - 错误详情")
        text = tk.Text(top, width=90, height=18, font=("Consolas", 9), wrap="none")
        text.insert("1.0", f"{p.path}\n\n{p.error}")
        text.configure(state="disabled")
        text.pack(fill="both", expand=True, padx=8, pady=8)

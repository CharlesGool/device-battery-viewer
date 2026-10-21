"""从插件目录加载插件。

规则: 目录里每个不以 "_" 开头的 .py 文件是一个插件模块, 模块里定义的每个 Plugin 子类都会被实例化。
内置目录 plugins/ 先加载, 用户目录里同 id 的插件会覆盖内置的。
"""

from __future__ import annotations

import importlib.util
import inspect
import re
import shutil
import sys
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from .config import USER_PLUGIN_DIR
from .plugin import Plugin

BUILTIN_PLUGIN_DIR = Path(__file__).resolve().parent.parent / "plugins"
TEMPLATE_PATH = BUILTIN_PLUGIN_DIR / "_template.py"


@dataclass(eq=False)
class LoadedPlugin:
    id: str
    path: Path
    plugin: Optional[Plugin] = None
    error: str = ""          # 加载失败的原因
    disabled: bool = False
    builtin: bool = True


_counter = 0


def _load_file(path: Path) -> list[LoadedPlugin]:
    # 每次加载用新的模块名, 重新加载插件时拿到的是改过的代码
    global _counter
    _counter += 1
    mod_name = f"dbv_plugin_{_counter}_{path.stem}"
    try:
        spec = importlib.util.spec_from_file_location(mod_name, path)
        module = importlib.util.module_from_spec(spec)
        sys.modules[mod_name] = module
        spec.loader.exec_module(module)
    except Exception:
        return [LoadedPlugin(path.stem, path, error=traceback.format_exc(limit=3))]

    classes = [c for _, c in inspect.getmembers(module, inspect.isclass)
               if issubclass(c, Plugin) and c is not Plugin and c.__module__ == mod_name]
    if not classes:
        return [LoadedPlugin(path.stem, path, error="文件里没有找到 Plugin 的子类")]
    out = []
    for cls in classes:
        pid = cls.id or (path.stem if len(classes) == 1 else f"{path.stem}.{cls.__name__}")
        try:
            inst = cls()
            inst.id = pid
            inst.name = inst.name or pid
            out.append(LoadedPlugin(pid, path, inst))
        except Exception:
            out.append(LoadedPlugin(pid, path, error=traceback.format_exc(limit=3)))
    return out


def load_plugins(disabled: list[str]) -> list[LoadedPlugin]:
    found: dict[str, LoadedPlugin] = {}
    for folder, builtin in ((BUILTIN_PLUGIN_DIR, True), (USER_PLUGIN_DIR, False)):
        if not folder.is_dir():
            continue
        for path in sorted(folder.glob("*.py")):
            if path.name.startswith("_"):
                continue
            for lp in _load_file(path):
                lp.builtin = builtin
                found[lp.id] = lp
    for lp in found.values():
        if lp.id in disabled:
            lp.disabled = True
    return list(found.values())


class PluginFileError(Exception):
    pass


def user_plugin_path(src: Path) -> Path:
    """插件文件复制到用户目录后的路径(去掉开头的 _, 否则不会被加载)。"""
    return USER_PLUGIN_DIR / (src.name.lstrip("_") or "plugin.py")


def install_plugin_file(src: Path) -> list[LoadedPlugin]:
    """检查 src 是不是能用的插件, 能用就复制到用户插件目录; 不能用抛 PluginFileError。"""
    if src.suffix.lower() != ".py":
        raise PluginFileError("插件是一个 .py 文件")
    checked = _load_file(src)
    errors = [lp.error for lp in checked if lp.error]
    if errors:
        raise PluginFileError(errors[0])
    for lp in checked:
        lp.plugin.close()
    USER_PLUGIN_DIR.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, user_plugin_path(src))
    return checked


def new_plugin_from_template(name: str) -> Path:
    """按模板在用户插件目录里建一个新插件文件, 返回路径。name 只能是英文、数字、下划线。"""
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", name):
        raise PluginFileError("名字只能用英文字母、数字和下划线, 并以字母开头")
    dest = USER_PLUGIN_DIR / f"{name}.py"
    if dest.exists():
        raise PluginFileError(f"{dest.name} 已经存在")
    USER_PLUGIN_DIR.mkdir(parents=True, exist_ok=True)
    cls = "".join(p.capitalize() for p in name.split("_")) or "MyPlugin"
    text = TEMPLATE_PATH.read_text("utf-8").replace("class ExampleMouse(", f"class {cls}(")
    text = text.replace('name = "示例鼠标"', f'name = "{name}"')
    dest.write_text(text, "utf-8")
    return dest

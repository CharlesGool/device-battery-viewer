"""从插件目录加载插件。

规则: 目录里每个不以 "_" 开头的 .py 文件是一个插件模块, 模块里定义的每个 Plugin 子类都会被实例化。
内置目录 plugins/ 先加载, 用户目录里同 id 的插件会覆盖内置的。
"""

from __future__ import annotations

import importlib.util
import inspect
import sys
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from .config import USER_PLUGIN_DIR
from .plugin import Plugin

BUILTIN_PLUGIN_DIR = Path(__file__).resolve().parent.parent / "plugins"


@dataclass
class LoadedPlugin:
    id: str
    path: Path
    plugin: Optional[Plugin] = None
    error: str = ""          # 加载失败的原因
    disabled: bool = False


def _load_file(path: Path) -> list[LoadedPlugin]:
    mod_name = f"dbv_plugin_{path.parent.name}_{path.stem}"
    try:
        spec = importlib.util.spec_from_file_location(mod_name, path)
        module = importlib.util.module_from_spec(spec)
        sys.modules[mod_name] = module
        spec.loader.exec_module(module)
    except Exception:
        return [LoadedPlugin(path.stem, path, error=traceback.format_exc(limit=3))]

    classes = [c for _, c in inspect.getmembers(module, inspect.isclass)
               if issubclass(c, Plugin) and c is not Plugin and c.__module__ == mod_name]
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
    for folder in (BUILTIN_PLUGIN_DIR, USER_PLUGIN_DIR):
        if not folder.is_dir():
            continue
        for path in sorted(folder.glob("*.py")):
            if path.name.startswith("_"):
                continue
            for lp in _load_file(path):
                found[lp.id] = lp
    for lp in found.values():
        if lp.id in disabled:
            lp.disabled = True
    return list(found.values())

"""配置文件: %APPDATA%\\DeviceBatteryViewer\\config.json"""

from __future__ import annotations

import json
import os
from pathlib import Path

APP_NAME = "DeviceBatteryViewer"
DATA_DIR = Path(os.environ.get("APPDATA", Path.home())) / APP_NAME
CONFIG_PATH = DATA_DIR / "config.json"
USER_PLUGIN_DIR = DATA_DIR / "plugins"
STATE_PATH = DATA_DIR / "devices.json"   # 上次读到的设备和电量, 重启后离线设备也能显示

DEFAULTS = {
    "interval": 60,             # 刷新间隔, 秒
    "disabled_plugins": [],     # 不加载的插件 id
    "hidden_devices": [],       # 不显示托盘图标的设备 id("插件id:key")
}

INTERVALS = (30, 60, 120, 300, 600, 1800)


def load() -> dict:
    cfg = dict(DEFAULTS)
    try:
        cfg.update(json.loads(CONFIG_PATH.read_text("utf-8")))
    except (OSError, ValueError):
        pass
    return cfg


def write_json(path: Path, data) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), "utf-8")
    tmp.replace(path)


def save(cfg: dict) -> None:
    write_json(CONFIG_PATH, cfg)

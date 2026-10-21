"""后台轮询: 按间隔依次调用各插件的 scan(), 合并成设备状态表, 变化时通知界面。"""

from __future__ import annotations

import json
import logging
import threading
import time
import traceback
from dataclasses import dataclass
from typing import Callable, Optional

from . import config
from .loader import LoadedPlugin
from .plugin import Reading

log = logging.getLogger(__name__)


@dataclass
class DeviceState:
    id: str                       # "插件id:key"
    plugin_id: str
    plugin_name: str
    name: str
    kind: str = "other"
    level: Optional[int] = None   # 最后一次读到的电量(离线时保留)
    charging: bool = False
    online: bool = False
    detail: str = ""
    updated: float = 0.0          # 最后一次读到电量的时间戳
    seen: float = 0.0             # 最后一次被插件报告的时间戳


@dataclass
class PluginStatus:
    id: str
    name: str
    description: str = ""
    path: str = ""
    builtin: bool = True
    error: str = ""
    disabled: bool = False
    scanned: bool = False         # 启用后是否已经扫描过一轮
    last_scan_ms: int = 0
    device_count: int = 0


def _status_of(lp: LoadedPlugin) -> PluginStatus:
    p = lp.plugin
    return PluginStatus(lp.id, p.name if p else lp.id, p.description if p else "", str(lp.path),
                        lp.builtin, lp.error, lp.disabled)


def _active(lp: LoadedPlugin) -> bool:
    return lp.plugin is not None and not lp.disabled


class Poller:
    def __init__(self, plugins: list[LoadedPlugin], interval: int, on_change: Callable[[], None]):
        self.plugins = plugins
        self.interval = interval
        self.on_change = on_change
        self.devices: dict[str, DeviceState] = {}
        self.status: dict[str, PluginStatus] = {lp.id: _status_of(lp) for lp in plugins}
        self.lock = threading.Lock()
        self._scan_lock = threading.Lock()
        self._retired: list[LoadedPlugin] = []   # 被替换下来、等当前这轮扫描结束再关闭的插件
        self._wake = threading.Event()
        self._stop = False
        self.scanning = False
        self._saved = ""
        self._load_state()
        self._thread = threading.Thread(target=self._run, name="poller", daemon=True)

    def _load_state(self) -> None:
        try:
            saved = json.loads(config.STATE_PATH.read_text("utf-8"))
        except (OSError, ValueError):
            return
        names = {lp.id: lp.plugin.name for lp in self.plugins if _active(lp)}
        for item in saved:
            if item.get("plugin_id") in names:
                fields = {k: v for k, v in item.items() if k in DeviceState.__dataclass_fields__}
                fields.update(plugin_name=names[item["plugin_id"]], online=False, charging=False)
                d = DeviceState(**fields)
                self.devices[d.id] = d

    def _save_state(self) -> None:
        keep = ("id", "plugin_id", "name", "kind", "level", "updated")
        with self.lock:
            data = [{k: getattr(d, k) for k in keep} for d in self.devices.values() if d.updated]
        text = json.dumps(data, sort_keys=True)
        if text != self._saved:
            try:
                config.write_json(config.STATE_PATH, data)
                self._saved = text
            except OSError:
                log.exception("保存设备状态失败")

    def start(self) -> None:
        self._thread.start()

    def refresh_now(self) -> None:
        self._wake.set()

    def set_interval(self, seconds: int) -> None:
        self.interval = seconds
        self._wake.set()

    def set_plugins(self, plugins: list[LoadedPlugin]) -> None:
        """换成新加载的插件列表(添加、启停、重新加载插件后调用), 并立即扫描一轮。"""
        with self.lock:
            self._retired += self.plugins
            self.plugins = plugins
            self.status = {lp.id: _status_of(lp) for lp in plugins}
            names = {lp.id: lp.plugin.name for lp in plugins if _active(lp)}
            # 停用或删掉的插件, 它的设备也一起去掉
            self.devices = {k: d for k, d in self.devices.items() if d.plugin_id in names}
            for d in self.devices.values():
                d.plugin_name = names[d.plugin_id]
        if self._scan_lock.acquire(blocking=False):
            try:
                self._close_retired()
            finally:
                self._scan_lock.release()
        self.on_change()
        self._wake.set()

    def _close_retired(self) -> None:
        with self.lock:
            retired, self._retired = self._retired, []
        for lp in retired:
            if lp.plugin:
                try:
                    lp.plugin.close()
                except Exception:
                    log.exception("关闭插件 %s 失败", lp.id)

    def stop(self) -> None:
        self._stop = True
        self._wake.set()
        if self._thread.is_alive():
            self._thread.join(timeout=5)
        with self.lock:
            self._retired += self.plugins
        self._close_retired()

    def snapshot(self) -> tuple[list[DeviceState], list[PluginStatus]]:
        with self.lock:
            devs = sorted(self.devices.values(), key=lambda d: (d.plugin_id, d.name))
            return [DeviceState(**d.__dict__) for d in devs], [PluginStatus(**s.__dict__) for s in self.status.values()]

    def scan_once(self) -> None:
        with self._scan_lock:
            self.scanning = True
            self.on_change()
            try:
                with self.lock:
                    plugins = [lp for lp in self.plugins if _active(lp)]
                for lp in plugins:
                    self._scan_plugin(lp)
            finally:
                self.scanning = False
                self._close_retired()
        self._save_state()
        self.on_change()

    def _scan_plugin(self, lp: LoadedPlugin) -> None:
        t0 = time.monotonic()
        try:
            readings = lp.plugin.scan()
            err = ""
        except Exception:
            readings, err = None, traceback.format_exc(limit=4)
            log.debug("插件 %s 出错:\n%s", lp.id, err)
        ms = int((time.monotonic() - t0) * 1000)
        with self.lock:
            if lp not in self.plugins:
                return  # 扫描期间插件被替换了, 结果作废
            st = self.status[lp.id]
            st.error, st.last_scan_ms, st.scanned = err, ms, True
            if readings is not None:
                self._merge(lp, readings)
                st.device_count = len(readings)
        log.debug("插件 %s: %s 台设备, %d ms", lp.id, "?" if readings is None else len(readings), ms)

    def _merge(self, lp: LoadedPlugin, readings: list[Reading]) -> None:
        now = time.time()
        reported = set()
        for r in readings:
            did = f"{lp.id}:{r.key}"
            reported.add(did)
            d = self.devices.get(did)
            if d is None:
                d = self.devices[did] = DeviceState(did, lp.id, lp.plugin.name, r.name)
            d.name, d.kind, d.detail, d.seen = r.name, r.kind, r.detail, now
            d.online = r.online
            if r.level is not None:
                d.level = max(0, min(100, int(r.level)))
                d.charging = r.charging
                d.updated = now
            elif not r.online:
                d.charging = False
        # 这次没报告的设备(拔掉了接收器、蓝牙断开)标为离线, 保留最后的电量
        for did, d in self.devices.items():
            if d.plugin_id == lp.id and did not in reported:
                d.online = False
                d.charging = False

    def _run(self) -> None:
        while not self._stop:
            self.scan_once()
            self._wake.wait(self.interval)
            self._wake.clear()

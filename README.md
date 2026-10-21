# 设备电量托盘

Windows 通知区域显示无线设备电量: 每台设备一个托盘图标, 图标就是电量数字。参考
[2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer) 的思路重写, 改成插件结构, 新设备写一个插件文件就能接进来。

**纯 Python 标准库 + ctypes**, 不装任何第三方包。

## 支持的设备

| 插件 | 设备 | 原理 |
|---|---|---|
| `logitech_hidpp` | 罗技 G502 X LIGHTSPEED(已真机验证), 以及 LIGHTSPEED / Unifying / Bolt 接收器上或有线直连的 HID++ 2.0 设备 | HID++ 2.0, 特性 `0x1004` 统一电池 → `0x1000` 电池状态 → `0x1001` 电压, 和 Linux 内核 `hid-logitech-hidpp`、Solaar 同一套协议 |
| `bluetooth` | Windows 设置里能看到电量的蓝牙设备(耳机、手柄、BLE 鼠标键盘等) | 读系统写在设备节点上的 PnP 属性 `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` |

## 使用

需要 Windows 10/11 + Python 3.10+(官方安装包自带 tkinter)。

```bash
python main.py --once      # 读一次, 打印后退出
pythonw main.py            # 托盘常驻
python main.py --install   # 复制到本机 %LOCALAPPDATA%\Programs\DeviceBatteryViewer 并开机自启
python main.py --uninstall # 取消开机自启并删除本机副本
python main.py --plugins   # 列出插件和加载状态
python main.py --hid       # 列出本机所有 HID 接口(写插件时用)
```

加 `--debug` 在控制台看每个插件的耗时和报错。

仓库在 NAS 上, 常驻运行请用 `--install` 装到本机再跑, 不要直接从 NAS 路径开机自启。

**托盘操作**: 左键打开面板, 右键菜单(各设备电量、立即刷新、插件管理、开机自启、退出)。面板里可以改刷新间隔(30 秒 - 30 分钟)、单独关掉某台设备的托盘图标。

**图标颜色**: 绿 ≥60%, 黄 30-59%, 红 <30%, 蓝 = 充电中(底部带横线), 灰 = 离线(显示上次读到的电量)。

**离线设备**: 无线鼠标休眠后接收器不再转发请求, 这时保留上次读到的电量并标为离线。读到过的设备记在 `%APPDATA%\DeviceBatteryViewer\devices.json`, 重启后也能显示。

## 管理插件

面板右上角「插件…」或托盘右键「插件管理…」打开插件管理窗口, 所有操作立即生效, 不用重启:

- **勾选框**: 启用 / 停用插件(停用后它的设备也从托盘消失)。
- **添加插件文件…**: 选一个别人写好的 `.py` 插件, 先检查能不能加载, 能用才复制到用户插件目录。
- **从模板新建…**: 起个英文名, 按模板生成插件文件并用编辑器(VS Code, 没有就记事本)打开; 改好保存后点「重新加载」。
- **编辑 / 移除**: 只对自定义插件; 移除是把文件挪到插件目录下的 `_removed\`, 可以挪回来。
- **错误详情**: 插件加载或读取出错时显示完整报错。
- **打开插件目录**: `%APPDATA%\DeviceBatteryViewer\plugins\`, 直接往里放文件后点「重新加载」也行。

## 写一个插件

1. 在插件管理里「从模板新建…」, 或手动复制 [plugins/_template.py](plugins/_template.py) 并改个不以 `_` 开头的文件名。
2. 放到 `%APPDATA%\DeviceBatteryViewer\plugins\`(重装程序不会覆盖; 和内置插件同 id 时覆盖内置的)。
3. 实现 `scan()`, 返回 `Reading` 列表:

```python
from core.plugin import Plugin, Reading
from winapi import hid

class MyMouse(Plugin):
    name = "我的鼠标"

    def scan(self) -> list[Reading]:
        out = []
        for info in hid.enumerate(vid=0x1234, usage_page=0xFF05):
            with hid.HidDevice(info.path) as dev:
                level = hid.transact([dev], lambda: dev.write(b"\x08\x7d..."),
                                     lambda d: d[7] if d[1] == 0x72 else None)
            out.append(Reading(key=info.serial, name=info.product, level=level,
                               online=level is not None, kind="mouse"))
        return out
```

4. 在插件管理里点「重新加载」看结果; 或用 `python main.py --once --debug` 在控制台看详细报错。

约定:

- `key` 在插件内唯一且稳定(序列号、蓝牙地址), 用来跨刷新对应同一台设备。
- `level=None, online=False` 表示设备不在线, 界面会保留上次的电量。
- `scan()` 在后台线程里按间隔调用, 可以阻塞, 抛异常只影响这个插件(面板底部会显示错误)。
- 配置文件 `%APPDATA%\DeviceBatteryViewer\config.json` 里的 `disabled_plugins` 可以停用插件。

插件可用的系统接口:

| 模块 | 用途 |
|---|---|
| `winapi.hid` | `enumerate()` 列 HID 接口; `HidDevice` 读写报文 / Feature Report; `transact()` 发请求等匹配回复; `read_any()` 同时在多个接口上等报文 |
| `winapi.devprop` | `list_devices(枚举器, {名字: 属性键})` 列 PnP 设备节点并读属性 |

## 目录

| 路径 | 说明 |
|---|---|
| `main.py` | 入口: 托盘程序、命令行、开机自启、安装 |
| `core/plugin.py` | 插件接口 `Plugin` / `Reading` |
| `core/loader.py` | 从插件目录加载插件 |
| `core/poller.py` | 后台轮询、合并设备状态、离线保留、状态持久化 |
| `core/config.py` | 配置和数据目录 |
| `winapi/hid.py` | HID 访问(setupapi / hid.dll, overlapped I/O) |
| `winapi/devprop.py` | PnP 设备属性 |
| `ui/tray.py` | 托盘图标(Shell_NotifyIconW, 独立线程消息循环) |
| `ui/icon.py` | 把数字画成图标(GDI) |
| `ui/panel.py` | 设备面板(tkinter) |
| `ui/plugins_window.py` | 插件管理窗口 |
| `plugins/` | 内置插件 |

## 隐私

不联网, 不写日志文件。只在 `%APPDATA%\DeviceBatteryViewer\` 下存配置和上次读到的电量。

# 设备电量托盘

Windows 通知区域显示无线设备电量: 每台设备一个托盘图标, 图标就是电量数字。参考
[2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer) 的思路重写, 做成插件结构, 新设备写一个插件文件就能接进来。

单个 exe, 原生 Windows 界面(WinForms), 只依赖 Windows 自带的 .NET Framework 4.x, 不用装任何东西。

## 支持的设备

| 插件 | 设备 | 原理 |
|---|---|---|
| 罗技 HID++(内置) | 罗技 G502 X LIGHTSPEED(已真机验证), 以及 LIGHTSPEED / Unifying / Bolt 接收器上或有线直连的 HID++ 2.0 设备 | HID++ 2.0, 特性 `0x1004` 统一电池 → `0x1000` 电池状态 → `0x1001` 电压, 和 Linux 内核 `hid-logitech-hidpp`、Solaar 同一套协议 |
| 蓝牙(内置) | Windows 设置里能看到电量的蓝牙设备(耳机、手柄、BLE 鼠标键盘等) | 读系统写在设备节点上的 PnP 属性 `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`(未真机验证) |
| 自定义插件 | 你写的 | 见下文 |

## 使用

1. 从 [Releases](../../releases) 下载 `DeviceBatteryViewer.exe`, 或按下文自行编译
2. 双击运行, 面板会出现在屏幕右下角, 程序常驻托盘
3. 需要开机自启的话勾选「开机自启」, 或者用 `--install` 装到固定位置

```bash
DeviceBatteryViewer.exe              # 打开面板, 常驻托盘
DeviceBatteryViewer.exe --install    # 复制到 %LOCALAPPDATA%\Programs\DeviceBatteryViewer 并开机自启
DeviceBatteryViewer.exe --uninstall  # 取消开机自启并删除本机副本
DeviceBatteryViewer.exe --once       # 读一次电量, 打印后退出
DeviceBatteryViewer.exe --hid        # 列出本机所有 HID 接口(写插件时找 VID / PID / usage page)
```

- **托盘**: 左键打开面板; 右键菜单有各设备电量、立即刷新、插件管理、开机自启、退出。
- **颜色**: 充电中(包括充满后还插着线)绿色并带 ⚡ / 托盘图标底部带横线; 用电池时蓝色, 低于 30% 黄色, 低于 15% 红色; 离线灰色(显示上次读到的电量)。
- **面板**: 每台设备一行, 可以单独关掉某台设备的托盘图标, 改刷新间隔(30 秒 - 30 分钟)。
- **离线设备**: 无线鼠标休眠后接收器不再转发请求, 这时保留上次读到的电量并标为离线。读到过的设备记在 `%APPDATA%\DeviceBatteryViewer\devices.json`, 重启后也能显示。

## 管理插件

面板上的「插件…」或托盘菜单「插件管理…」, 所有操作立即生效, 不用重启:

- **勾选框**: 启用 / 停用插件(停用后它的设备也从面板和托盘消失)。
- **添加插件文件…**: 选一个 `.cs` 或 `.dll` 插件, 先检查能不能加载, 能用才复制到插件目录。
- **从模板新建…**: 起个英文名, 按模板生成插件文件并用 VS Code(没有就记事本)打开; 改好保存后点「重新加载」。
- **编辑 / 移除**: 只对自定义插件; 移除是把文件挪到插件目录下的 `_removed\`, 可以挪回来。
- **错误详情**(或双击出错的插件): 编译错误带行号, 运行出错带完整异常。
- **打开插件目录**: `%APPDATA%\DeviceBatteryViewer\plugins\`, 直接往里放文件再点「重新加载」也行。

## 写一个插件

插件是一个 `.cs` 文件, 程序加载时用系统自带的 C# 编译器编译, 不用装 Visual Studio。最简单的插件:

```csharp
using System.Collections.Generic;
using BatteryViewer;

public class MyMouse : BatteryPlugin
{
    public override string Name { get { return "我的鼠标"; } }

    public override List<Reading> Scan()
    {
        var result = new List<Reading>();
        foreach (var info in Hid.Enumerate(0x1234, null, 0xFF05))
        {
            using (var dev = new HidDevice(info.Path))
            {
                object level = Hid.Transact<object>(new[] { dev }, () => dev.Write(new byte[] { 0x08, 0x7D }),
                    d => d[1] == 0x72 ? (object)(int)d[7] : null, 500);
                result.Add(new Reading(info.Serial, info.Product, (int?)level) { Online = level != null, Kind = DeviceKind.Mouse });
            }
        }
        return result;
    }
}
```

完整示例见「从模板新建」生成的文件([res/PluginTemplate.cs](res/PluginTemplate.cs))。约定:

- 语法是 **C# 5**(系统自带编译器): 不能用 `$"..."`、`?.`、`=>` 写的属性。
- `Reading.Key` 在插件内唯一且稳定(序列号、蓝牙地址), 用来跨刷新对应同一台设备。
- `Level = null, Online = false` 表示设备不在线, 界面保留上次的电量。
- `Scan()` 在后台线程按刷新间隔调用, 可以阻塞; 抛异常只影响这个插件。
- 默认引用 System / System.Core / System.Drawing 和本程序; 需要别的程序集, 在文件里加一行 `// ref: System.Management.dll`。
- 也可以编译成 `.dll`(引用 `DeviceBatteryViewer.exe`, 目标 .NET Framework 4.x)放进插件目录。
- 和内置插件同 id(文件名)时覆盖内置的。

插件可用的接口:

| 类 | 用途 |
|---|---|
| `Hid` | `Enumerate(vid, pid, usagePage, usage)` 列 HID 接口; `Transact(设备, 发送, 匹配, 超时)` 发请求等第一个匹配的回复; `ReadAny()` 同时在多个接口上等报文 |
| `HidDevice` | 打开 HID 接口, `Write` / `Read` / `GetFeature` / `SetFeature` |
| `DevProp` | `List(枚举器, 属性键)` 列 PnP 设备节点并读属性(蓝牙插件用的就是它) |

## 编译

```bash
build.cmd
```

用 Windows 自带的 `csc.exe`(.NET Framework 4.x)编译, 输出 `bin\DeviceBatteryViewer.exe`。仓库在网络共享路径下 cmd 跑不了时, 用 Git Bash 运行 `sh build.sh`。

## 目录

| 路径 | 说明 |
|---|---|
| `src/Program.cs` | 入口、命令行、安装 / 卸载、单实例 |
| `src/AppController.cs` | 托盘图标和菜单, 协调窗口和后台轮询 |
| `src/MainForm.cs` | 设备面板 |
| `src/PluginsForm.cs` | 插件管理 |
| `src/PluginApi.cs` | 插件接口 `BatteryPlugin` / `Reading` |
| `src/PluginLoader.cs` | 加载内置插件和插件目录里的 .cs / .dll |
| `src/Poller.cs` | 后台线程: 加载插件、轮询、合并设备状态、持久化 |
| `src/Hid.cs` / `src/DevProp.cs` | HID 访问、PnP 设备属性 |
| `src/Settings.cs` | 配置、开机自启 |
| `src/Ui.cs` | DPI 缩放、配色、托盘数字图标 |
| `src/Plugins/` | 内置插件 |
| `res/PluginTemplate.cs` | 插件模板(嵌入 exe) |

## 隐私

不联网, 不写日志。只在 `%APPDATA%\DeviceBatteryViewer\` 下存配置、插件和上次读到的电量。

## 许可证

[MIT](LICENSE)

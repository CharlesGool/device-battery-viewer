# 可选插件

这里的插件不编进 exe, 用到哪个装哪个: 下载 `.cs` 文件, 在程序的「插件管理」里点「添加插件文件…」选它, 立即生效。

| 文件 | 设备 | 说明 |
|---|---|---|
| [vxe_mouse.cs](vxe_mouse.cs) | ATK / VXE 2.4G 无线鼠标(接收器 `373B:1216`) | 鼠标休眠时读不到, 动一下鼠标再刷新 |
| [darkrim_headset.cs](darkrim_headset.cs) | DARKRIM SHADOW 2.4G 耳机(杰理方案接收器, VID `3654`) | 耳机没开机时显示离线; 顺便读设备名和耳机固件版本 |

两个插件的协议来自 [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer) 公开的说明(原作者在真机上验证过), 这里的代码是按说明重写的, **还没有在真机上测试过**。
如果你有这些设备并且读不到, 欢迎提 issue, 附上程序 `--hid` 的输出。

想接入别的设备, 见主 README 的「写一个插件」。

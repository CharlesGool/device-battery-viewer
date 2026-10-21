#!/bin/sh
# 在 Git Bash 里编译(仓库在网络共享路径下、cmd 跑不了 build.cmd 时用)
cd "$(dirname "$0")" && mkdir -p bin && "$WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" -nologo -target:winexe -platform:anycpu -optimize+ -utf8output -codepage:65001 \
  -out:bin/DeviceBatteryViewer.exe -win32manifest:app.manifest "-resource:res/PluginTemplate.cs,PluginTemplate.cs" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll \
  'src\*.cs' 'src\Plugins\*.cs'

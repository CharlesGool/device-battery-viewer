@echo off
rem Builds bin\DeviceBatteryViewer.exe with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 ^
  /out:bin\DeviceBatteryViewer.exe /win32manifest:app.manifest ^
  /resource:res\PluginTemplate.cs,PluginTemplate.cs ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  src\*.cs src\Plugins\*.cs

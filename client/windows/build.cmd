@echo off
rem REQ-REMOTE-20261105-01
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:ToLink.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ToLink.cs VideoEncoder.cs ServerSettings.cs IncomingFiles.cs StreamingClipboard.cs DesktopBridge.cs
if errorlevel 1 exit /b %ERRORLEVEL%
"%CSC%" /nologo /target:winexe /main:ToLink.DesktopHost /platform:x64 /optimize+ /out:ToLink.DesktopHost.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll ToLink.cs VideoEncoder.cs ServerSettings.cs IncomingFiles.cs StreamingClipboard.cs DesktopBridge.cs DesktopHost.cs
exit /b %ERRORLEVEL%

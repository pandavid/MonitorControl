@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found - .NET Framework 4.x is required.
  pause
  exit /b 1
)
tasklist /FI "IMAGENAME eq MonitorSwitchTray.exe" 2>nul | find /I "MonitorSwitchTray.exe" >nul
if not errorlevel 1 (
  echo MonitorSwitchTray.exe is running - exit it from the panel or the tray menu first.
  pause
  exit /b 1
)
echo Using: %CSC%
echo Building MonitorSwitchTray.exe (panel + tray + gamma/modes/usage) ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"%~dp0..\MonitorSwitchTray.exe" "%~dp0MonitorSwitchTray.cs" "%~dp0MonitorPanel.cs" "%~dp0DisplayExtras.cs" "%~dp0Settings.cs"
if errorlevel 1 ( echo BUILD FAILED & pause & exit /b 1 )
echo.
echo Done. Run ..\MonitorSwitchTray.exe --panel-demo 10 to check the panel.
pause

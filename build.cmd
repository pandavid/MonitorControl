@echo off
rem 一键编译：托盘版 + 三个输入源切换小工具（用系统自带 csc.exe，无需 SDK）
cd /d "%~dp0"
call "%~dp0source\build.cmd"
if errorlevel 1 exit /b 1
call "%~dp0source\build_tray.cmd"
if errorlevel 1 exit /b 1
echo.
echo All binaries are in: %~dp0

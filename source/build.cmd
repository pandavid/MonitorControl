@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found - .NET Framework 4.x is required.
  pause
  exit /b 1
)
echo Using: %CSC%
for %%D in (DP1 HDMI1 HDMI2) do (
  echo Building Switch-%%D.exe ...
  "%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /define:%%D /out:"%~dp0..\Switch-%%D.exe" "%~dp0MonitorInputSwitch.cs"
  if errorlevel 1 ( echo BUILD FAILED for %%D & pause & exit /b 1 )
)
echo.
echo Done. Run ..\Switch-DP1.exe -c to verify.
pause

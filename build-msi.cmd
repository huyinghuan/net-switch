@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msi.ps1"
if errorlevel 1 (
  echo.
  echo MSI build failed. See the error above.
  pause
  exit /b 1
)
echo.
echo MSI build completed successfully.
pause

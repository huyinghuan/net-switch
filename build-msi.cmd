@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msi.ps1"
if errorlevel 1 (
  echo.
  echo Release build failed. See the error above.
  pause
  exit /b 1
)
echo.
echo MSI and standalone EXE build completed successfully.
pause

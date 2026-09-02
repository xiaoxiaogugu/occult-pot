@echo off
setlocal
cd /d "%~dp0"
dotnet build OccultPotPlugin.csproj -c Release
if errorlevel 1 exit /b 1
echo.
echo Build OK. Install with:
echo   powershell -File install-occult-pot.ps1
pause

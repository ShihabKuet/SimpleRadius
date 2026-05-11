@echo off
:: Simple Radius — Windows Service Installer
:: Run this script as Administrator

echo ============================================
echo   Simple Radius — Service Installer
echo ============================================
echo.

:: Check admin rights
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    echo Right-click install-service.bat and select "Run as administrator"
    pause
    exit /b 1
)

set SERVICE_NAME=SimpleRadius
set DISPLAY_NAME=Simple Radius RADIUS Server
set SERVICE_BIN=%~dp0SimpleRadius.Service.exe

:: Check if the exe exists
if not exist "%SERVICE_BIN%" (
    echo ERROR: SimpleRadius.Service.exe not found.
    echo Please build the project first:
    echo   dotnet publish SimpleRadius.Service -c Release -r win-x64 --self-contained
    pause
    exit /b 1
)

:: Stop and remove existing service if present
sc query %SERVICE_NAME% >nul 2>&1
if %errorlevel% equ 0 (
    echo Stopping existing service...
    sc stop %SERVICE_NAME% >nul 2>&1
    timeout /t 2 /nobreak >nul
    echo Removing existing service...
    sc delete %SERVICE_NAME% >nul 2>&1
    timeout /t 1 /nobreak >nul
)

:: Install the service
echo Installing service...
sc create "%SERVICE_NAME%" ^
   binPath= "\"%SERVICE_BIN%\"" ^
   DisplayName= "%DISPLAY_NAME%" ^
   start= auto ^
   obj= LocalSystem

if %errorlevel% neq 0 (
    echo ERROR: Failed to create service.
    pause
    exit /b 1
)

:: Set description
sc description "%SERVICE_NAME%" "Simple Radius RADIUS authentication and accounting server (RFC 2865/2866)"

:: Set recovery options — restart on failure
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/10000/restart/30000

:: Start the service
echo Starting service...
sc start "%SERVICE_NAME%"

if %errorlevel% equ 0 (
    echo.
    echo SUCCESS: Simple Radius service installed and started.
    echo.
    echo Useful commands:
    echo   sc start   SimpleRadius   — Start the service
    echo   sc stop    SimpleRadius   — Stop the service
    echo   sc query   SimpleRadius   — Check status
    echo   sc delete  SimpleRadius   — Uninstall the service
    echo.
    echo Logs: Windows Event Viewer ^> Windows Logs ^> Application
) else (
    echo WARNING: Service installed but failed to start.
    echo Check Windows Event Viewer for details.
)

pause

@echo off
setlocal enabledelayedexpansion
title NovaDownloader - Install Native Messaging Host

echo =======================================================
echo   NovaDownloader Chrome / Edge Native Host Installer
echo =======================================================
echo.

set "HOST_DIR=%~dp0"
set "HOST_EXE=%~dp0NovaDownloader.ExtensionHost.exe"
set "MANIFEST_PATH=%~dp0com.novadownloader.host.json"

echo Target Executable: "!HOST_EXE!"
echo Target Manifest:   "!MANIFEST_PATH!"
echo.

if not exist "!HOST_EXE!" (
    echo [ERROR] Native Host binary not found at "!HOST_EXE!"
    echo Please build the project first using: dotnet build
    pause
    exit /b 1
)

:: Run host with --install to create manifest and register keys
"!HOST_EXE!" --install

:: Also ensure direct registry registration for Chrome and Edge
echo.
echo Registering Native Messaging Host for Google Chrome...
reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.novadownloader.host" /ve /t REG_SZ /d "!MANIFEST_PATH!" /f >nul
if %ERRORLEVEL% equ 0 (
    echo [OK] Chrome registered successfully.
) else (
    echo [WARN] Failed to write Chrome registry key.
)

echo Registering Native Messaging Host for Microsoft Edge...
reg add "HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.novadownloader.host" /ve /t REG_SZ /d "!MANIFEST_PATHExternal:!" /f >nul
reg add "HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.novadownloader.host" /ve /t REG_SZ /d "!MANIFEST_PATH!" /f >nul
if %ERRORLEVEL% equ 0 (
    echo [OK] Edge registered successfully.
) else (
    echo [WARN] Failed to write Edge registry key.
)

echo Registering Native Messaging Host for Brave Browser...
reg add "HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.novadownloader.host" /ve /t REG_SZ /d "!MANIFEST_PATH!" /f >nul

echo.
echo =======================================================
echo [SUCCESS] NovaDownloader Native Messaging Host installed!
echo You can now reload the Chrome/Edge extension.
echo =======================================================
echo.
pause

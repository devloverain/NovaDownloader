@echo off
setlocal
title NovaDownloader - Uninstall Native Messaging Host

echo =======================================================
echo   NovaDownloader Chrome / Edge Native Host Uninstaller
echo =======================================================
echo.

set "HOST_EXE=%~dp0NovaDownloader.ExtensionHost.exe"

if exist "%HOST_EXE%" (
    "%HOST_EXE%" --uninstall
)

echo Removing Chrome registration...
reg delete "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.novadownloader.host" /f >nul 2>&1
echo Removing Edge registration...
reg delete "HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.novadownloader.host" /f >nul 2>&1
echo Removing Opera registration...
reg delete "HKCU\Software\Opera Software\NativeMessagingHosts\com.novadownloader.host" /f >nul 2>&1
reg delete "HKCU\Software\Opera Software\Opera Stable\NativeMessagingHosts\com.novadownloader.host" /f >nul 2>&1
reg delete "HKCU\Software\Opera Software\Opera GX Stable\NativeMessagingHosts\com.novadownloader.host" /f >nul 2>&1
echo Removing Brave registration...
reg delete "HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.novadownloader.host" /f >nul 2>&1

echo.
echo =======================================================
echo [SUCCESS] NovaDownloader Native Host uninstalled.
echo =======================================================
echo.
pause

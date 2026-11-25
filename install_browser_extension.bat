@echo off
title NovaDownloader Browser Extension Setup
echo ========================================================
echo   Registering NovaDownloader Native Host in Registry
echo ========================================================
cd /d "%~dp0"
dotnet run --project src\NovaDownloader.ExtensionHost\NovaDownloader.ExtensionHost.csproj -- --install
echo.
echo ========================================================
echo Next Step: Load Extension in Google Chrome or Microsoft Edge
echo ========================================================
echo 1. Open your browser and go to: chrome://extensions or edge://extensions
echo 2. Enable "Developer Mode" (toggle at top right)
echo 3. Click "Load unpacked"
echo 4. Select folder: %~dp0extension
echo.
pause

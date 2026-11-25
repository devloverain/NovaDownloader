@echo off
title NovaDownloader Launcher
echo ========================================================
echo   Launching NovaDownloader (.NET 10 / C# 13)
echo ========================================================
cd /d "%~dp0"
dotnet run --project src\NovaDownloader.UI\NovaDownloader.UI.csproj

@echo off
rem Double-click to build the portable ClaudeUsageTray.exe (one file, no .NET needed to run it).
rem Needs the .NET 10 SDK only to build. The result opens in Explorer when done.
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 10 SDK is needed to build. Get it from https://dotnet.microsoft.com/download
  pause
  exit /b 1
)

echo Building the portable ClaudeUsageTray.exe ...
dotnet publish src\ClaudeUsageTray -p:PublishProfile=win-x64 --nologo -v quiet
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

echo.
echo Done: %~dp0artifacts\publish\ClaudeUsageTray.exe
echo Copy it anywhere you like ^(for example Documents^) and double-click it.
echo On first run it offers Start menu and desktop shortcuts.
explorer /select,"%~dp0artifacts\publish\ClaudeUsageTray.exe"

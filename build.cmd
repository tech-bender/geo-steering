@echo off
rem Builds both parts of GordonGeoSteer:
rem   1. tools\PzsImporter  (.NET Framework 3.5, x86, csc.exe)  -> tools\PzsImporter\bin\PzsImporter.exe
rem   2. src\GordonGeoSteer (.NET 8 WPF)                        -> src\GordonGeoSteer\bin\<Config>\net8.0-windows\
rem The importer is copied next to the app (PzsImporter\PzsImporter.exe) by the app build.
rem Usage: build.cmd [Release|Debug]   (default Release)
setlocal
set ROOT=%~dp0
set CFG=%1
if "%CFG%"=="" set CFG=Release

call "%ROOT%tools\PzsImporter\build.cmd" || exit /b 1

rem Prefer a repo-local SDK (.dotnet\, see scripts\get-dotnet-sdk.ps1), else dotnet on PATH.
set DOTNET=%ROOT%.dotnet\dotnet.exe
if not exist "%DOTNET%" set DOTNET=dotnet
"%DOTNET%" --list-sdks 2>nul | findstr /b "8. 9. 10." >nul
if errorlevel 1 (
  echo ERROR: no .NET 8+ SDK found. Install one, or run:
  echo   powershell -ExecutionPolicy Bypass -File "%ROOT%scripts\get-dotnet-sdk.ps1"
  echo which installs the .NET 8 SDK into %ROOT%.dotnet\ ^(no admin rights needed^).
  exit /b 1
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
"%DOTNET%" build "%ROOT%GordonGeoSteer.sln" -c %CFG% || (echo GordonGeoSteer build FAILED & exit /b 1)
echo.
echo Built: %ROOT%src\GordonGeoSteer\bin\%CFG%\net8.0-windows\GordonGeoSteer.exe
exit /b 0

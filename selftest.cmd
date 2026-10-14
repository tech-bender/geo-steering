@echo off
rem End-to-end self-test: import a real PZS project folder through the app, visit every view,
rem write a view-model dump + screenshots, then (if it is the Bakken sample) verify known values.
rem Usage: selftest.cmd "<PZS project folder>" [reportDir]     (set GG_CONFIG=Debug to test a Debug build)
rem Also renders the steering view (ui_steer_*.png), checks zoom/workspace behaviour and writes engine-validation.txt.
setlocal
set ROOT=%~dp0
if "%~1"=="" (echo usage: selftest.cmd "<PZS project folder>" [reportDir] & exit /b 2)
set OUT=%~2
if "%OUT%"=="" set OUT=%ROOT%selftest-output
set CFG=%GG_CONFIG%
if "%CFG%"=="" set CFG=Release
set EXE=%ROOT%src\GordonGeoSteer\bin\%CFG%\net8.0-windows\GordonGeoSteer.exe
if not exist "%EXE%" (echo Build first: build.cmd & exit /b 1)
if exist "%OUT%" rmdir /s /q "%OUT%"
start "" /wait "%EXE%" --selftest --import "%~1" --out "%OUT%\import" --report "%OUT%\report"
set RC=%ERRORLEVEL%
type "%OUT%\report\selftest.log"
if not "%RC%"=="0" (echo SELFTEST FAILED, exit %RC% & exit /b %RC%)
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tests\Verify-BakkenDump.ps1" -Dump "%OUT%\report\viewmodel-dump.txt"
exit /b %ERRORLEVEL%

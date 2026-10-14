@echo off
rem Builds tools\PzsImporter\bin\PzsImporter.exe with the .NET Framework 3.5 C# compiler (CLR 2.0, x86).
rem x86 + CLR 2.0 are required: the importer loads PZS 5.2.0's own 32-bit .NET 2.0 assemblies to read
rem .pzscfg files (BinaryFormatter), and uses the 32-bit Jet ODBC driver to read .pzsdb files.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v3.5\csc.exe
if not exist "%CSC%" (
  echo ERROR: %CSC% not found. Enable ".NET Framework 3.5 (includes .NET 2.0 and 3.0)" in Windows Features.
  exit /b 1
)
set HERE=%~dp0
if not exist "%HERE%bin" mkdir "%HERE%bin"
"%CSC%" /nologo /platform:x86 /target:exe /optimize+ /out:"%HERE%bin\PzsImporter.exe" ^
  /r:System.Drawing.dll /r:System.Data.dll /r:System.Core.dll "%HERE%PzsImporter.cs"
if errorlevel 1 (
  echo PzsImporter build FAILED
  exit /b 1
)
echo Built %HERE%bin\PzsImporter.exe
exit /b 0

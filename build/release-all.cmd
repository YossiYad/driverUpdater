@echo off
setlocal

set VERSION=%~1
if "%VERSION%"=="" set VERSION=0.1.4

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test.ps1" -Configuration Release -ResultsDirectory "%~dp0..\artifacts\test-results\release-all" -BaseOutputPath "%~dp0..\artifacts\validation\bin" || exit /b 1
dotnet list "%~dp0..\DriverUpdater.slnx" package --vulnerable --include-transitive || exit /b 1

set SKIP_RELEASE_VALIDATION=1
call "%~dp0release.cmd" %VERSION% win-x64 || exit /b 1
call "%~dp0release.cmd" %VERSION% win-arm64 || exit /b 1
call "%~dp0release.cmd" %VERSION% win-x86 || exit /b 1

echo.
echo All release packages completed successfully.
exit /b 0

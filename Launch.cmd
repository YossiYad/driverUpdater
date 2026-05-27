@echo off
setlocal
set REPO_ROOT=%~dp0
set APP_EXE=%REPO_ROOT%src\DriverUpdater.App\bin\Debug\net10.0-windows\DriverUpdater.exe

if not exist "%APP_EXE%" (
    echo Build the solution first: dotnet build
    exit /b 1
)

powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%APP_EXE%'"
endlocal

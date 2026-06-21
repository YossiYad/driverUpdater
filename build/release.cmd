@echo off
setlocal enabledelayedexpansion

set REPO_ROOT=%~dp0..
set CONFIG=Release
set RID=win-x64
set APP_PROJECT=%REPO_ROOT%\src\DriverUpdater.App\DriverUpdater.App.csproj
set PUBLISH_DIR=%REPO_ROOT%\src\DriverUpdater.App\bin\%CONFIG%\net10.0-windows\%RID%\publish
set OUTPUT_DIR=%REPO_ROOT%\build\output
set VERSION=%~1
if "%VERSION%"=="" set VERSION=0.1.0

echo.
echo === DriverUpdater release ===
echo Version: %VERSION%
echo Configuration: %CONFIG%
echo Runtime: %RID%
echo.

echo Restoring solution...
dotnet restore "%REPO_ROOT%\DriverUpdater.slnx" || goto :error

echo Publishing app...
dotnet publish "%APP_PROJECT%" -c %CONFIG% -r %RID% --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true || goto :error

echo Ensuring vpk tool...
dotnet tool restore || goto :error

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

echo Packaging with Velopack...
dotnet vpk pack ^
    --packId DriverUpdater ^
    --packTitle DriverUpdater ^
    --packAuthors "Yossi Yadgar" ^
    --packVersion %VERSION% ^
    --packDir "%PUBLISH_DIR%" ^
    --mainExe DriverUpdater.exe ^
    --icon "%REPO_ROOT%\src\DriverUpdater.App\Assets\app.ico" ^
    --outputDir "%OUTPUT_DIR%" ^
    --runtime %RID% || goto :error

echo.
echo Release artifacts in %OUTPUT_DIR%
exit /b 0

:error
echo.
echo Release failed.
exit /b 1

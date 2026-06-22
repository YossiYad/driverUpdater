@echo off
setlocal

set REPO_ROOT=%~dp0..
set CONFIG=Release
set RID=%~2
if "%RID%"=="" set RID=win-x64
set APP_PROJECT=%REPO_ROOT%\src\DriverUpdater.App\DriverUpdater.App.csproj
set PUBLISH_DIR=%REPO_ROOT%\src\DriverUpdater.App\bin\%CONFIG%\net10.0-windows\%RID%\publish
set OUTPUT_DIR=%REPO_ROOT%\build\output\%RID%
set VERSION=%~1
if "%VERSION%"=="" set VERSION=0.1.4
set SIGN_ARGS=
if defined VELOPACK_SIGN_PARAMS set SIGN_ARGS=--signParams "%VELOPACK_SIGN_PARAMS%"

echo.
echo === DriverUpdater release ===
echo Version: %VERSION%
echo Configuration: %CONFIG%
echo Runtime: %RID%
echo.

if not defined SKIP_RELEASE_VALIDATION (
    echo Running release validation...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%\build\test.ps1" -Configuration %CONFIG% -ResultsDirectory "%REPO_ROOT%\artifacts\test-results\release-%RID%" -BaseOutputPath "%REPO_ROOT%\artifacts\validation\bin" || goto :error
    dotnet list "%REPO_ROOT%\DriverUpdater.slnx" package --vulnerable --include-transitive || goto :error
)

echo Restoring solution...
dotnet restore "%REPO_ROOT%\DriverUpdater.slnx" || goto :error

echo Publishing app...
dotnet publish "%APP_PROJECT%" -c %CONFIG% -r %RID% --self-contained true -p:PublishSingleFile=false || goto :error

echo Ensuring vpk tool...
dotnet tool restore || goto :error

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

echo Packaging with Velopack...
dotnet tool run vpk pack ^
    --packId DriverUpdater ^
    --packTitle DriverUpdater ^
    --packAuthors "Yossi Yadgar" ^
    --packVersion %VERSION% ^
    --packDir "%PUBLISH_DIR%" ^
    --mainExe DriverUpdater.exe ^
    --icon "%REPO_ROOT%\src\DriverUpdater.App\Assets\app.ico" ^
    --outputDir "%OUTPUT_DIR%" ^
    --runtime %RID% ^
    %SIGN_ARGS% || goto :error

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%\build\write-checksums.ps1" -Directory "%OUTPUT_DIR%" || goto :error

echo.
echo Release artifacts in %OUTPUT_DIR%
exit /b 0

:error
echo.
echo Release failed.
exit /b 1

@echo off
setlocal
echo ========================================================
echo Building Monitor Layout Switcher (Release Standalone)
echo ========================================================
cd /d "%~dp0\src"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0\dist"
if %ERRORLEVEL% equ 0 (
    echo.
    echo Build Successful! Binary published to:
    echo %~dp0dist\MonitorLayoutSwitcher.exe
) else (
    echo.
    echo Build Failed!
)
endlocal

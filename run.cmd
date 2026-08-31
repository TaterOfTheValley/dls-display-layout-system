@echo off
setlocal
if exist "%~dp0dist\MonitorLayoutSwitcher.exe" (
    start "" "%~dp0dist\MonitorLayoutSwitcher.exe"
) else (
    echo Building first...
    call "%~dp0build.cmd"
    if exist "%~dp0dist\MonitorLayoutSwitcher.exe" (
        start "" "%~dp0dist\MonitorLayoutSwitcher.exe"
    )
)
endlocal

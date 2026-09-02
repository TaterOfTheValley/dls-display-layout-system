@echo off
setlocal
if exist "%~dp0dist\DLS.exe" (
    start "" "%~dp0dist\DLS.exe"
) else (
    echo Building first...
    call "%~dp0build.cmd"
    if exist "%~dp0dist\DLS.exe" (
        start "" "%~dp0dist\DLS.exe"
    )
)
endlocal

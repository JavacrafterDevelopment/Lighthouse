@echo off
setlocal EnableExtensions

rem ---------------------------------------------------------------------------
rem  Builds the portable Lighthouse folder.
rem
rem  Just run:  build.bat            (or double-click it)
rem  Optional:  build.bat -Zip       to also produce a distributable archive
rem
rem  This is only a wrapper: the actual work lives in build.ps1. It exists so you
rem  never have to type the execution-policy incantation by hand.
rem ---------------------------------------------------------------------------

rem Double-clicking from Explorer closes the window the moment the build ends, so
rem hold it open in that case only. Typed at a prompt it stays quiet. Set
rem LIGHTHOUSE_NOPAUSE=1 to never wait, which matters if a script calls this.
set "_hold="
echo %cmdcmdline% | find /i "%~nx0" >nul 2>&1 && set "_hold=1"
if defined LIGHTHOUSE_NOPAUSE set "_hold="

if not exist "%~dp0build.ps1" (
    echo build.ps1 was not found next to this file - are they still together?
    set "_code=1"
    goto :finish
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "_code=%ERRORLEVEL%"

if not "%_code%"=="0" (
    echo.
    echo   Build failed ^(exit code %_code%^).
    echo.
)

:finish
if defined _hold pause
exit /b %_code%

@echo off
setlocal
title CrxShield Driver Stopper

:: ============================================================
::  Stop-CrxShield.cmd
::  Stops the CrxShield kernel driver service.
::  Use Restart-CrxShield.cmd instead if you rebuilt the driver
::  and want the new image loaded.
:: ============================================================

:: --- Self-elevate (UAC prompt) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo   CrxShield driver stopper
echo ============================================
echo.

sc query CrxShield >nul 2>&1
if %errorlevel%==1060 (
    echo [info] CrxShield service is not installed. Nothing to stop.
    echo.
    pause
    exit /b 0
)

echo [step 1] Stopping CrxShield service...
echo.
sc stop CrxShield
set RC=%errorlevel%

if %RC%==0 goto stopped
if %RC%==1062 (
    echo.
    echo [info] Driver was not running. Nothing to do.
    echo.
    pause
    exit /b 0
)

echo.
echo --------------------------------------------
echo  Stop failed with error %RC%.
echo  If a process ^(CrxMem, verifier^) holds a handle
echo  to \\.\CrxShield, close it and try again.
echo --------------------------------------------
echo.
pause
exit /b 1

:stopped
echo.
echo [step 2] Waiting for driver to unload...
timeout /t 2 /nobreak >nul

sc query CrxShield | find "STOPPED" >nul
if %errorlevel%==0 (
    echo [ ok ] CrxShield driver is STOPPED and unloaded.
) else (
    echo [warn] Stop was accepted but driver may still be unloading:
    sc query CrxShield
)
echo.
pause
exit /b 0

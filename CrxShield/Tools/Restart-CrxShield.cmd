@echo off
setlocal
title CrxShield Driver Reloader

:: ============================================================
::  Restart-CrxShield.cmd
::  One-click reload: stops the running driver (if any) and
::  starts it again. Use this AFTER REBUILDING CrxShield.sys
::  so the kernel loads the new image - no reboot needed
::  (testsigning must already be on).
::
::  Also safe as a generic "make sure the driver is running"
::  button: if not installed, it tells you to run
::  Install-CrxShieldDriver.ps1 once.
:: ============================================================

:: --- Self-elevate (UAC prompt) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo   CrxShield driver reload ^(stop + start^)
echo ============================================
echo.

sc query CrxShield >nul 2>&1
if %errorlevel%==1060 (
    echo [fail] CrxShield service is not installed.
    echo        Run Tools\Install-CrxShieldDriver.ps1 once first.
    echo.
    pause
    exit /b 1
)

:: --- Stop if running ---
sc query CrxShield | find "RUNNING" >nul
if %errorlevel%==0 (
    echo [step 1] Driver is running - stopping old image...
    sc stop CrxShield >nul
    if %errorlevel% neq 0 (
        echo.
        echo [fail] Could not stop the driver.
        echo        Close CrxMem / the verifier ^(anything holding \\.\CrxShield^)
        echo        and run this again.
        echo.
        pause
        exit /b 1
    )
    timeout /t 2 /nobreak >nul
    echo [ ok ] Old image unloaded.
) else (
    echo [step 1] Driver not running - nothing to stop.
)
echo.

:: --- Start ---
echo [step 2] Starting CrxShield with current image...
echo.
sc start CrxShield
set RC=%errorlevel%

if %RC%==0 goto success
if %RC%==1056 goto success

echo.
echo --------------------------------------------
echo  Start failed with error %RC%.
if %RC%==577 (
    echo  577 = kernel Code Integrity rejected the signature.
    echo  Run Start-CrxShield.cmd - it can enable testsigning
    echo  and reboot for you.
)
echo --------------------------------------------
echo.
pause
exit /b 1

:success
echo.
echo [step 3] Confirming driver state...
sc query CrxShield | find "RUNNING" >nul
if %errorlevel%==0 (
    echo [ ok ] CrxShield is RUNNING with the latest image.
    echo        You can now start CrxMem and use kernel mode.
) else (
    echo [warn] Start returned success but state looks odd:
    sc query CrxShield
)
echo.
pause
exit /b 0
